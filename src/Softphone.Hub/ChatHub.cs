using System.Collections.Concurrent;
using Softphone.Contratos;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Softphone.Hub;

/// <summary>Presença em memória: quem está conectado (por conexão) e o estado que cada um escolheu.</summary>
public sealed class Presencas
{
    private sealed class Estado
    {
        public readonly HashSet<string> Conexoes = new();
        public EstadoPresenca Escolhido = EstadoPresenca.Disponivel;
    }

    private readonly ConcurrentDictionary<string, Estado> _porLogin = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Retorna true se a pessoa acabou de ficar online.</summary>
    public bool Conectou(string login, string conexao)
    {
        var e = _porLogin.GetOrAdd(login, _ => new Estado());
        lock (e) { e.Conexoes.Add(conexao); return e.Conexoes.Count == 1; }
    }

    /// <summary>Retorna true se a pessoa ficou offline (última conexão caiu).</summary>
    public bool Desconectou(string login, string conexao)
    {
        if (!_porLogin.TryGetValue(login, out var e)) return false;
        lock (e) { e.Conexoes.Remove(conexao); return e.Conexoes.Count == 0; }
    }

    public void Definir(string login, EstadoPresenca estado)
    {
        var e = _porLogin.GetOrAdd(login, _ => new Estado());
        lock (e) e.Escolhido = estado == EstadoPresenca.Offline ? EstadoPresenca.Disponivel : estado;
    }

    public EstadoPresenca De(string login)
    {
        if (!_porLogin.TryGetValue(login, out var e)) return EstadoPresenca.Offline;
        lock (e) return e.Conexoes.Count == 0 ? EstadoPresenca.Offline : e.Escolhido;
    }

    public int Online => _porLogin.Values.Count(e => { lock (e) return e.Conexoes.Count > 0; });
}

[Authorize]
public sealed class ChatHub : Microsoft.AspNetCore.SignalR.Hub
{
    private readonly Banco _banco;
    private readonly Presencas _pres;
    private readonly Reunioes _reunioes;
    private readonly OpcoesHub _op;
    private readonly ILogger<ChatHub> _log;

    private readonly ConvitesReuniao _convites;

    public ChatHub(Banco banco, Presencas pres, Reunioes reunioes, ConvitesReuniao convites, OpcoesHub op, ILogger<ChatHub> log, Automacoes automacoes)
    { _banco = banco; _automacoes = automacoes; _pres = pres; _reunioes = reunioes; _convites = convites; _op = op; _log = log; }

    private string Eu => Context.UserIdentifier ?? throw new HubException("Não autenticado.");
    private static string Grupo(string login) => "u:" + login.ToLowerInvariant();

    public override async Task OnConnectedAsync()
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, Grupo(Eu));
        if (_pres.Conectou(Eu, Context.ConnectionId))
            await Clients.All.SendAsync(HubNomes.EvPresenca, Eu, _pres.De(Eu), _banco.ObterPessoa(Eu)?.Recado);
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (_pres.Desconectou(Eu, Context.ConnectionId))
        {
            _banco.MarcarVisto(Eu);
            await Clients.All.SendAsync(HubNomes.EvPresenca, Eu, EstadoPresenca.Offline, (string?)null);
        }
        await base.OnDisconnectedAsync(exception);
    }

    public async Task DefinirPresenca(EstadoPresenca estado, string? recado)
    {
        _pres.Definir(Eu, estado);
        if (recado != null) _banco.DefinirRecado(Eu, recado.Length > 120 ? recado[..120] : recado);
        await Clients.All.SendAsync(HubNomes.EvPresenca, Eu, _pres.De(Eu), _banco.ObterPessoa(Eu)?.Recado);
    }

    public async Task InformarRamal(string? ramal)
    {
        if (ramal != null && (ramal.Length > 20 || !ramal.All(ch => char.IsLetterOrDigit(ch) || ch is '*' or '#' or '+')))
            throw new HubException("Ramal inválido.");
        var antes = _banco.ObterPessoa(Eu)?.Ramal;
        if (antes == ramal) return;
        _banco.DefinirRamal(Eu, ramal);
        var p = Dto(_banco.ObterPessoa(Eu)!);
        await Clients.All.SendAsync(HubNomes.EvPessoa, p);
    }

    private readonly Automacoes _automacoes;

    public List<PessoaDto> ListarPessoas() => _banco.ListarPessoas().Select(Dto).ToList();

    public List<ConversaDto> ListarConversas() => _banco.ConversasDe(Eu);

    public ConversaDto AbrirDireta(string login)
    {
        login = Diretorio.NormalizarLogin(login);
        if (_banco.ObterPessoa(login) is null) throw new HubException("Pessoa não encontrada no diretório.");
        var id = _banco.ObterOuCriarDireta(Eu, login);
        _banco.Mostrar(id, Eu); // se eu tinha ocultado, volta para a lista
        return _banco.Conversa(id, Eu)!;
    }

    public async Task<ConversaDto> CriarGrupo(string titulo, string[] logins)
    {
        if (string.IsNullOrWhiteSpace(titulo) || titulo.Length > 80) throw new HubException("Informe um nome de até 80 caracteres.");
        var membros = logins.Select(Diretorio.NormalizarLogin).Append(Eu).Distinct().ToList();
        if (membros.Count < 2) throw new HubException("Escolha pelo menos uma pessoa.");
        if (membros.Count > 300) throw new HubException("Grupo grande demais (máximo 300).");
        foreach (var m in membros) if (_banco.ObterPessoa(m) is null) throw new HubException($"Pessoa não encontrada: {m}");
        var id = _banco.CriarGrupo(titulo, membros, Eu);
        foreach (var m in membros) await Clients.Group(Grupo(m)).SendAsync(HubNomes.EvConversa, _banco.Conversa(id, m));
        return _banco.Conversa(id, Eu)!;
    }

    public List<MensagemDto> Historico(long conversa, long antesDeId, int quantidade)
    {
        Exigir(conversa);
        return _banco.Historico(conversa, antesDeId, quantidade);
    }

    public async Task<MensagemDto> Enviar(long conversa, string texto, string? arquivoId)
    {
        Exigir(conversa);
        texto = (texto ?? "").Trim();
        if (texto.Length == 0 && arquivoId is null) throw new HubException("Mensagem vazia.");
        if (texto.Length > 8000) throw new HubException("Mensagem longa demais (máximo 8000 caracteres).");
        if (arquivoId != null && !_banco.ArquivoEhDe(arquivoId, Eu)) throw new HubException("Arquivo inválido.");
        var msg = _banco.Inserir(conversa, Eu, texto, arquivoId);
        foreach (var m in _banco.Membros(conversa)) await Clients.Group(Grupo(m)).SendAsync(HubNomes.EvMensagem, msg);
        _ = _automacoes.AoMensagemAsync(msg);   // 1.3: regras do chat (depois de entregar; falha de regra nunca atrapalha a mensagem)
        return msg;
    }

    // ===================== 1.1: citar, @menções, editar, apagar, buscar =====================

    /// <summary>Envia com citação (RespostaA) e menções (só valem logins que estão na conversa).</summary>
    public async Task<MensagemDto> EnviarMensagem(NovaMensagemDto n)
    {
        Exigir(n.Conversa);
        var texto = (n.Texto ?? "").Trim();
        if (texto.Length == 0 && n.ArquivoId is null) throw new HubException("Mensagem vazia.");
        if (texto.Length > 8000) throw new HubException("Mensagem longa demais (máximo 8000 caracteres).");
        if (n.ArquivoId != null && !_banco.ArquivoEhDe(n.ArquivoId, Eu)) throw new HubException("Arquivo inválido.");
        long? resposta = null;
        if (n.RespostaA is long r)
        {
            var citada = _banco.Mensagem(r);
            if (citada is null || citada.Conversa != n.Conversa) throw new HubException("A mensagem citada não é desta conversa.");
            resposta = r;
        }
        var membros = _banco.Membros(n.Conversa);
        var mencoes = (n.Mencoes ?? Array.Empty<string>()).Select(Diretorio.NormalizarLogin)
            .Where(l => membros.Contains(l, StringComparer.OrdinalIgnoreCase) && !string.Equals(l, Eu, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(50).ToArray();
        var msg = _banco.Inserir(n.Conversa, Eu, texto, n.ArquivoId, resposta, mencoes);
        foreach (var m in membros) await Clients.Group(Grupo(m)).SendAsync(HubNomes.EvMensagem, msg);
        _ = _automacoes.AoMensagemAsync(msg);
        return msg;
    }

    public async Task<MensagemDto> EditarMensagem(long id, string texto)
    {
        var msg = MinhaMensagem(id);
        texto = (texto ?? "").Trim();
        if (texto.Length == 0) throw new HubException("Para tirar o texto, apague a mensagem.");
        if (texto.Length > 8000) throw new HubException("Mensagem longa demais (máximo 8000 caracteres).");
        if (texto == msg.Texto) return msg;
        var nova = _banco.Editar(id, texto);
        foreach (var m in _banco.Membros(msg.Conversa)) await Clients.Group(Grupo(m)).SendAsync(HubNomes.EvMensagemAlterada, nova);
        return nova;
    }

    public async Task<MensagemDto> ApagarMensagem(long id)
    {
        var msg = MinhaMensagem(id);
        var (nova, arquivo) = _banco.Apagar(id);
        if (arquivo != null) ApagarArquivos(new[] { arquivo });
        foreach (var m in _banco.Membros(msg.Conversa)) await Clients.Group(Grupo(m)).SendAsync(HubNomes.EvMensagemAlterada, nova);
        return nova;
    }

    private MensagemDto MinhaMensagem(long id)
    {
        var msg = _banco.Mensagem(id) ?? throw new HubException("Mensagem não encontrada.");
        Exigir(msg.Conversa);
        if (!string.Equals(msg.Autor, Eu, StringComparison.OrdinalIgnoreCase)) throw new HubException("Só quem escreveu pode mudar a mensagem.");
        if (msg.Apagada) throw new HubException("A mensagem já foi apagada.");
        return msg;
    }

    public List<ResultadoBuscaDto> Buscar(string texto)
    {
        texto = (texto ?? "").Trim();
        if (texto.Length < 2) throw new HubException("Digite pelo menos 2 letras.");
        if (texto.Length > 100) texto = texto[..100];
        return _banco.Buscar(Eu, texto);
    }

    // ===================== 1.1: painel da TI e comunicados =====================

    /// <summary>Versão do app e nome do PC (o painel da TI mostra quem está em qual versão).</summary>
    public void InformarCliente(string? versao, string? pc)
    {
        static string? Curto(string? s, int max) => string.IsNullOrWhiteSpace(s) ? null : (s.Trim().Length > max ? s.Trim()[..max] : s.Trim());
        bool primeiroAcesso = !_banco.Clientes().Any(c => string.Equals(c.Login, Eu, StringComparison.OrdinalIgnoreCase));
        _banco.RegistrarCliente(Eu, Curto(versao, 20), Curto(pc, 60));
        if (primeiroAcesso) _ = _automacoes.AoPrimeiroAcessoAsync(Eu);   // 1.3: boas-vindas (uma vez por pessoa)
    }

    public List<ComunicadoDto> ComunicadosPendentes()
    {
        var lista = _banco.ComunicadosPendentes(Eu, _banco.ObterPessoa(Eu)?.Unidade);
        foreach (var c in lista) _banco.MarcarEntregue(c.Id, Eu);   // 1.3: buscado pelo app = entregue
        return lista;
    }

    public void ConfirmarComunicado(long id) => _banco.ConfirmarComunicado(id, Eu);

    public async Task MarcarLida(long conversa, long ultimaId)
    {
        Exigir(conversa);
        _banco.MarcarLida(conversa, Eu, ultimaId);
        foreach (var m in _banco.Membros(conversa)) await Clients.Group(Grupo(m)).SendAsync(HubNomes.EvLida, conversa, Eu, ultimaId);
    }

    public async Task Digitar(long conversa)
    {
        Exigir(conversa);
        foreach (var m in _banco.Membros(conversa).Where(x => !string.Equals(x, Eu, StringComparison.OrdinalIgnoreCase)))
            await Clients.Group(Grupo(m)).SendAsync(HubNomes.EvDigitando, conversa, Eu);
    }

    // ===================== ocultar / sair / excluir =====================

    /// <summary>Some da MINHA lista (em todos os meus PCs); volta sozinha se chegar mensagem nova. Histórico mantido.</summary>
    public async Task OcultarConversa(long conversa)
    {
        Exigir(conversa);
        _banco.Ocultar(conversa, Eu);
        await Clients.Group(Grupo(Eu)).SendAsync(HubNomes.EvConversaRemovida, conversa);
    }

    /// <summary>Saio do grupo; os outros veem o aviso.</summary>
    public async Task SairDoGrupo(long conversa)
    {
        Exigir(conversa);
        var conv = _banco.Conversa(conversa, Eu)!;
        if (!conv.Grupo) throw new HubException("Só dá para sair de grupos (conversa individual: use Ocultar).");
        var nome = _banco.ObterPessoa(Eu)?.Nome ?? Eu;
        _banco.Sair(conversa, Eu);
        await Clients.Group(Grupo(Eu)).SendAsync(HubNomes.EvConversaRemovida, conversa);
        var restantes = _banco.Membros(conversa);
        if (restantes.Length == 0) { ApagarArquivos(_banco.Excluir(conversa)); return; }
        var aviso = _banco.Inserir(conversa, restantes[0], $"ℹ️ {nome} saiu do grupo.", null);
        foreach (var m in restantes)
        {
            await Clients.Group(Grupo(m)).SendAsync(HubNomes.EvConversa, _banco.Conversa(conversa, m));
            await Clients.Group(Grupo(m)).SendAsync(HubNomes.EvMensagem, aviso);
        }
    }

    /// <summary>Apaga o grupo para todos (mensagens e arquivos). Só quem criou.</summary>
    public async Task ExcluirGrupo(long conversa)
    {
        Exigir(conversa);
        var conv = _banco.Conversa(conversa, Eu)!;
        if (!conv.Grupo) throw new HubException("Conversa individual não se exclui para os dois: use Ocultar.");
        if (!string.Equals(_banco.CriadorDe(conversa), Eu, StringComparison.OrdinalIgnoreCase))
            throw new HubException("Só quem criou o grupo pode excluir para todos. Você pode sair do grupo.");
        var membros = _banco.Membros(conversa);
        ApagarArquivos(_banco.Excluir(conversa));
        foreach (var m in membros) await Clients.Group(Grupo(m)).SendAsync(HubNomes.EvConversaRemovida, conversa);
        _log.LogInformation("Grupo {Conversa} ({Titulo}) excluído por {Eu}", conversa, conv.Titulo, Eu);
    }

    private void ApagarArquivos(IEnumerable<string> ids)
    {
        foreach (var id in ids)
        {
            try { File.Delete(Path.Combine(_op.Dados, "arquivos", id)); } catch (Exception ex) { _log.LogWarning("Não apagou o arquivo {Id}: {Msg}", id, ex.Message); }
        }
    }

    // ===================== reuniões / chamadas de vídeo =====================

    /// <summary>
    /// Cria uma sala e devolve o meu passe. Com conversa: o convite vai para o chat (botão "Entrar") e, se tocar=true,
    /// toca no Softphone de quem está online (como uma ligação). Sem conversa e sem logins: reunião avulsa ("Reunir agora").
    /// </summary>
    public async Task<ReuniaoDto> IniciarReuniao(long? conversa, string[]? logins, bool tocar)
    {
        if (!_reunioes.Configurado) throw new HubException("Reuniões ainda não estão configuradas no servidor.");
        var eu = _banco.ObterPessoa(Eu)!;
        string[] convidados;
        string titulo;
        bool grupo = false;
        if (conversa is long c)
        {
            Exigir(c);
            var conv = _banco.Conversa(c, Eu)!;
            grupo = conv.Grupo;
            convidados = conv.Membros.Where(m => !string.Equals(m, Eu, StringComparison.OrdinalIgnoreCase)).ToArray();
            titulo = conv.Grupo ? conv.Titulo ?? "Reunião do grupo" : $"Chamada de vídeo com {eu.Nome}";
        }
        else
        {
            convidados = (logins ?? Array.Empty<string>()).Select(Diretorio.NormalizarLogin).Where(l => l != Eu).Distinct().ToArray();
            titulo = $"Reunião de {eu.Nome}";
        }

        var sala = Reunioes.NovaSala(conversa);
        var passe = _reunioes.Passe(sala, titulo, Eu, eu.Nome, eu.Email);
        _convites.Registrar(sala, convidados);

        if (conversa is long cv)
        {
            var msg = _banco.Inserir(cv, Eu, $"{HubNomes.PrefixoConvite}{passe.UrlConvite}\n{titulo}", null);
            foreach (var m in _banco.Membros(cv)) await Clients.Group(Grupo(m)).SendAsync(HubNomes.EvMensagem, msg);
        }
        if (tocar)
        {
            var convite = new ConviteDto(sala, titulo, Eu, eu.Nome, conversa, grupo);
            foreach (var l in convidados.Where(l => _pres.De(l) != EstadoPresenca.Offline))
                await Clients.Group(Grupo(l)).SendAsync(HubNomes.EvConvite, convite);
        }
        _log.LogInformation("Reunião {Sala} iniciada por {Eu} ({Qtd} convidados, tocar={Tocar})", sala, Eu, convidados.Length, tocar);
        return passe;
    }

    /// <summary>Passe para entrar numa sala que alguém abriu (convite tocando ou botão "Entrar" no chat).</summary>
    public ReuniaoDto EntrarReuniao(string sala, string? titulo)
    {
        if (!_reunioes.Configurado) throw new HubException("Reuniões ainda não estão configuradas no servidor.");
        sala = (sala ?? "").Trim().ToLowerInvariant();
        if (!Reunioes.SalaValida(sala)) throw new HubException("Sala inválida.");
        // sala de conversa: só quem participa da conversa entra, ou quem foi chamado para ela (1.2.2)
        if (ConvitesReuniao.ConversaDaSala(sala) is long conv && !_convites.Convidado(sala, Eu)) Exigir(conv);
        var eu = _banco.ObterPessoa(Eu)!;
        return _reunioes.Passe(sala, string.IsNullOrWhiteSpace(titulo) ? "Reunião" : titulo, Eu, eu.Nome, eu.Email);
    }

    /// <summary>"Adicionar pessoas" com a reunião aberta (1.2.2): toca no Softphone de quem está online e deixa entrar
    /// na sala. Só quem pode entrar na sala pode chamar outras pessoas. Devolve os logins que estão offline.</summary>
    public async Task<string[]> ConvidarParaReuniao(string sala, string? titulo, string[] logins)
    {
        if (!_reunioes.Configurado) throw new HubException("Reuniões ainda não estão configuradas no servidor.");
        sala = (sala ?? "").Trim().ToLowerInvariant();
        if (!Reunioes.SalaValida(sala)) throw new HubException("Sala inválida.");
        if (ConvitesReuniao.ConversaDaSala(sala) is long conv && !_convites.Convidado(sala, Eu)) Exigir(conv);
        var convidados = (logins ?? Array.Empty<string>()).Select(Diretorio.NormalizarLogin)
            .Where(l => l.Length > 0 && !string.Equals(l, Eu, StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        _convites.Registrar(sala, convidados);
        var eu = _banco.ObterPessoa(Eu)!;
        var convite = new ConviteDto(sala, string.IsNullOrWhiteSpace(titulo) ? $"Reunião de {eu.Nome}" : titulo, Eu, eu.Nome, null, false);
        var offline = new List<string>();
        foreach (var l in convidados)
        {
            if (_pres.De(l) == EstadoPresenca.Offline) { offline.Add(l); continue; }
            await Clients.Group(Grupo(l)).SendAsync(HubNomes.EvConvite, convite);
        }
        _log.LogInformation("Reunião {Sala}: {Eu} chamou {Qtd} pessoa(s) ({Off} offline)", sala, Eu, convidados.Length, offline.Count);
        return offline.ToArray();
    }

    public async Task ResponderConvite(string sala, string de, bool aceitou)
    {
        await Clients.Group(Grupo(Diretorio.NormalizarLogin(de))).SendAsync(HubNomes.EvConviteResposta, sala, Eu, aceitou);
        // nas minhas outras conexões (outro PC), para de tocar
        await Clients.Group(Grupo(Eu)).SendAsync(HubNomes.EvConviteCancelado, sala);
    }

    public async Task CancelarConvite(string sala, string[] logins)
    {
        foreach (var l in logins.Select(Diretorio.NormalizarLogin).Distinct())
            await Clients.Group(Grupo(l)).SendAsync(HubNomes.EvConviteCancelado, sala);
    }

    private void Exigir(long conversa)
    {
        if (!_banco.EhMembro(conversa, Eu)) throw new HubException("Você não participa desta conversa.");
    }

    private PessoaDto Dto(Banco.Pessoa p) =>
        new(p.Login, p.Nome, p.Setor, p.Unidade, p.Email, p.Ramal, _pres.De(p.Login), p.VistoEm, p.Recado);
}
