using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Softphone.Contratos;
using Microsoft.AspNetCore.SignalR.Client;

namespace Softphone.Core;

public enum EstadoHub { Desligado, Conectando, Conectado, PrecisaSenha, Falha }

/// <summary>O Hub respondeu com erro (a mensagem é a dele, em português). <see cref="Status"/> separa recusa definitiva
/// (400/413: aquele pedido nunca vai passar) de falha passageira.</summary>
public sealed class ErroHubException(HttpStatusCode status, string mensagem) : InvalidOperationException(mensagem)
{
    public HttpStatusCode Status { get; } = status;
    public bool Definitivo => Status is HttpStatusCode.BadRequest or HttpStatusCode.RequestEntityTooLarge or HttpStatusCode.UnsupportedMediaType;
}

/// <summary>
/// Conexão do Softphone com o Hub (presença, chat, diretório). Login automático pelo Windows (Kerberos); se o servidor
/// recusar, fica em PrecisaSenha e a tela pede usuário/senha do AD uma vez. Reconecta sozinho. Eventos chegam em threads
/// da biblioteca: a tela despacha para a UI.
/// </summary>
public sealed class HubCliente : IAsyncDisposable
{
    private readonly HttpClient _http;
    private HubConnection? _con;
    private string? _token;
    private DateTime _tokenExpira;
    private readonly SemaphoreSlim _trava = new(1, 1);

    public HubCliente(string urlBase) : this(urlBase, null) { }

    /// <summary>Com <paramref name="rede"/>: só para testes (rede de mentira, sem servidor).</summary>
    public HubCliente(string urlBase, HttpMessageHandler? rede)
    {
        UrlBase = urlBase.TrimEnd('/');
        var raiz = new Uri(UrlBase + "/");
        _http = rede is null
            ? new HttpClient(new HttpClientHandler { UseDefaultCredentials = true, PreAuthenticate = true })
            : new HttpClient(rede, disposeHandler: false);
        _http.BaseAddress = raiz;
        _http.Timeout = TimeSpan.FromSeconds(30);
        // envio de gravação (centenas de MB) e respostas da IA (dezenas de segundos) não cabem nos 30 s
        _httpLongo = rede is null ? new HttpClient() : new HttpClient(rede, disposeHandler: false);
        _httpLongo.BaseAddress = raiz;
        _httpLongo.Timeout = TimeSpan.FromHours(1);
    }

    private readonly HttpClient _httpLongo;
    /// <summary>Cancelado ao descartar o cliente: encerra o laço de nova tentativa.</summary>
    private readonly CancellationTokenSource _fim = new();
    private readonly SemaphoreSlim _travaToken = new(1, 1);

    /// <summary>Espera entre tentativas quando o Hub está fora do ar (testes usam menos).</summary>
    public TimeSpan IntervaloNovaTentativa { get; set; } = TimeSpan.FromSeconds(20);

    public string UrlBase { get; }
    /// <summary>Painel web de administração servido pelo próprio Hub (1.2.0+); o app só abre no navegador.</summary>
    public string UrlPainel => UrlBase + "/admin/";
    public EstadoHub Estado { get; private set; } = EstadoHub.Desligado;
    public string? Motivo { get; private set; }
    public PessoaDto? Eu { get; private set; }
    public string? VersaoServidor { get; private set; }
    /// <summary>A pessoa é da TI (vê o painel de administração). Vem do Hub 1.1 no login.</summary>
    public bool Admin { get; private set; }

    /// <summary>Só para testes automáticos (servidor com Hub:Teste=true).</summary>
    public string? LoginTeste { get; set; }
    /// <summary>Só testes: simula o ipPhone que viria do AD.</summary>
    public string? RamalAdTeste { get; set; }

    public event Action<EstadoHub, string?>? EstadoMudou;
    public event Action<MensagemDto>? Mensagem;
    public event Action<string, EstadoPresenca, string?>? Presenca;
    public event Action<long, string>? Digitando;
    public event Action<ConversaDto>? Conversa;
    public event Action<long, string, long>? Lida;
    public event Action<PessoaDto>? Pessoa;
    /// <summary>Chamada de vídeo / reunião tocando para mim.</summary>
    public event Action<ConviteDto>? Convite;
    /// <summary>(sala, login, aceitou): alguém respondeu o meu convite.</summary>
    public event Action<string, string, bool>? ConviteResposta;
    /// <summary>(sala): quem chamou desistiu, ou eu atendi em outro PC.</summary>
    public event Action<string>? ConviteCancelado;
    /// <summary>(conversa): saiu da minha lista (ocultei, saí ou excluíram o grupo).</summary>
    public event Action<long>? ConversaRemovida;
    /// <summary>Andamento de uma gravação minha (na fila, transcrevendo, resumindo, pronta, erro).</summary>
    public event Action<GravacaoDto>? Gravacao;
    /// <summary>Mensagem editada ou apagada pelo autor (1.1).</summary>
    public event Action<MensagemDto>? MensagemAlterada;
    /// <summary>Aviso da TI (1.1).</summary>
    public event Action<ComunicadoDto>? Comunicado;
    public event Action<long>? ComunicadoCancelado;
    /// <summary>A TI ligou/desligou a IA pelo painel (1.1).</summary>
    public event Action<EstadoIaDto>? EstadoIa;
    /// <summary>Token novo (o app guarda criptografado para reconectar sem pedir senha).</summary>
    public event Action<string, DateTime>? TokenRenovado;

    /// <summary>Hub 1.1 ou mais novo (citar/editar/apagar, comunicados, painel). Com Hub 1.0 o app esconde esses recursos.</summary>
    public bool ServidorV11 => Version.TryParse(VersaoServidor, out var v) && v >= new Version(1, 1);
    public event Action<string>? Log;

    /// <summary>Token salvo de uma sessão anterior (evita pedir senha de novo quando o login automático não funciona).</summary>
    public void UsarTokenSalvo(string? token, DateTime expira)
    {
        if (!string.IsNullOrWhiteSpace(token) && expira > DateTime.UtcNow.AddHours(1)) { _token = token; _tokenExpira = expira; }
    }

    // ========================= login =========================

    public async Task IniciarAsync(CancellationToken ct = default)
    {
        if (_fim.IsCancellationRequested) return;
        await _trava.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Mudar(EstadoHub.Conectando, null);
            if (!await ObterTokenAsync(ct).ConfigureAwait(false)) return;
            await ConectarSignalRAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Mudar(EstadoHub.Falha, Traduzir(ex));
            _ = TentarDeNovoDepoisAsync();
        }
        finally { _trava.Release(); }
    }

    /// <summary>Login com usuário e senha do AD (quando o automático não deu). A senha não é guardada.</summary>
    public async Task<string?> EntrarComSenhaAsync(string login, string senha, CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.PostAsJsonAsync("api/entrar-senha", new EntrarComSenha(login, senha), ct).ConfigureAwait(false);
            if (resp.StatusCode == HttpStatusCode.Unauthorized) return "Usuário ou senha inválidos.";
            resp.EnsureSuccessStatusCode();
            Aceitar((await resp.Content.ReadFromJsonAsync<EntrarResposta>(cancellationToken: ct).ConfigureAwait(false))!);
            await IniciarAsync(ct).ConfigureAwait(false);
            return null;
        }
        catch (Exception ex) { return Traduzir(ex); }
    }

    private async Task<bool> ObterTokenAsync(CancellationToken ct)
    {
        if (LoginTeste != null)
        {
            var extra = RamalAdTeste is null ? "" : "&ramalAd=" + Uri.EscapeDataString(RamalAdTeste);
            using var r = await _http.PostAsync($"api/entrar-teste?login={Uri.EscapeDataString(LoginTeste)}{extra}", null, ct).ConfigureAwait(false);
            r.EnsureSuccessStatusCode();
            Aceitar((await r.Content.ReadFromJsonAsync<EntrarResposta>(cancellationToken: ct).ConfigureAwait(false))!);
            return true;
        }

        if (await TokenSemSenhaAsync(ct).ConfigureAwait(false)) return true;

        // 3) precisa de usuário e senha
        Mudar(EstadoHub.PrecisaSenha, "Entre com o seu usuário e senha de rede para usar o chat.");
        return false;
    }

    /// <summary>Consegue um token sem pedir senha: 1) login do Windows; 2) troca do token atual/salvo por um novo.
    /// Servidor fora do ar sobe como exceção (a conexão fica em Falha e tenta de novo depois).</summary>
    private async Task<bool> TokenSemSenhaAsync(CancellationToken ct)
    {
        // 1) login automático pelo Windows (renova o token a cada conexão)
        using (var resp = await _http.GetAsync("api/entrar", ct).ConfigureAwait(false))
        {
            if (resp.IsSuccessStatusCode)
            {
                Aceitar((await resp.Content.ReadFromJsonAsync<EntrarResposta>(cancellationToken: ct).ConfigureAwait(false))!);
                return true;
            }
            Registrar($"Login automático recusado ({(int)resp.StatusCode}).");
        }

        // 2) token salvo ainda válido: troca por um novo, que traz quem sou eu, a versão do Hub e se sou da TI
        if (_token != null && _tokenExpira > DateTime.UtcNow.AddMinutes(1)) return await TrocarTokenAsync(ct).ConfigureAwait(false);
        return false;
    }

    /// <summary>Hub 1.2.1+: POST /api/renovar devolve token novo + identidade. Hub mais antigo (404): mantém o token e
    /// busca quem sou eu em /api/eu e a versão em /saude. 401: o token não vale mais (vencido, chave trocada, conta
    /// desativada no AD) e é descartado.</summary>
    private async Task<bool> TrocarTokenAsync(CancellationToken ct)
    {
        var tk = _token;
        if (tk is null) return false;
        using (var req = new HttpRequestMessage(HttpMethod.Post, "api/renovar"))
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tk);
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode)
            {
                Aceitar((await resp.Content.ReadFromJsonAsync<EntrarResposta>(cancellationToken: ct).ConfigureAwait(false))!);
                return true;
            }
            if (resp.StatusCode == HttpStatusCode.Unauthorized) { Registrar("Token salvo recusado pelo servidor."); _token = null; return false; }
            if (resp.StatusCode is not (HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)) resp.EnsureSuccessStatusCode();
        }
        // Hub 1.2.0 ou anterior (sem /api/renovar)
        using (var req = new HttpRequestMessage(HttpMethod.Get, "api/eu"))
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tk);
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            if (resp.StatusCode == HttpStatusCode.Unauthorized) { Registrar("Token salvo recusado pelo servidor."); _token = null; return false; }
            resp.EnsureSuccessStatusCode();
            Eu = await resp.Content.ReadFromJsonAsync<PessoaDto>(cancellationToken: ct).ConfigureAwait(false);
        }
        try
        {
            using var resp = await _http.GetAsync("saude", ct).ConfigureAwait(false);
            using var doc = System.Text.Json.JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            if (doc.RootElement.TryGetProperty("versao", out var v)) VersaoServidor = v.GetString();
        }
        catch (Exception ex) { Registrar("Versão do Hub não lida: " + ex.Message); }
        return true;
    }

    /// <summary>Antes de usar o token (pedido HTTP ou (re)conexão do SignalR): se vence em menos de 10 minutos, troca
    /// por um novo. Se não der, segue com o atual (o servidor recusa e a conexão cai para o login de novo).</summary>
    private async Task GarantirTokenAsync(CancellationToken ct = default)
    {
        if (_token is null || _tokenExpira > DateTime.UtcNow.AddMinutes(10)) return;
        await _travaToken.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_token is null || _tokenExpira > DateTime.UtcNow.AddMinutes(10)) return; // outro pedido já renovou
            if (LoginTeste != null) { await ObterTokenAsync(ct).ConfigureAwait(false); return; }
            await TokenSemSenhaAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) { Registrar("Renovação do token falhou: " + Traduzir(ex)); }
        finally { _travaToken.Release(); }
    }

    internal async Task<string?> TokenParaConexaoAsync()
    {
        await GarantirTokenAsync().ConfigureAwait(false);
        return _token;
    }

    /// <summary>Só testes: simula um token que vence logo.</summary>
    internal void ForcarExpiracaoParaTeste(DateTime expira) => _tokenExpira = expira;

    private void Aceitar(EntrarResposta r)
    {
        _token = r.Token;
        _tokenExpira = r.ExpiraEm;
        Eu = r.Eu;
        VersaoServidor = r.VersaoServidor;
        Admin = r.Admin;
        TokenRenovado?.Invoke(r.Token, r.ExpiraEm);
    }

    private async Task ConectarSignalRAsync(CancellationToken ct)
    {
        if (_con != null) { try { await _con.DisposeAsync().ConfigureAwait(false); } catch { } }
        _con = new HubConnectionBuilder()
            .WithUrl(UrlBase + HubNomes.Rota, o => o.AccessTokenProvider = TokenParaConexaoAsync)
            .WithAutomaticReconnect(new TentarPorUmTempo())
            .Build();

        _con.On<MensagemDto>(HubNomes.EvMensagem, m => Mensagem?.Invoke(m));
        _con.On<string, EstadoPresenca, string?>(HubNomes.EvPresenca, (l, e, r) => Presenca?.Invoke(l, e, r));
        _con.On<long, string>(HubNomes.EvDigitando, (c, l) => Digitando?.Invoke(c, l));
        _con.On<ConversaDto>(HubNomes.EvConversa, c => Conversa?.Invoke(c));
        _con.On<long, string, long>(HubNomes.EvLida, (c, l, u) => Lida?.Invoke(c, l, u));
        _con.On<PessoaDto>(HubNomes.EvPessoa, p => Pessoa?.Invoke(p));
        _con.On<ConviteDto>(HubNomes.EvConvite, c => Convite?.Invoke(c));
        _con.On<string, string, bool>(HubNomes.EvConviteResposta, (s, l, a) => ConviteResposta?.Invoke(s, l, a));
        _con.On<string>(HubNomes.EvConviteCancelado, s => ConviteCancelado?.Invoke(s));
        _con.On<long>(HubNomes.EvConversaRemovida, c => ConversaRemovida?.Invoke(c));
        _con.On<GravacaoDto>(HubNomes.EvGravacao, g => Gravacao?.Invoke(g));
        _con.On<MensagemDto>(HubNomes.EvMensagemAlterada, m => MensagemAlterada?.Invoke(m));
        _con.On<ComunicadoDto>(HubNomes.EvComunicado, c => Comunicado?.Invoke(c));
        _con.On<long>(HubNomes.EvComunicadoCancelado, id => ComunicadoCancelado?.Invoke(id));
        _con.On<EstadoIaDto>(HubNomes.EvEstadoIa, e => EstadoIa?.Invoke(e));
        _con.Reconnecting += ex => { Mudar(EstadoHub.Conectando, "Reconectando ao servidor..."); return Task.CompletedTask; };
        _con.Reconnected += id => { Mudar(EstadoHub.Conectado, null); return Task.CompletedTask; };
        _con.Closed += ex => { Mudar(EstadoHub.Falha, ex is null ? "Desconectado." : Traduzir(ex)); _ = TentarDeNovoDepoisAsync(); return Task.CompletedTask; };

        await _con.StartAsync(ct).ConfigureAwait(false);
        Mudar(EstadoHub.Conectado, null);
    }

    // Um só laço de nova tentativa por cliente: enquanto o estado for Falha, tenta de novo a cada intervalo.
    // (Antes, a falha da tentativa agendada chamava este método com a trava ainda presa e ninguém agendava a próxima.)
    private int _tentando;
    private async Task TentarDeNovoDepoisAsync()
    {
        if (Interlocked.Exchange(ref _tentando, 1) == 1) return;
        try
        {
            while (!_fim.IsCancellationRequested)
            {
                await Task.Delay(IntervaloNovaTentativa, _fim.Token).ConfigureAwait(false);
                if (Estado != EstadoHub.Falha) break;
                await IniciarAsync(_fim.Token).ConfigureAwait(false);
                if (Estado != EstadoHub.Falha) break;
            }
        }
        catch (OperationCanceledException) { }
        finally { Interlocked.Exchange(ref _tentando, 0); }
        // caiu de novo enquanto o laço estava saindo: recomeça
        if (Estado == EstadoHub.Falha && !_fim.IsCancellationRequested) _ = TentarDeNovoDepoisAsync();
    }

    /// <summary>Reconexão automática do SignalR por uns 2 minutos; depois desiste (Closed) e o laço acima refaz o login
    /// (token recusado, chave do Hub trocada ou conta desativada não ficam tentando o mesmo token para sempre).</summary>
    private sealed class TentarPorUmTempo : IRetryPolicy
    {
        public TimeSpan? NextRetryDelay(RetryContext c) => c.PreviousRetryCount >= 8 ? null
            : TimeSpan.FromSeconds(c.PreviousRetryCount switch { 0 => 0, 1 => 2, 2 => 5, 3 => 10, _ => 30 });
    }

    // ========================= chamadas ao Hub =========================

    private HubConnection Con => _con is { State: HubConnectionState.Connected } c ? c : throw new InvalidOperationException("Sem conexão com o servidor do chat.");

    public Task DefinirPresencaAsync(EstadoPresenca estado, string? recado = null) => Con.InvokeAsync(HubNomes.DefinirPresenca, estado, recado);
    public Task InformarRamalAsync(string? ramal) => Con.InvokeAsync(HubNomes.InformarRamal, ramal);
    public Task<List<PessoaDto>> ListarPessoasAsync() => Con.InvokeAsync<List<PessoaDto>>(HubNomes.ListarPessoas);
    public Task<List<ConversaDto>> ListarConversasAsync() => Con.InvokeAsync<List<ConversaDto>>(HubNomes.ListarConversas);
    public Task<ConversaDto> AbrirDiretaAsync(string login) => Con.InvokeAsync<ConversaDto>(HubNomes.AbrirDireta, login);
    public Task<ConversaDto> CriarGrupoAsync(string titulo, string[] logins) => Con.InvokeAsync<ConversaDto>(HubNomes.CriarGrupo, titulo, logins);
    public Task<List<MensagemDto>> HistoricoAsync(long conversa, long antesDeId = 0, int quantidade = 50) => Con.InvokeAsync<List<MensagemDto>>(HubNomes.Historico, conversa, antesDeId, quantidade);
    public Task<MensagemDto> EnviarAsync(long conversa, string texto, string? arquivoId = null) => Con.InvokeAsync<MensagemDto>(HubNomes.Enviar, conversa, texto, arquivoId);
    public Task MarcarLidaAsync(long conversa, long ultimaId) => Con.InvokeAsync(HubNomes.MarcarLida, conversa, ultimaId);
    public Task DigitarAsync(long conversa) => Con.InvokeAsync(HubNomes.Digitar, conversa);
    public Task<ReuniaoDto> IniciarReuniaoAsync(long? conversa, string[]? logins, bool tocar) => Con.InvokeAsync<ReuniaoDto>(HubNomes.IniciarReuniao, conversa, logins, tocar);
    public Task<ReuniaoDto> EntrarReuniaoAsync(string sala, string? titulo = null) => Con.InvokeAsync<ReuniaoDto>(HubNomes.EntrarReuniao, sala, titulo);
    public Task ResponderConviteAsync(string sala, string de, bool aceitou) => Con.InvokeAsync(HubNomes.ResponderConvite, sala, de, aceitou);
    public Task CancelarConviteAsync(string sala, string[] logins) => Con.InvokeAsync(HubNomes.CancelarConvite, sala, logins);
    /// <summary>Hub 1.2.2+: chama mais pessoas para uma reunião já aberta (toca no Softphone delas). Devolve quem está offline.</summary>
    public Task<string[]> ConvidarParaReuniaoAsync(string sala, string titulo, string[] logins) =>
        Con.InvokeAsync<string[]>(HubNomes.ConvidarParaReuniao, sala, titulo, logins);
    /// <summary>Link da reunião para Outlook/e-mail (Hub 1.2.2+): abre no navegador com o login do Windows.</summary>
    public string LinkReuniao(string sala) => $"{UrlBase}/reuniao/{sala}";
    public Task OcultarConversaAsync(long conversa) => Con.InvokeAsync(HubNomes.OcultarConversa, conversa);
    public Task SairDoGrupoAsync(long conversa) => Con.InvokeAsync(HubNomes.SairDoGrupo, conversa);
    public Task ExcluirGrupoAsync(long conversa) => Con.InvokeAsync(HubNomes.ExcluirGrupo, conversa);

    // ----- 1.1 -----
    public Task<MensagemDto> EnviarMensagemAsync(long conversa, string texto, string? arquivoId, long? respostaA, string[]? mencoes) =>
        Con.InvokeAsync<MensagemDto>(HubNomes.EnviarMensagem, new NovaMensagemDto(conversa, texto, arquivoId, respostaA, mencoes));
    public Task<MensagemDto> EditarMensagemAsync(long id, string texto) => Con.InvokeAsync<MensagemDto>(HubNomes.EditarMensagem, id, texto);
    /// <summary>1.3.4: reage à mensagem com um emoji (o mesmo emoji de novo ou vazio tira a reação). Precisa do Hub 1.3.4.</summary>
    public Task<MensagemDto> ReagirAsync(long id, string? emoji) => Con.InvokeAsync<MensagemDto>(HubNomes.Reagir, id, emoji ?? "");
    public Task<MensagemDto> ApagarMensagemAsync(long id) => Con.InvokeAsync<MensagemDto>(HubNomes.ApagarMensagem, id);
    public Task<List<ResultadoBuscaDto>> BuscarAsync(string texto) => Con.InvokeAsync<List<ResultadoBuscaDto>>(HubNomes.Buscar, texto);
    public Task InformarClienteAsync(string? versao, string? pc) => Con.InvokeAsync(HubNomes.InformarCliente, versao, pc);
    public Task<List<ComunicadoDto>> ComunicadosPendentesAsync() => Con.InvokeAsync<List<ComunicadoDto>>(HubNomes.ComunicadosPendentes);
    public Task ConfirmarComunicadoAsync(long id) => Con.InvokeAsync(HubNomes.ConfirmarComunicado, id);

    // ========================= arquivos =========================

    public async Task<ArquivoDto> EnviarArquivoAsync(string caminho, CancellationToken ct = default)
    {
        using var conteudo = new MultipartFormDataContent();
        await using var fs = File.OpenRead(caminho);
        var parte = new StreamContent(fs);
        parte.Headers.ContentType = new MediaTypeHeaderValue(TipoPorExtensao(caminho));
        conteudo.Add(parte, "arquivo", Path.GetFileName(caminho));
        using var req = await PedidoAsync(HttpMethod.Post, "api/arquivos", ct, conteudo).ConfigureAwait(false);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException("Falha ao enviar o arquivo: " + await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        return (await resp.Content.ReadFromJsonAsync<ArquivoDto>(cancellationToken: ct).ConfigureAwait(false))!;
    }

    public async Task BaixarArquivoAsync(string id, string destino, CancellationToken ct = default)
    {
        using var req = await PedidoAsync(HttpMethod.Get, $"api/arquivos/{id}", ct).ConfigureAwait(false);
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        await SalvarAsync(resp, destino, ct).ConfigureAwait(false);
    }

    /// <summary>Baixa para um temporário ao lado e só renomeia para o destino depois de receber tudo: um download que
    /// cai no meio não deixa arquivo pela metade (que depois seria tocado/exportado como se estivesse completo).
    /// O nome do temporário é único, então dois downloads do mesmo arquivo ao mesmo tempo não se atrapalham.</summary>
    private static async Task SalvarAsync(HttpResponseMessage resp, string destino, CancellationToken ct)
    {
        var tmp = $"{destino}.{Guid.NewGuid():N}.baixando";
        try
        {
            await using (var s = File.Create(tmp)) await resp.Content.CopyToAsync(s, ct).ConfigureAwait(false);
            if (resp.Content.Headers.ContentLength is long esperado && new FileInfo(tmp).Length != esperado)
                throw new IOException("Download incompleto.");
            File.Move(tmp, destino, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch { }
            throw;
        }
    }

    // ========================= IA: gravações, contexto, sugestões =========================

    private async Task<HttpRequestMessage> PedidoAsync(HttpMethod m, string url, CancellationToken ct, HttpContent? conteudo = null)
    {
        await GarantirTokenAsync(ct).ConfigureAwait(false);
        var r = new HttpRequestMessage(m, url) { Content = conteudo };
        r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        return r;
    }

    private static async Task Conferir(HttpResponseMessage resp, CancellationToken ct)
    {
        if (resp.IsSuccessStatusCode) return;
        string msg = $"Servidor respondeu {(int)resp.StatusCode}.";
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            if (doc.RootElement.TryGetProperty("erro", out var e)) msg = e.GetString() ?? msg;
        }
        catch { }
        throw new ErroHubException(resp.StatusCode, msg);
    }

    private async Task<T> PedirAsync<T>(HttpMethod m, string url, CancellationToken ct, HttpContent? conteudo = null, bool longo = false)
    {
        using var req = await PedidoAsync(m, url, ct, conteudo).ConfigureAwait(false);
        using var resp = await (longo ? _httpLongo : _http).SendAsync(req, ct).ConfigureAwait(false);
        await Conferir(resp, ct).ConfigureAwait(false);
        return (await resp.Content.ReadFromJsonAsync<T>(cancellationToken: ct).ConfigureAwait(false))!;
    }

    public Task<EstadoIaDto> EstadoIaAsync(CancellationToken ct = default) => PedirAsync<EstadoIaDto>(HttpMethod.Get, "api/ia/estado", ct);

    /// <summary>Manda o WAV de uma ligação gravada para transcrição/resumo. O andamento chega pelo evento <see cref="Gravacao"/>.</summary>
    /// <param name="transcrever">false = o servidor só guarda o áudio (1.2.4+); a pessoa pede depois pelo botão Transcrever.</param>
    public async Task<GravacaoDto> EnviarGravacaoAsync(string wav, string numero, string? nome, bool entrada, DateTime inicio, TimeSpan duracao,
        CancellationToken ct = default, bool transcrever = true)
    {
        using var conteudo = new MultipartFormDataContent();
        await using var fs = File.OpenRead(wav);
        var parte = new StreamContent(fs, 256 * 1024);
        parte.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        conteudo.Add(parte, "audio", Path.GetFileName(wav));
        conteudo.Add(new StringContent(numero), "numero");
        if (!string.IsNullOrWhiteSpace(nome)) conteudo.Add(new StringContent(nome), "nome");
        conteudo.Add(new StringContent(entrada ? "entrada" : "saida"), "direcao");
        conteudo.Add(new StringContent(inicio.ToUniversalTime().ToString("O")), "inicio");
        conteudo.Add(new StringContent(((int)duracao.TotalSeconds).ToString()), "duracao");
        conteudo.Add(new StringContent(transcrever ? "true" : "false"), "transcrever");
        return await PedirAsync<GravacaoDto>(HttpMethod.Post, "api/gravacoes", ct, conteudo, longo: true).ConfigureAwait(false);
    }

    public Task<List<GravacaoDto>> ListarGravacoesAsync(CancellationToken ct = default) => PedirAsync<List<GravacaoDto>>(HttpMethod.Get, "api/gravacoes", ct);
    public Task<GravacaoDetalheDto> GravacaoAsync(long id, CancellationToken ct = default) => PedirAsync<GravacaoDetalheDto>(HttpMethod.Get, $"api/gravacoes/{id}", ct);
    public Task<GravacaoDto> ReprocessarGravacaoAsync(long id, CancellationToken ct = default) => PedirAsync<GravacaoDto>(HttpMethod.Post, $"api/gravacoes/{id}/reprocessar", ct);
    public Task<ContextoNumeroDto> ContextoAsync(string numero, CancellationToken ct = default) =>
        PedirAsync<ContextoNumeroDto>(HttpMethod.Get, $"api/contexto?numero={Uri.EscapeDataString(numero)}", ct);

    public async Task ExcluirGravacaoAsync(long id, CancellationToken ct = default)
    {
        using var req = await PedidoAsync(HttpMethod.Delete, $"api/gravacoes/{id}", ct).ConfigureAwait(false);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        await Conferir(resp, ct).ConfigureAwait(false);
    }

    public async Task BaixarAudioGravacaoAsync(long id, string destino, CancellationToken ct = default)
    {
        using var req = await PedidoAsync(HttpMethod.Get, $"api/gravacoes/{id}/audio", ct).ConfigureAwait(false);
        using var resp = await _httpLongo.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        await Conferir(resp, ct).ConfigureAwait(false);
        await SalvarAsync(resp, destino, ct).ConfigureAwait(false);
    }

    // ========================= 1.1: qualidade e painel da TI =========================

    /// <summary>Medição de uma ligação (vai para o painel da TI). Hub 1.0 não tem: ignora.</summary>
    public async Task EnviarQualidadeAsync(QualidadeLigacaoDto q, CancellationToken ct = default)
    {
        if (!ServidorV11) return;
        using var req = await PedidoAsync(HttpMethod.Post, "api/qualidade", ct, JsonContent.Create(q)).ConfigureAwait(false);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        await Conferir(resp, ct).ConfigureAwait(false);
    }

    // ========================= 1.3: chamadas, artigos, anotações e copiloto =========================

    /// <summary>Hub 1.3 ou mais novo (registro de chamadas, artigos, copiloto). Com Hub antigo o app esconde esses recursos.</summary>
    public bool ServidorV13 => Version.TryParse(VersaoServidor, out var v) && v >= new Version(1, 3);

    public async Task EnviarChamadaAsync(ChamadaRegistroDto r, CancellationToken ct = default)
    {
        if (!ServidorV13) return;
        using var req = await PedidoAsync(HttpMethod.Post, "api/chamadas", ct, JsonContent.Create(r)).ConfigureAwait(false);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        await Conferir(resp, ct).ConfigureAwait(false);
    }

    public Task<List<RegraAutomacao>> GravarAutomacoesAsync(IEnumerable<RegraAutomacao> r, CancellationToken ct = default) => PedirAsync<List<RegraAutomacao>>(HttpMethod.Put, "api/admin/automacoes", ct, JsonContent.Create(r));
    public Task<List<ExecucaoAutomacao>> ExecucoesAutomacoesAsync(CancellationToken ct = default) => PedirAsync<List<ExecucaoAutomacao>>(HttpMethod.Get, "api/admin/automacoes/execucoes", ct);

    // 1.3: perfis de acesso e comunicados por público
    public Task<PerfilEfetivoDto> MeuPerfilAcessoAsync(CancellationToken ct = default) => PedirAsync<PerfilEfetivoDto>(HttpMethod.Get, "api/eu/perfil", ct);
    public Task<List<RegraAcessoDto>> GravarRegrasAcessoAsync(IEnumerable<RegraAcessoDto> r, CancellationToken ct = default) => PedirAsync<List<RegraAcessoDto>>(HttpMethod.Put, "api/admin/acesso", ct, JsonContent.Create(r));
    public Task<PerfilEfetivoDto> PerfilDeAsync(string login, CancellationToken ct = default) => PedirAsync<PerfilEfetivoDto>(HttpMethod.Get, $"api/admin/acesso/consulta?login={Uri.EscapeDataString(login)}", ct);
    public Task<AlcanceComunicadoDto> AlcanceComunicadoAsync(NovoComunicadoV13Dto n, CancellationToken ct = default) => PedirAsync<AlcanceComunicadoDto>(HttpMethod.Post, "api/comunicados/alcance", ct, JsonContent.Create(n));
    public Task<ComunicadoDto> EnviarComunicadoAsync(NovoComunicadoV13Dto n, CancellationToken ct = default) => PedirAsync<ComunicadoDto>(HttpMethod.Post, "api/comunicados/enviar", ct, JsonContent.Create(n));

    // administração (painel e testes): artigos, auditoria, resumo operacional
    public Task<List<AuditoriaDto>> AuditoriaAsync(string? acao = null, CancellationToken ct = default) =>
        PedirAsync<List<AuditoriaDto>>(HttpMethod.Get, "api/admin/auditoria" + (acao == null ? "" : "?acao=" + Uri.EscapeDataString(acao)), ct);
    public Task<ResumoOperacionalDto> ResumoOperacionalAsync(string periodo = "hoje", CancellationToken ct = default) =>
        PedirAsync<ResumoOperacionalDto>(HttpMethod.Get, $"api/admin/resumo?periodo={periodo}", ct);
    public Task<List<ChamadaPainelDto>> ChamadasAdminAsync(string consulta, CancellationToken ct = default) =>
        PedirAsync<List<ChamadaPainelDto>>(HttpMethod.Get, "api/admin/chamadas?" + consulta, ct);

    private async Task EnviarAsync(HttpMethod m, string url, HttpContent? conteudo, CancellationToken ct)
    {
        using var req = await PedidoAsync(m, url, ct, conteudo).ConfigureAwait(false);
        using var resp = await _httpLongo.SendAsync(req, ct).ConfigureAwait(false);
        await Conferir(resp, ct).ConfigureAwait(false);
    }

    public Task<PainelAdminDto> PainelAsync(CancellationToken ct = default) => PedirAsync<PainelAdminDto>(HttpMethod.Get, "api/admin/painel", ct);
    public Task<EstadoIaDto> ConfigurarIaAsync(ConfigIaDto d, CancellationToken ct = default) => PedirAsync<EstadoIaDto>(HttpMethod.Put, "api/admin/ia", ct, JsonContent.Create(d));
    public Task<TesteIaDto> TestarIaAsync(CancellationToken ct = default) => PedirAsync<TesteIaDto>(HttpMethod.Post, "api/admin/ia/testar", ct, longo: true);
    public Task<QualidadePainelDto> QualidadePainelAsync(int dias, CancellationToken ct = default) => PedirAsync<QualidadePainelDto>(HttpMethod.Get, $"api/admin/qualidade?dias={dias}", ct);
    public Task<List<ComunicadoAdminDto>> ComunicadosAdminAsync(CancellationToken ct = default) => PedirAsync<List<ComunicadoAdminDto>>(HttpMethod.Get, "api/admin/comunicados", ct);
    public Task<ComunicadoDto> CriarComunicadoAsync(NovoComunicadoDto n, CancellationToken ct = default) => PedirAsync<ComunicadoDto>(HttpMethod.Post, "api/admin/comunicados", ct, JsonContent.Create(n));
    public Task CancelarComunicadoAsync(long id, CancellationToken ct = default) => EnviarAsync(HttpMethod.Delete, $"api/admin/comunicados/{id}", null, ct);
    public Task<List<string>> UnidadesAsync(CancellationToken ct = default) => PedirAsync<List<string>>(HttpMethod.Get, "api/admin/unidades", ct);
    public Task<List<string>> DefinirAdminsAsync(string[] extras, CancellationToken ct = default) => PedirAsync<List<string>>(HttpMethod.Put, "api/admin/admins", ct, JsonContent.Create(extras));
    public Task<List<RamalAdminDto>> RamaisAdminAsync(CancellationToken ct = default) => PedirAsync<List<RamalAdminDto>>(HttpMethod.Get, "api/admin/ramais-app", ct);
    /// <summary>1.2.8: ramal e senha do SIP que a TI gravou no AD para MIM (o servidor lê na hora; nada fica guardado nele).</summary>
    public Task<ContaSipAdDto> ContaSipAdAsync(CancellationToken ct = default) => PedirAsync<ContaSipAdDto>(HttpMethod.Get, "api/conta-sip", ct);

    private async Task BaixarAsync(string caminho, string destino, CancellationToken ct)
    {
        using var req = await PedidoAsync(HttpMethod.Get, caminho, ct).ConfigureAwait(false);
        using var resp = await _httpLongo.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        await Conferir(resp, ct).ConfigureAwait(false);
        await SalvarAsync(resp, destino, ct).ConfigureAwait(false);
    }

    // ----- 1.2.5: provedores de IA (painel) -----
    public Task<ProvedoresIaDto> ProvedoresIaAsync(CancellationToken ct = default) =>
        PedirAsync<ProvedoresIaDto>(HttpMethod.Get, "api/admin/ia/provedores", ct);
    public Task<ProvedoresIaDto> SalvarProvedorIaAsync(SalvarProvedorIaDto d, CancellationToken ct = default) =>
        PedirAsync<ProvedoresIaDto>(HttpMethod.Post, "api/admin/ia/provedores", ct, JsonContent.Create(d));
    public Task<ProvedoresIaDto> ExcluirProvedorIaAsync(string id, CancellationToken ct = default) =>
        PedirAsync<ProvedoresIaDto>(HttpMethod.Delete, "api/admin/ia/provedores/" + Uri.EscapeDataString(id), ct);
    public Task<ProvedoresIaDto> OrdenarProvedoresIaAsync(OrdemProvedoresIaDto d, CancellationToken ct = default) =>
        PedirAsync<ProvedoresIaDto>(HttpMethod.Put, "api/admin/ia/provedores/ordem", ct, JsonContent.Create(d));
    public Task<TesteIaDto> TestarProvedorIaAsync(string id, CancellationToken ct = default) =>
        PedirAsync<TesteIaDto>(HttpMethod.Post, $"api/admin/ia/provedores/{Uri.EscapeDataString(id)}/testar", ct, longo: true);
    public Task<List<string>> BuscarModelosIaAsync(BuscarModelosIaDto d, CancellationToken ct = default) =>
        PedirAsync<List<string>>(HttpMethod.Post, "api/admin/ia/modelos", ct, JsonContent.Create(d));

    /// <summary>1.2.4: grupos do AD cujo nome/descrição contém <paramref name="busca"/> (vazio = os primeiros). Só admin.</summary>
    public Task<List<GrupoAdDto>> BuscarGruposAdAsync(string busca, CancellationToken ct = default) =>
        PedirAsync<List<GrupoAdDto>>(HttpMethod.Get, "api/admin/diretorio/grupos?busca=" + Uri.EscapeDataString(busca ?? ""), ct);

    /// <summary>Hub 1.2.3: grupos do AD cujos membros são administradores (substitui a lista).</summary>
    public Task<List<GrupoAdminDto>> DefinirGruposAdminAsync(string[] grupos, CancellationToken ct = default) =>
        PedirAsync<List<GrupoAdminDto>>(HttpMethod.Put, "api/admin/grupos-admin", ct, JsonContent.Create(grupos));
    /// <summary>Hub 1.2.3: lê o AD agora, sem esperar a sincronização de 30 em 30 minutos.</summary>
    public Task<SincronizacaoDto> SincronizarDiretorioAsync(CancellationToken ct = default) =>
        PedirAsync<SincronizacaoDto>(HttpMethod.Post, "api/admin/diretorio/sincronizar", ct, longo: true);

    private static string TipoPorExtensao(string caminho) => Path.GetExtension(caminho).ToLowerInvariant() switch
    {
        ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".gif" => "image/gif", ".pdf" => "application/pdf",
        ".txt" => "text/plain", ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document", _ => "application/octet-stream",
    };

    // ========================= interno =========================

    private void Mudar(EstadoHub e, string? motivo)
    {
        Estado = e;
        Motivo = motivo;
        Registrar($"Hub: {e} {motivo}");
        EstadoMudou?.Invoke(e, motivo);
    }

    private static string Traduzir(Exception ex) => ex switch
    {
        HttpRequestException { StatusCode: null } => "Servidor do chat fora do ar ou inacessível.",
        HttpRequestException h => $"Servidor do chat respondeu {(int?)h.StatusCode}.",
        TaskCanceledException => "Servidor do chat não respondeu a tempo.",
        _ => ex.Message,
    };

    private void Registrar(string m) => Log?.Invoke(m);

    public async ValueTask DisposeAsync()
    {
        _fim.Cancel();
        if (_con != null) { try { await _con.DisposeAsync().ConfigureAwait(false); } catch { } }
        _http.Dispose();
        _httpLongo.Dispose();
    }
}
