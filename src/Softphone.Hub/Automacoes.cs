using System.Text.Json;
using Softphone.Contratos;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;

namespace Softphone.Hub;

/// <summary>
/// 1.3 (Claude Code, 10/11/2026): automações do chat. Regras no painel (gatilho + ação), executadas pelo Hub. Salvaguardas:
/// (1) cada regra roda no máximo UMA vez por evento (tabela automacoes_exec, chave = evento), mesmo se o evento chegar repetido;
/// (2) mensagens do próprio assistente nunca disparam regra (sem laço); (3) limite de 30 execuções por regra por hora;
/// (4) o texto automático só leva variáveis simples ({nome}, {numero}), sem dados de outras conversas.
/// </summary>
public sealed class Automacoes
{
    public const string LoginAssistente = "softphone.assistente";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly Banco _banco;
    private readonly IHubContext<ChatHub> _hub;
    private readonly ILogger<Automacoes> _log;

    public Automacoes(Banco banco, IHubContext<ChatHub> hub, ILogger<Automacoes> log) { _banco = banco; _hub = hub; _log = log; }

    public List<RegraAutomacao> Regras()
    {
        var j = _banco.LerConfig("automacoes.regras");
        return string.IsNullOrEmpty(j) ? new() : JsonSerializer.Deserialize<List<RegraAutomacao>>(j, Json) ?? new();
    }

    public string? Gravar(IEnumerable<RegraAutomacao> regras)
    {
        var lista = new List<RegraAutomacao>();
        foreach (var r in regras)
        {
            if (string.IsNullOrWhiteSpace(r.Nome) || r.Nome.Length > 80) return "Dê um nome à regra (até 80 caracteres).";
            if (r.Gatilho is not ("palavra" or "chamada_perdida" or "boas_vindas")) return $"Gatilho inválido em \"{r.Nome}\".";
            if (r.Acao is not ("responder" or "notificar" or "encaminhar")) return $"Ação inválida em \"{r.Nome}\".";
            if (r.Gatilho == "palavra" && (r.Palavras ?? []).All(string.IsNullOrWhiteSpace)) return $"\"{r.Nome}\": informe ao menos uma palavra-chave.";
            if (r.Gatilho != "palavra" && r.Acao != "notificar") return $"\"{r.Nome}\": esse gatilho só combina com a ação \"notificar\".";
            if (r.Acao == "encaminhar" && (string.IsNullOrWhiteSpace(r.Destino) || _banco.ObterPessoa(Diretorio.NormalizarLogin(r.Destino)) is null)) return $"\"{r.Nome}\": o destino não está no diretório.";
            if (r.Acao != "encaminhar" && (string.IsNullOrWhiteSpace(r.Texto) || r.Texto.Length > 1000)) return $"\"{r.Nome}\": escreva o texto da mensagem (até 1000 caracteres).";
            if (!string.IsNullOrWhiteSpace(r.Responsavel) && _banco.ObterPessoa(Diretorio.NormalizarLogin(r.Responsavel)) is null) return $"\"{r.Nome}\": responsável fora do diretório.";
            lista.Add(r with
            {
                Id = string.IsNullOrWhiteSpace(r.Id) ? Guid.NewGuid().ToString("N")[..10] : r.Id,
                Palavras = (r.Palavras ?? []).Select(p => p.Trim()).Where(p => p.Length > 0).Take(20).ToArray(),
                Setores = (r.Setores ?? []).Select(p => p.Trim()).Where(p => p.Length > 0).ToArray(),
                Destino = string.IsNullOrWhiteSpace(r.Destino) ? null : Diretorio.NormalizarLogin(r.Destino),
                Responsavel = string.IsNullOrWhiteSpace(r.Responsavel) ? null : Diretorio.NormalizarLogin(r.Responsavel),
            });
        }
        if (lista.Count > 50) return "Regras demais (máximo 50).";
        _banco.GravarConfig("automacoes.regras", JsonSerializer.Serialize(lista, Json));
        return null;
    }

    /// <summary>Marca a execução (regra + evento). false = esta regra já rodou para este evento: não faz de novo.</summary>
    private bool Reservar(string regra, string chave)
    {
        using var c = new SqliteConnection(_banco.CadeiaDeConexao);
        c.Open();
        using (var ins = c.CreateCommand())
        {
            ins.CommandText = "INSERT OR IGNORE INTO automacoes_exec(regra, chave, em) VALUES($r, $k, $e)";
            ins.Parameters.AddWithValue("$r", regra); ins.Parameters.AddWithValue("$k", chave); ins.Parameters.AddWithValue("$e", DateTime.UtcNow.ToString("O"));
            ins.ExecuteNonQuery();
        }
        using (var ch = c.CreateCommand())
        {
            ch.CommandText = "SELECT changes()";   // 0 = esta regra já rodou para este evento
            if (Convert.ToInt64(ch.ExecuteScalar()) == 0) return false;
        }
        using var cnt = c.CreateCommand();
        cnt.CommandText = "SELECT COUNT(*) FROM automacoes_exec WHERE regra=$r AND em > $h";
        cnt.Parameters.AddWithValue("$r", regra); cnt.Parameters.AddWithValue("$h", DateTime.UtcNow.AddHours(-1).ToString("O"));
        if (Convert.ToInt64(cnt.ExecuteScalar()) > 30) { _log.LogWarning("Automação {Regra}: limite de 30 execuções por hora; ignorada", regra); return false; }
        return true;
    }

    public List<ExecucaoAutomacao> Execucoes(int limite = 100)
    {
        var nomes = Regras().ToDictionary(r => r.Id, r => r.Nome);
        var l = new List<ExecucaoAutomacao>();
        using var c = new SqliteConnection(_banco.CadeiaDeConexao);
        c.Open();
        using (var criar = c.CreateCommand()) { criar.CommandText = "CREATE TABLE IF NOT EXISTS automacoes_exec(regra TEXT NOT NULL, chave TEXT NOT NULL, em TEXT NOT NULL, PRIMARY KEY(regra, chave))"; criar.ExecuteNonQuery(); }
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT regra, chave, em FROM automacoes_exec ORDER BY em DESC LIMIT $l";
        cmd.Parameters.AddWithValue("$l", Math.Clamp(limite, 1, 500));
        using var rd = cmd.ExecuteReader();
        while (rd.Read()) l.Add(new(rd.GetString(0), nomes.GetValueOrDefault(rd.GetString(0), "(regra removida)"), rd.GetString(1), DateTime.Parse(rd.GetString(2), null, System.Globalization.DateTimeStyles.RoundtripKind)));
        return l;
    }

    private static string Preencher(string texto, string? nome, string? numero) =>
        (texto ?? "").Replace("{nome}", nome ?? "", StringComparison.OrdinalIgnoreCase).Replace("{numero}", numero ?? "", StringComparison.OrdinalIgnoreCase);

    private bool Abrange(RegraAutomacao r, string login) =>
        r.Setores.Length == 0 || (_banco.ObterPessoa(login)?.Setor is { } s && r.Setores.Contains(s, StringComparer.OrdinalIgnoreCase));

    private async Task EnviarComoAssistenteAsync(long conversa, string texto)
    {
        var msg = _banco.Inserir(conversa, LoginAssistente, texto, null);
        foreach (var m in _banco.Membros(conversa)) await _hub.Clients.Group("u:" + m.ToLowerInvariant()).SendAsync(HubNomes.EvMensagem, msg);
    }

    private async Task NotificarAsync(string login, string texto)
    {
        _banco.GarantirPessoa(LoginAssistente, "Softphone (automático)", "TI");
        var id = _banco.ObterOuCriarDireta(LoginAssistente, login);
        var conv = _banco.Conversa(id, login);
        if (conv != null) await _hub.Clients.Group("u:" + login.ToLowerInvariant()).SendAsync(HubNomes.EvConversa, conv);
        await EnviarComoAssistenteAsync(id, texto);
    }

    /// <summary>Mensagem nova numa conversa (qualquer origem). Regras de palavra-chave.</summary>
    public async Task AoMensagemAsync(MensagemDto msg)
    {
        try
        {
            if (string.Equals(msg.Autor, LoginAssistente, StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(msg.Texto)) return;   // o assistente nunca dispara regra
            var texto = BuscaTexto(msg.Texto);
            _log.LogInformation("Automação: avaliando a mensagem {Id} contra {N} regra(s) ligada(s)", msg.Id, Regras().Count(x => x.Ligada));
            foreach (var r in Regras().Where(r => r.Ligada && r.Gatilho == "palavra"))
            {
                if (!Abrange(r, msg.Autor) || !r.Palavras.Any(p => texto.Contains(BuscaTexto(p), StringComparison.Ordinal))) continue;
                if (!Reservar(r.Id, "msg:" + msg.Id)) continue;
                var autor = _banco.ObterPessoa(msg.Autor);
                _banco.GarantirPessoa(LoginAssistente, "Softphone (automático)", "TI");
                if (r.Acao == "responder") await EnviarComoAssistenteAsync(msg.Conversa, Preencher(r.Texto, autor?.Nome, null));
                else if (r.Acao == "notificar") await NotificarAsync(msg.Autor, Preencher(r.Texto, autor?.Nome, null));
                else if (r.Acao == "encaminhar" && r.Destino != null)
                    await NotificarAsync(r.Destino, $"Encaminhado automaticamente ({r.Nome}) de {autor?.Nome ?? msg.Autor}: {msg.Texto}");
                _log.LogInformation("Automação \"{Nome}\" ({Acao}) executada para a mensagem {Id}", r.Nome, r.Acao, msg.Id);
            }
        }
        catch (Exception ex) { _log.LogWarning(ex, "Automação de mensagem falhou (a mensagem já foi entregue)"); }
    }

    public async Task AoChamadaPerdidaAsync(string login, string chamadaId, string numero)
    {
        try
        {
            foreach (var r in Regras().Where(r => r.Ligada && r.Gatilho == "chamada_perdida"))
            {
                if (!Abrange(r, login) || !Reservar(r.Id, $"chamada:{chamadaId}:{login}")) continue;
                var p = _banco.ObterPessoa(login);
                await NotificarAsync(login, Preencher(r.Texto, p?.Nome, numero));
            }
        }
        catch (Exception ex) { _log.LogWarning(ex, "Automação de chamada perdida falhou"); }
    }

    public async Task AoPrimeiroAcessoAsync(string login)
    {
        try
        {
            foreach (var r in Regras().Where(r => r.Ligada && r.Gatilho == "boas_vindas"))
            {
                if (!Abrange(r, login) || !Reservar(r.Id, "boas:" + login)) continue;
                await NotificarAsync(login, Preencher(r.Texto, _banco.ObterPessoa(login)?.Nome, null));
            }
        }
        catch (Exception ex) { _log.LogWarning(ex, "Automação de boas-vindas falhou"); }
    }

    private static string BuscaTexto(string s) => SoftphoneBusca.SemAcento(s);
}

/// <summary>Minúsculas e sem acento (o Hub não referencia o Core, então fica aqui).</summary>
internal static class SoftphoneBusca
{
    public static string SemAcento(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var d = s.Normalize(System.Text.NormalizationForm.FormD);
        var sb = new System.Text.StringBuilder(d.Length);
        foreach (var ch in d) if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch) != System.Globalization.UnicodeCategory.NonSpacingMark) sb.Append(ch);
        return sb.ToString().ToLowerInvariant();
    }
}
