using System.Text.Json;
using Softphone.Contratos;

namespace Softphone.Hub;

/// <summary>
/// 1.3 (Claude Code, 10/11/2026): matriz de acesso grupo do AD -> perfil -> setores. Perfis: Administrador (grupos de administradores,
/// já existentes), Supervisor (envia comunicado e edita artigos só do(s) setor(es) dele), Atendente (padrão: consulta e anota).
/// Os grupos são lidos do AD com a sincronização (a cada 30 min ou "Sincronizar agora"): quem sai do grupo perde o perfil na leitura
/// seguinte, mesmo com sessão aberta, porque a regra é conferida no servidor a cada pedido. Membros de grupos aninhados valem (o AD
/// expande). Várias regras somam; vale o perfil mais alto (Administrador > Supervisor > Atendente). Falha ao ler um grupo mantém os
/// membros da última leitura boa (como GruposAdmin), nunca tira o acesso de todos.
/// </summary>
public sealed class AcessoPerfis
{
    public const string Administrador = "administrador", Supervisor = "supervisor", Atendente = "atendente";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly Banco _banco;
    private readonly GruposAdmin _grupos;
    private readonly OpcoesHub _op;
    private readonly ILogger<AcessoPerfis> _log;
    private readonly object _trava = new();
    private Dictionary<string, HashSet<string>> _membros = new(StringComparer.OrdinalIgnoreCase);   // grupo -> logins (última leitura boa)
    private DateTime _lidoEm = DateTime.MinValue;

    public AcessoPerfis(Banco banco, GruposAdmin grupos, OpcoesHub op, ILogger<AcessoPerfis> log) { _banco = banco; _grupos = grupos; _op = op; _log = log; }

    public List<RegraAcessoDto> Regras()
    {
        var j = _banco.LerConfig("acesso.regras");
        return string.IsNullOrEmpty(j) ? new() : JsonSerializer.Deserialize<List<RegraAcessoDto>>(j, Json) ?? new();
    }

    /// <summary>Valida e grava as regras. Erro = texto para o painel (nada é gravado).</summary>
    public string? Gravar(IEnumerable<RegraAcessoDto> regras)
    {
        var lista = new List<RegraAcessoDto>();
        foreach (var r in regras)
        {
            var perfil = (r.Perfil ?? "").Trim().ToLowerInvariant();
            if (perfil is not (Supervisor or Atendente)) return $"Perfil inválido em \"{r.Grupo}\": use supervisor ou atendente (administradores ficam na lista própria).";
            var grupo = (r.Grupo ?? "").Trim();
            if (grupo.Length is 0 or > 256) return "Informe o grupo do AD (até 256 caracteres).";
            var (logins, erro) = _grupos.Ler(grupo);
            if (logins == null) return $"Grupo {grupo}: {erro}.";
            var setores = (r.Setores ?? []).Select(s => s.Trim()).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (setores.Any(s => s.Length > 80)) return "Nome de setor grande demais.";
            lista.Add(new RegraAcessoDto(grupo, perfil, setores));
        }
        if (lista.Count > 100) return "Regras demais (máximo 100).";
        _banco.GravarConfig("acesso.regras", JsonSerializer.Serialize(lista, Json));
        Atualizar();
        return null;
    }

    /// <summary>Relê os membros de todos os grupos das regras (chamado na sincronização do AD e quando as regras mudam).</summary>
    public void Atualizar()
    {
        var novo = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in Regras().Select(r => r.Grupo).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var (logins, erro) = _grupos.Ler(g);
            if (logins != null) novo[g] = new HashSet<string>(logins.Select(Diretorio.NormalizarLogin), StringComparer.OrdinalIgnoreCase);
            else
            {
                lock (_trava) { if (_membros.TryGetValue(g, out var velho)) novo[g] = velho; }   // mantém a última leitura boa
                _log.LogWarning("Acesso: grupo {Grupo} não lido ({Erro}); mantida a última leitura", g, erro);
            }
        }
        lock (_trava) { _membros = novo; _lidoEm = DateTime.UtcNow; }
    }

    private void Garantir()
    {
        bool velho; lock (_trava) velho = DateTime.UtcNow - _lidoEm > TimeSpan.FromMinutes(30);
        if (velho) { try { Atualizar(); } catch (Exception ex) { _log.LogWarning(ex, "Acesso: leitura dos grupos falhou"); } }
    }

    /// <summary>Perfil efetivo, setores de escopo (para supervisor) e os grupos que deram o acesso.</summary>
    public PerfilEfetivoDto De(string login)
    {
        login = Diretorio.NormalizarLogin(login);
        if (_op.EhAdmin(login)) return new(login, Administrador, [], []);
        Garantir();
        var setorProprio = _banco.ObterPessoa(login)?.Setor;
        var setores = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var grupos = new List<string>();
        bool supervisor = false;
        Dictionary<string, HashSet<string>> snap; lock (_trava) snap = _membros;
        foreach (var r in Regras())
        {
            if (!snap.TryGetValue(r.Grupo, out var m) || !m.Contains(login)) continue;
            grupos.Add(r.Grupo);
            if (r.Perfil != Supervisor) continue;
            supervisor = true;
            if (r.Setores is { Length: > 0 }) setores.UnionWith(r.Setores);
            else if (!string.IsNullOrWhiteSpace(setorProprio)) setores.Add(setorProprio);   // sem setor na regra: o do próprio supervisor
        }
        return new(login, supervisor ? Supervisor : Atendente, setores.ToArray(), grupos.ToArray());
    }
}
