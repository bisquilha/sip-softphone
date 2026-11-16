using System.Net;
using System.Text;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;
using SIPSorceryMedia.Abstractions;

namespace Softphone.Core;

/// <summary>
/// Motor do softphone: registro no PABX, chamadas de entrada e saída, espera, transferência e DTMF.
/// Uma instância por conta. Os eventos chegam em threads da biblioteca; a tela despacha para a UI.
/// </summary>
public sealed class Telefone : IAsyncDisposable
{
    public const string NomeAgente = "Softphone";

    private readonly IFabricaAudio _fabricaAudio;
    private readonly List<Chamada> _chamadas = new();
    private readonly object _trava = new();
    private SIPTransport? _transporte;
    private SIPChannel? _canal;
    private SIPRegistrationUserAgent? _registro;
    private SIPEndPoint? _proxySaida;
    private SIPEndPoint? _servidorEP;
    private Timer? _manterAtivo;
    private ContaSip _conta = new();

    private readonly Timer _relogioQualidade;

    public Telefone(IFabricaAudio fabricaAudio, Toques? toques = null)
    {
        _fabricaAudio = fabricaAudio;
        Toques = toques ?? new Toques();
        _relogioQualidade = new Timer(_ => AtualizarQualidade(), null, 1000, 1000);
    }

    /// <summary>A qualidade de uma chamada conectada foi recalculada (1 vez por segundo).</summary>
    public event Action<Chamada>? QualidadeMudou;

    private void AtualizarQualidade()
    {
        foreach (var c in Chamadas.Where(x => x.Conectada))
        {
            try
            {
                c.Medidor.Pausado = c.Estado == EstadoChamada.EmEspera;
                c.Qualidade = c.Medidor.Instante();
                QualidadeMudou?.Invoke(c);
            }
            catch (Exception ex) { Registrar("Qualidade: " + ex.Message); }
        }
    }

    // =====================================================================
    // Conferência a três (mistura no próprio app)
    // =====================================================================

    /// <summary>
    /// Junta duas chamadas conectadas numa conferência: as duas saem da espera, cada lado ouve o outro e eu ouço os dois.
    /// A primeira vira a "principal" (o microfone e o alto-falante são os dela).
    /// </summary>
    public async Task<bool> ConferenciaAsync(Chamada a, Chamada b)
    {
        if (a == b || !a.Conectada || !b.Conectada || a.Audio == null || b.Audio == null || a.EmConferencia || b.EmConferencia) return false;
        await RetomarAsync(a).ConfigureAwait(false);
        await RetomarAsync(b).ConfigureAwait(false);
        a.ConsultaDe = null; b.ConsultaDe = null;   // virou conferência: não é mais transferência assistida
        b.Mudo = a.Mudo;
        a.Audio.IniciarConferencia(b.Audio);
        a.ConferenciaCom = b; b.ConferenciaCom = a;
        Registrar($"Conferência: {a.Numero} + {b.Numero}");
        ChamadaMudou?.Invoke(a);
        ChamadaMudou?.Invoke(b);
        return true;
    }

    /// <summary>Desfaz a conferência: as duas continuam; a outra vai para a espera.</summary>
    public async Task SepararConferenciaAsync(Chamada c)
    {
        var outra = DesfazerConferencia(c);
        if (outra != null && outra.Estado == EstadoChamada.EmAndamento) await PorEmEsperaAsync(outra).ConfigureAwait(false);
    }

    private Chamada? DesfazerConferencia(Chamada c)
    {
        var outra = c.ConferenciaCom;
        if (outra == null) return null;
        c.Audio?.EncerrarConferencia();
        outra.Audio?.EncerrarConferencia();
        c.ConferenciaCom = null; outra.ConferenciaCom = null;
        Registrar($"Conferência desfeita: {c.Numero} / {outra.Numero}");
        ChamadaMudou?.Invoke(c);
        ChamadaMudou?.Invoke(outra);
        return outra;
    }

    public Toques Toques { get; }
    public ContaSip Conta => _conta;
    public EstadoRegistro EstadoRegistro { get; private set; } = EstadoRegistro.Desligado;
    public string? MotivoRegistro { get; private set; }

    /// <summary>Não perturbe: recusa as chamadas recebidas com "ocupado".</summary>
    public bool NaoPerturbe { get; set; }
    /// <summary>Números bloqueados (comparação só pelos dígitos).</summary>
    public Func<string, bool>? Bloqueado { get; set; }
    /// <summary>Grava o tráfego SIP no log (diagnóstico).</summary>
    public bool LogSip { get; set; }
    /// <summary>Faixa de portas RTP (0 = qualquer uma livre). Útil quando o firewall libera só um intervalo.</summary>
    public int RtpPortaInicial { get; set; }
    public int RtpPortaFinal { get; set; }
    /// <summary>Encaminhar TODA chamada recebida para este número (vazio = não).</summary>
    public string? EncaminharSempre { get; set; }
    /// <summary>Encaminhar quando já estou em outra chamada.</summary>
    public string? EncaminharSeOcupado { get; set; }
    /// <summary>Encaminhar se não atender em <see cref="EncaminharSegundos"/>.</summary>
    public string? EncaminharSeNaoAtender { get; set; }
    public int EncaminharSegundos { get; set; } = 20;

    public event Action<EstadoRegistro, string?>? RegistroMudou;
    public event Action<Chamada>? ChamadaNova;
    /// <summary>1.3: escolhe o toque da chamada que está chegando: (preset, arquivo .wav ou null). Null = toque padrão geral.</summary>
    public Func<string, (string? Preset, string? Arquivo)>? EscolherToque { get; set; }
    public event Action<Chamada>? ChamadaMudou;
    public event Action<Chamada>? ChamadaEncerrada;
    /// <summary>Chamada recebida e recusada automaticamente (não perturbe / bloqueio): entra no histórico.</summary>
    public event Action<string, string?, ResultadoChamada>? ChamadaRecusadaAuto;
    public event Action<string>? Log;
    /// <summary>Microfone/alto-falante não abriu numa chamada (1 aviso por chamada). Texto já pronto para o usuário.</summary>
    public event Action<Chamada, string>? ProblemaAudio;
    /// <summary>Gravação terminada (o usuário parou ou a ligação acabou): caminho do WAV estéreo, início e duração.</summary>
    public event Action<Chamada, string, DateTime, TimeSpan>? GravacaoConcluida;

    /// <summary>Começa a gravar a chamada. Com <paramref name="aviso16k"/>, os dois lados ouvem o aviso falado.</summary>
    public bool IniciarGravacao(Chamada c, string pastaTemporaria, short[]? aviso16k)
    {
        if (!c.Conectada || c.Audio == null || c.Gravador != null) return false;
        var g = new GravadorChamada(pastaTemporaria, c.Codec == "G722" ? 16000 : 8000, LimiteGravacaoBytes);
        if (Interlocked.CompareExchange(ref c.Gravador, g, null) != null) { g.Dispose(); return false; }
        c.Audio.Gravador = g;
        if (aviso16k is { Length: > 0 }) c.Audio.TocarAviso(aviso16k);
        Registrar($"Gravação iniciada: {c.Numero}");
        ChamadaMudou?.Invoke(c);
        return true;
    }

    public void PararGravacao(Chamada c) => ConcluirGravacao(c, avisar: true);

    /// <summary>Tamanho máximo do WAV que o Hub aceita (o app atualiza com o que o Hub informa).</summary>
    public long LimiteGravacaoBytes { get; set; } = GravadorChamada.LimitePadraoBytes;

    /// <summary>1.3: liga a captura de fala (legenda ao vivo) nesta chamada. O áudio não vai para disco. Null se a chamada não está conectada.</summary>
    public CapturaLegenda? IniciarLegenda(Chamada c)
    {
        if (!c.Conectada || c.Audio == null) return null;
        if (c.Legenda is { } ja) return ja;
        var cap = new CapturaLegenda();
        c.Legenda = cap; c.Audio.Legenda = cap;
        return cap;
    }

    public void PararLegenda(Chamada c)
    {
        var cap = c.Legenda; c.Legenda = null;
        try { if (c.Audio != null) c.Audio.Legenda = null; } catch { }
        cap?.Parar();
    }

    // R05 (revisão do Codex): montar o WAV e gravar os dados roda em segundo plano depois de desligar; sair ou reiniciar
    // para a versão nova precisa esperar isso terminar, senão a gravação fica pela metade ou sem os dados.
    private readonly HashSet<Task> _salvando = new();
    public int GravacoesSendoSalvas { get { lock (_salvando) return _salvando.Count; } }

    /// <summary>Espera as gravações que estão sendo salvas (no máximo <paramref name="maximo"/>).</summary>
    public async Task AguardarGravacoesAsync(TimeSpan maximo)
    {
        Task[] t;
        lock (_salvando) t = _salvando.ToArray();
        if (t.Length > 0) await Task.WhenAny(Task.WhenAll(t), Task.Delay(maximo)).ConfigureAwait(false);
    }

    private void ConcluirGravacao(Chamada c, bool avisar)
    {
        var g = Interlocked.Exchange(ref c.Gravador, null);
        if (g == null) return;
        if (c.Audio != null) c.Audio.Gravador = null;
        var tarefa = new Task(() =>
        {
            try
            {
                var wav = g.Concluir();
                Registrar($"Gravação concluída: {c.Numero}, {g.Duracao:mm\\:ss}");
                GravacaoConcluida?.Invoke(c, wav, g.Inicio, g.Duracao);
            }
            catch (Exception ex) { Registrar("Falha ao salvar a gravação: " + ex.Message); }
            finally { g.Dispose(); }
        });
        lock (_salvando) _salvando.Add(tarefa);   // registrada antes de começar: quem perguntar logo depois já vê
        tarefa.ContinueWith(t => { lock (_salvando) _salvando.Remove(t); }, TaskScheduler.Default);
        tarefa.Start(TaskScheduler.Default);
        if (avisar) ChamadaMudou?.Invoke(c);
    }

    public IReadOnlyList<Chamada> Chamadas { get { lock (_trava) return _chamadas.ToList(); } }

    // =====================================================================
    // Registro
    // =====================================================================

    public async Task IniciarAsync(ContaSip conta)
    {
        await PararAsync().ConfigureAwait(false);
        _conta = conta;
        if (!conta.Valida) { MudarRegistro(EstadoRegistro.Falha, "Conta incompleta: informe servidor e usuário."); return; }

        var tcp = string.Equals(conta.Transporte, "TCP", StringComparison.OrdinalIgnoreCase);
        var protocolo = tcp ? SIPProtocolsEnum.tcp : SIPProtocolsEnum.udp;

        _transporte = new SIPTransport();
        var local = new IPEndPoint(IPAddress.Any, conta.PortaLocal);
        _canal = tcp ? new SIPTCPChannel(local) : new SIPUDPChannel(local);
        _transporte.AddSIPChannel(_canal);
        _transporte.SIPTransportRequestReceived += RequisicaoRecebida;
        if (LogSip)
        {
            _transporte.SIPRequestOutTraceEvent += (l, r, req) => Registrar($">>> {r}\n{req}");
            _transporte.SIPRequestInTraceEvent += (l, r, req) => Registrar($"<<< {r}\n{req}");
            _transporte.SIPResponseOutTraceEvent += (l, r, resp) => Registrar($">>> {r}\n{resp}");
            _transporte.SIPResponseInTraceEvent += (l, r, resp) => Registrar($"<<< {r}\n{resp}");
        }

        _proxySaida = string.IsNullOrWhiteSpace(conta.Proxy) ? null : ParaEndPoint(conta.Proxy, protocolo);
        _servidorEP = ParaEndPoint(conta.Servidor, protocolo);

        var aor = SIPURI.ParseSIPURIRelaxed($"{conta.Usuario.Trim()}@{conta.DominioEfetivo}");
        var contato = new SIPURI(SIPSchemesEnum.sip, new SIPEndPoint(protocolo, IPAddress.Any, 0)) { User = conta.Usuario.Trim() };
        _registro = new SIPRegistrationUserAgent(
            _transporte, _proxySaida, aor, conta.LoginEfetivo, conta.Senha, null, conta.Servidor.Trim(), contato,
            Math.Max(60, conta.RegistroSegundos), null,
            maxRegistrationAttemptTimeout: 30, registerFailureRetryInterval: 30, maxRegisterAttempts: int.MaxValue,
            exitOnUnequivocalFailure: true)
        {
            UserAgent = NomeAgente,
            UserDisplayName = string.IsNullOrWhiteSpace(conta.NomeExibicao) ? null : conta.NomeExibicao,
        };
        _registro.RegistrationSuccessful += (_, _) => MudarRegistro(EstadoRegistro.Registrado, null);
        _registro.RegistrationTemporaryFailure += (_, resp, erro) => MudarRegistro(EstadoRegistro.Falha, TraduzirRegistro(resp, erro) + " Tentando de novo...");
        _registro.RegistrationFailed += (_, resp, erro) => MudarRegistro(EstadoRegistro.Falha, TraduzirRegistro(resp, erro));
        _registro.RegistrationRemoved += (_, _) => MudarRegistro(EstadoRegistro.Desligado, null);

        MudarRegistro(EstadoRegistro.Registrando, null);
        _registro.Start();

        if (conta.ManterAtivoSegundos > 0 && !tcp)
        {
            var periodo = TimeSpan.FromSeconds(conta.ManterAtivoSegundos);
            _manterAtivo = new Timer(_ => EnviarKeepAlive(), null, periodo, periodo);
        }
    }

    public async Task PararAsync()
    {
        _manterAtivo?.Dispose();
        _manterAtivo = null;
        foreach (var c in Chamadas) Desligar(c);
        Toques.Parar();

        if (_registro != null)
        {
            try { _registro.Stop(); } catch { }
            await Task.Delay(300).ConfigureAwait(false); // dá tempo do REGISTER Expires: 0 sair
            _registro = null;
        }
        if (_transporte != null)
        {
            _transporte.SIPTransportRequestReceived -= RequisicaoRecebida;
            try { _transporte.Shutdown(); } catch { }
            _transporte = null;
            _canal = null;
        }
        if (EstadoRegistro != EstadoRegistro.Desligado) MudarRegistro(EstadoRegistro.Desligado, null);
    }

    private void EnviarKeepAlive()
    {
        try
        {
            if (_canal == null || _servidorEP == null) return;
            var destino = _proxySaida ?? _servidorEP;
            _ = _canal.SendAsync(destino, Encoding.ASCII.GetBytes("\r\n\r\n"), true);
        }
        catch { }
    }

    private void MudarRegistro(EstadoRegistro estado, string? motivo)
    {
        EstadoRegistro = estado;
        MotivoRegistro = motivo;
        Registrar($"Registro: {estado} {motivo}");
        RegistroMudou?.Invoke(estado, motivo);
    }

    private static string TraduzirRegistro(SIPResponse? resp, string? erro)
    {
        if (resp == null) return string.IsNullOrWhiteSpace(erro) ? "Sem resposta do servidor." : "Sem resposta do servidor (" + erro + ").";
        return resp.StatusCode switch
        {
            401 or 407 => "Usuário ou senha inválidos.",
            403 => "Acesso negado pelo PABX (usuário/senha ou IP não autorizado).",
            404 => "Ramal não encontrado no PABX.",
            408 => "O PABX não respondeu a tempo.",
            503 => "PABX indisponível.",
            _ => $"Falha no registro ({resp.StatusCode} {resp.ReasonPhrase}).",
        };
    }

    private static SIPEndPoint ParaEndPoint(string host, SIPProtocolsEnum protocolo)
    {
        host = host.Trim();
        if (host.StartsWith("sip:", StringComparison.OrdinalIgnoreCase)) host = host[4..];
        if (!host.Contains(':')) host += ":5060";
        var ep = SIPEndPoint.ParseSIPEndPoint($"{protocolo}:{host}");
        if (ep != null) return ep;
        // nome DNS: resolve agora (o PABX atual é IP fixo, mas deixa preparado)
        var partes = host.Split(':');
        var ip = Dns.GetHostAddresses(partes[0]).First(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
        return new SIPEndPoint(protocolo, ip, int.Parse(partes[1]));
    }

    // =====================================================================
    // Requisições fora de diálogo (chamadas novas, OPTIONS, NOTIFY...)
    // =====================================================================

    private Task RequisicaoRecebida(SIPEndPoint local, SIPEndPoint remoto, SIPRequest req)
    {
        if (req.Header.To?.ToTag != null) return Task.CompletedTask; // dentro de um diálogo: cada SIPUserAgent cuida do seu
        try
        {
            switch (req.Method)
            {
                case SIPMethodsEnum.INVITE:
                    ReceberChamada(req);
                    break;
                case SIPMethodsEnum.OPTIONS:
                case SIPMethodsEnum.NOTIFY:   // ex.: correio de voz (message-summary)
                case SIPMethodsEnum.MESSAGE:
                    var ok = SIPResponse.GetResponse(req, SIPResponseStatusCodesEnum.Ok, null);
                    ok.Header.Allow = "INVITE, ACK, CANCEL, BYE, REFER, NOTIFY, OPTIONS, INFO, MESSAGE";
                    ok.Header.UserAgent = NomeAgente;
                    _ = _transporte?.SendResponseAsync(ok);
                    break;
            }
        }
        catch (Exception ex)
        {
            Registrar("Erro tratando " + req.Method + ": " + ex.Message);
        }
        return Task.CompletedTask;
    }

    private void ReceberChamada(SIPRequest req)
    {
        if (_transporte == null) return;
        string numero = req.Header.From?.FromURI?.User ?? req.Header.From?.FromURI?.Host ?? "desconhecido";
        string? nome = req.Header.From?.FromName;

        var ua = NovoUA();
        if (NaoPerturbe || (Bloqueado?.Invoke(numero) ?? false))
        {
            var recusa = ua.AcceptCall(req);
            recusa.Reject(SIPResponseStatusCodesEnum.BusyHere, null, null);
            ua.Dispose();
            ChamadaRecusadaAuto?.Invoke(numero, nome, NaoPerturbe ? ResultadoChamada.Recusada : ResultadoChamada.Recusada);
            Registrar($"Chamada de {numero} recusada automaticamente ({(NaoPerturbe ? "não perturbe" : "bloqueado")}).");
            return;
        }

        // encaminhamento (302): sempre, ou se já tem chamada em andamento
        bool ocupado = Chamadas.Any(x => x.Ativa);
        var destinoEncaminhar = !string.IsNullOrWhiteSpace(EncaminharSempre) ? EncaminharSempre
            : ocupado && !string.IsNullOrWhiteSpace(EncaminharSeOcupado) ? EncaminharSeOcupado : null;
        if (destinoEncaminhar != null)
        {
            var redir = ua.AcceptCall(req);
            redir.Redirect(SIPResponseStatusCodesEnum.MovedTemporarily, SIPURI.ParseSIPURIRelaxed($"{LimparDestino(destinoEncaminhar)}@{_conta.DominioEfetivo}"));
            ua.Dispose();
            ChamadaRecusadaAuto?.Invoke(numero, nome, ResultadoChamada.Encaminhada);
            Registrar($"Chamada de {numero} encaminhada para {destinoEncaminhar}.");
            return;
        }

        var c = new Chamada(ua, DirecaoChamada.Entrada, numero, nome);
        c.Uas = ua.AcceptCall(req);
        if (!string.IsNullOrWhiteSpace(EncaminharSeNaoAtender))
        {
            var para = EncaminharSeNaoAtender;
            _ = Task.Delay(TimeSpan.FromSeconds(Math.Clamp(EncaminharSegundos, 5, 120))).ContinueWith(_ =>
            {
                if (c.Estado != EstadoChamada.Recebendo || c.Uas == null) return;
                try { c.Uas.Redirect(SIPResponseStatusCodesEnum.MovedTemporarily, SIPURI.ParseSIPURIRelaxed($"{LimparDestino(para)}@{_conta.DominioEfetivo}")); } catch { }
                Encerrar(c, ResultadoChamada.Encaminhada, $"Encaminhada para {para}");
            }, TaskScheduler.Default);
        }
        c.Uas.CallCancelled += (_, _) => Encerrar(c, ResultadoChamada.Perdida, "Chamada perdida");
        ua.OnCallHungup += _ => Encerrar(c, ResultadoChamada.Atendida, c.MotivoLocal ?? "Encerrada pelo outro lado");
        LigarEventosComuns(c);

        bool haOutra;
        lock (_trava) { haOutra = _chamadas.Any(x => x.Ativa); _chamadas.Add(c); }
        c.Uas.Progress(SIPResponseStatusCodesEnum.Ringing, null, null, null, null);
        if (!haOutra && EscolherToque != null)
        {
            // 1.3: toque por tipo de chamada (interna, externa, fila ou setor); o app decide pelo número
            try { var (preset, arquivo) = EscolherToque(c.Numero); Toques.Preset = preset; Toques.ArquivoCampainha = arquivo; } catch { }
        }
        Toques.Tocar(haOutra ? TipoToque.ChamadaEmEspera : TipoToque.Campainha);
        ChamadaNova?.Invoke(c);
    }

    // =====================================================================
    // Ações
    // =====================================================================

    /// <summary>Liga para um número/ramal. Coloca em espera a chamada em andamento, se houver.</summary>
    public async Task<Chamada> LigarAsync(string destino)
    {
        if (_transporte == null) throw new InvalidOperationException("Telefone não iniciado.");
        destino = LimparDestino(destino);
        if (destino.Length == 0) throw new ArgumentException("Informe o número.");

        await ColocarOutrasEmEsperaAsync(null).ConfigureAwait(false);

        var ua = NovoUA();
        var c = new Chamada(ua, DirecaoChamada.Saida, destino, null);
        LigarEventosComuns(c);

        ua.ClientCallTrying += (_, _) => Mudar(c, EstadoChamada.Discando);
        ua.ClientCallRinging += (_, resp) =>
        {
            Mudar(c, EstadoChamada.Chamando);
            // 180 sem áudio: toca o retorno local. 183 com SDP: o PABX manda o áudio (early media).
            bool temMidia = resp != null && !string.IsNullOrEmpty(resp.Body);
            Toques.Tocar(temMidia ? TipoToque.Nenhum : TipoToque.Retorno);
        };
        ua.ClientCallFailed += (_, erro, resp) =>
        {
            var (res, motivo) = Volatile.Read(ref c.Encerrando) == 1 ? (ResultadoChamada.Cancelada, "Cancelada") : TraduzirFalha(resp, erro);
            if (res == ResultadoChamada.Ocupado && Interlocked.CompareExchange(ref c.Encerrando, 0, 0) == 0) Toques.Tocar(TipoToque.Ocupado);
            Encerrar(c, res, motivo, manterToque: res == ResultadoChamada.Ocupado);
        };
        ua.OnCallHungup += _ => Encerrar(c, ResultadoChamada.Atendida, c.MotivoLocal ?? "Encerrada pelo outro lado");

        lock (_trava) _chamadas.Add(c);
        ChamadaNova?.Invoke(c);

        var uri = $"sip:{destino}@{_conta.DominioEfetivo}";
        var exib = string.IsNullOrWhiteSpace(_conta.NomeExibicao) ? "" : $"\"{_conta.NomeExibicao.Replace("\"", "")}\" ";
        var de = $"{exib}<sip:{_conta.Usuario.Trim()}@{_conta.DominioEfetivo}>";
        var descritor = new SIPCallDescriptor(_conta.LoginEfetivo, _conta.Senha, uri, de, uri, null, null,
            _conta.LoginEfetivo, SIPCallDirection.Out, SDP.SDP_MIME_CONTENTTYPE, null, null);

        c.Midia = NovaMidia(c);
        _ = Task.Run(async () =>
        {
            try
            {
                bool atendeu = await ua.Call(descritor, c.Midia).ConfigureAwait(false);
                if (atendeu && c.Ativa)
                {
                    Toques.Parar();
                    c.Atendida = DateTime.Now;
                    c.Resultado = ResultadoChamada.Atendida;
                    Mudar(c, EstadoChamada.EmAndamento);
                    GravarSeAutomatico(c);
                }
                else if (c.Ativa && Interlocked.CompareExchange(ref c.Encerrando, 0, 0) == 0)
                {
                    Encerrar(c, ResultadoChamada.Falhou, "Não completada");
                }
            }
            catch (Exception ex)
            {
                Encerrar(c, ResultadoChamada.Falhou, "Erro: " + ex.Message);
            }
        });
        return c;
    }

    /// <summary>Atende uma chamada recebida. Coloca em espera a que estiver em andamento.</summary>
    public async Task AtenderAsync(Chamada c)
    {
        if (c.Estado != EstadoChamada.Recebendo || c.Uas == null) return;
        Toques.Parar();
        await ColocarOutrasEmEsperaAsync(c).ConfigureAwait(false);
        Mudar(c, EstadoChamada.Conectando);
        c.Midia = NovaMidia(c);
        bool ok;
        try { ok = await c.UA.Answer(c.Uas, c.Midia).ConfigureAwait(false); }
        catch (Exception ex) { Encerrar(c, ResultadoChamada.Falhou, "Erro ao atender: " + ex.Message); return; }
        if (!ok) { Encerrar(c, ResultadoChamada.Falhou, "Não foi possível atender"); return; }
        c.Atendida = DateTime.Now;
        c.Resultado = ResultadoChamada.Atendida;
        Mudar(c, EstadoChamada.EmAndamento);
        GravarSeAutomatico(c);
    }

    // ----- 1.1.3: gravar todas as ligações sozinho (opção da pessoa nas Configurações) -----

    /// <summary>Toda ligação atendida (feita ou recebida) começa a gravar sozinha, com o aviso.</summary>
    public bool GravarAutomaticamente { get; set; }
    /// <summary>Onde ficam as gravações até irem para o servidor.</summary>
    public string? PastaGravacaoAutomatica { get; set; }
    public short[]? AvisoGravacaoAutomatica { get; set; }
    /// <summary>O app diz se dá para gravar agora (a gravação precisa do servidor do chat). Null = sempre.</summary>
    public Func<bool>? PodeGravarAutomaticamente { get; set; }

    private void GravarSeAutomatico(Chamada c)
    {
        if (!GravarAutomaticamente || PastaGravacaoAutomatica is null || c.Gravando) return;
        if (PodeGravarAutomaticamente?.Invoke() == false) { Registrar($"Gravação automática: servidor do chat indisponível, {c.Numero} não foi gravada."); return; }
        if (IniciarGravacao(c, PastaGravacaoAutomatica, AvisoGravacaoAutomatica)) Registrar($"Gravação automática: {c.Numero}");
    }

    /// <summary>Recusa uma chamada recebida (ocupado).</summary>
    public void Recusar(Chamada c)
    {
        if (c.Estado != EstadoChamada.Recebendo || c.Uas == null) return;
        try { c.Uas.Reject(SIPResponseStatusCodesEnum.BusyHere, null, null); } catch { }
        Encerrar(c, ResultadoChamada.Recusada, "Recusada");
    }

    /// <summary>Desliga / cancela / recusa, conforme o estado.</summary>
    public void Desligar(Chamada c)
    {
        if (!c.Ativa) { if (Toques.Atual == TipoToque.Ocupado) Toques.Parar(); return; }
        try
        {
            switch (c.Estado)
            {
                case EstadoChamada.Recebendo:
                    Recusar(c);
                    return;
                case EstadoChamada.Discando:
                case EstadoChamada.Chamando:
                    Interlocked.Exchange(ref c.Encerrando, 1);
                    c.UA.Cancel();
                    Encerrar(c, ResultadoChamada.Cancelada, "Cancelada");
                    return;
                default:
                    Interlocked.Exchange(ref c.Encerrando, 1);
                    c.MotivoLocal ??= "Encerrada";
                    c.UA.Hangup();
                    Encerrar(c, ResultadoChamada.Atendida, "Encerrada");
                    return;
            }
        }
        catch (Exception ex)
        {
            Registrar("Erro ao desligar: " + ex.Message);
            Encerrar(c, c.Resultado == ResultadoChamada.Nenhum ? ResultadoChamada.Cancelada : c.Resultado, "Encerrada");
        }
    }

    public void AlternarMudo(Chamada c)
    {
        c.Mudo = !c.Mudo;
        // na conferência o microfone é um só: muda as duas pernas juntas
        if (c.ConferenciaCom is { } outra) { outra.Mudo = c.Mudo; ChamadaMudou?.Invoke(outra); }
        ChamadaMudou?.Invoke(c);
    }

    /// <summary>Coloca em espera ou retoma (retomar coloca as outras em espera).</summary>
    public async Task AlternarEsperaAsync(Chamada c)
    {
        if (c.Estado == EstadoChamada.EmAndamento) await PorEmEsperaAsync(c).ConfigureAwait(false);
        else if (c.Estado == EstadoChamada.EmEspera)
        {
            await ColocarOutrasEmEsperaAsync(c).ConfigureAwait(false);
            await RetomarAsync(c).ConfigureAwait(false);
        }
    }

    private Task PorEmEsperaAsync(Chamada c)
    {
        if (c.Estado != EstadoChamada.EmAndamento) return Task.CompletedTask;
        DesfazerConferencia(c); // espera numa perna desfaz a conferência (a outra perna segue, ou vai junto para a espera)
        c.UA.PutOnHold();
        if (c.Audio != null) c.Audio.Pausado = true;
        Mudar(c, EstadoChamada.EmEspera);
        return Task.CompletedTask;
    }

    private Task RetomarAsync(Chamada c)
    {
        if (c.Estado != EstadoChamada.EmEspera) return Task.CompletedTask;
        c.UA.TakeOffHold();
        if (c.Audio != null) c.Audio.Pausado = false;
        Mudar(c, EstadoChamada.EmAndamento);
        return Task.CompletedTask;
    }

    private async Task ColocarOutrasEmEsperaAsync(Chamada? exceto)
    {
        foreach (var outra in Chamadas.Where(x => x != exceto && x.Estado == EstadoChamada.EmAndamento))
            await PorEmEsperaAsync(outra).ConfigureAwait(false);
    }

    /// <summary>Envia um dígito DTMF (RFC 2833) na chamada.</summary>
    public async Task EnviarDtmfAsync(Chamada c, char tecla)
    {
        byte? codigo = tecla switch
        {
            >= '0' and <= '9' => (byte)(tecla - '0'),
            '*' => 10,
            '#' => 11,
            _ => null,
        };
        if (codigo is null || c.Estado != EstadoChamada.EmAndamento) return;
        Toques.Dtmf(tecla);
        try { await c.UA.SendDtmf(codigo.Value).ConfigureAwait(false); }
        catch (Exception ex) { Registrar("Falha no DTMF: " + ex.Message); }
    }

    /// <summary>Quanto esperar o primeiro aviso de andamento (NOTIFY) depois que o PABX aceita a transferência.
    /// PABX que não manda aviso nenhum: passado esse tempo, a transferência vale como feita.</summary>
    public TimeSpan EsperaAvisoTransferencia { get; set; } = TimeSpan.FromSeconds(1.5);
    /// <summary>Com avisos chegando, quanto esperar o resultado final (o destino atender, ou dar ocupado/inexistente).</summary>
    public TimeSpan EsperaResultadoTransferencia { get; set; } = TimeSpan.FromSeconds(8);

    /// <summary>Transferência direta (cega): a chamada vai para o destino e esta linha é liberada.
    /// 1.1.3: só libera depois do resultado final que o PABX manda (NOTIFY). Se o destino der ocupado/inexistente, a
    /// chamada continua com quem transferiu (antes o app desligava assim que o PABX aceitava o pedido e a ligação se perdia).</summary>
    public async Task<ResultadoTransferencia> TransferirAsync(Chamada c, string destino)
    {
        destino = LimparDestino(destino);
        if (!c.Conectada || destino.Length == 0) return new(false, "Ligação ou destino inválido.");
        if (string.Equals(LimparDestino(c.Numero), destino, StringComparison.OrdinalIgnoreCase))
            return new(false, $"{destino} é o número de quem está na ligação. Escolha outro destino.");
        Registrar($"Transferência pedida: {c.Numero} -> {destino}");
        var uri = SIPURI.ParseSIPURIRelaxed($"{destino}@{_conta.DominioEfetivo}");
        var r = await TransferirEsperandoAsync(c, async ct =>
            await c.UA.BlindTransfer(uri, TimeSpan.FromSeconds(15), ct, null, _conta.LoginEfetivo, _conta.Senha).ConfigureAwait(false)).ConfigureAwait(false);
        if (r.Ok)
        {
            c.Resultado = ResultadoChamada.Transferida;
            c.MotivoLocal = $"Transferida para {destino}";
            if (c.Ativa) { Interlocked.Exchange(ref c.Encerrando, 1); try { c.UA.Hangup(); } catch { } }
            Encerrar(c, ResultadoChamada.Transferida, $"Transferida para {destino}");
        }
        else Registrar($"Transferência para {destino} não concluída: {r.Motivo}");
        return r;
    }

    /// <summary>Manda o pedido de transferência (REFER) e espera o resultado final que o PABX avisa por NOTIFY.
    /// Enquanto espera, a chamada fica marcada como transferida: se o PABX desligar esta perna, é sinal de que deu certo.</summary>
    private async Task<ResultadoTransferencia> TransferirEsperandoAsync(Chamada c, Func<CancellationToken, Task<bool>> enviar)
    {
        var algumAviso = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var final = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        void AoAvisar(string s)
        {
            algumAviso.TrySetResult();
            var m = System.Text.RegularExpressions.Regex.Match(s ?? "", @"SIP/2\.0\s+(\d{3})");
            if (m.Success && int.Parse(m.Groups[1].Value) is var codigo && codigo >= 200) final.TrySetResult(codigo);
        }
        c.UA.OnTransferNotify += AoAvisar;
        var antes = c.Resultado;
        try
        {
            bool aceito;
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                aceito = await enviar(cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) { Registrar("Falha na transferência: " + ex.Message); aceito = false; }
            if (!aceito) return new(false, "O PABX não aceitou a transferência.");

            c.Resultado = ResultadoChamada.Transferida; // se o PABX desligar esta perna enquanto espera, ficou transferida
            await Task.WhenAny(algumAviso.Task, Task.Delay(EsperaAvisoTransferencia)).ConfigureAwait(false);
            if (algumAviso.Task.IsCompleted && !final.Task.IsCompleted)
                await Task.WhenAny(final.Task, Task.Delay(EsperaResultadoTransferencia)).ConfigureAwait(false);
            if (c.Estado == EstadoChamada.Encerrada) return new(true, null);
            if (final.Task.IsCompleted && final.Task.Result >= 300)
            {
                c.Resultado = antes;
                return new(false, $"O destino não completou a transferência ({DescreverCodigo(final.Task.Result)}).");
            }
            return new(true, null); // 2xx, ou o destino ainda chamando: na transferência direta a linha é liberada
        }
        finally { c.UA.OnTransferNotify -= AoAvisar; }
    }

    private static string DescreverCodigo(int codigo) => codigo switch
    {
        404 or 484 => "ramal ou número inexistente",
        480 => "o destino não está disponível",
        486 or 600 => "o destino está ocupado",
        487 => "cancelada",
        408 => "o destino não atendeu",
        403 or 603 => "o destino recusou",
        _ => $"código {codigo}",
    };

    /// <summary>Começa uma transferência assistida: põe a chamada em espera e liga para o destino (consulta).</summary>
    public async Task<Chamada> IniciarConsultaAsync(Chamada original, string destino)
    {
        var consulta = await LigarAsync(destino).ConfigureAwait(false); // LigarAsync já põe a original em espera
        consulta.ConsultaDe = original;
        ChamadaMudou?.Invoke(consulta);
        return consulta;
    }

    /// <summary>Conclui a transferência assistida: liga a chamada original com a consulta e sai das duas.</summary>
    public async Task<ResultadoTransferencia> ConcluirConsultaAsync(Chamada consulta)
    {
        var original = consulta.ConsultaDe;
        if (original == null || !original.Conectada || !consulta.Conectada || consulta.UA.Dialogue == null) return new(false, "A consulta não está em andamento.");
        Registrar($"Transferência assistida pedida: {original.Numero} -> {consulta.Numero}");
        var dialogo = consulta.UA.Dialogue;
        var r = await TransferirEsperandoAsync(original, async ct =>
            await original.UA.AttendedTransfer(dialogo, TimeSpan.FromSeconds(15), ct, null, _conta.LoginEfetivo, _conta.Senha).ConfigureAwait(false)).ConfigureAwait(false);
        if (!r.Ok) Registrar($"Transferência assistida não concluída: {r.Motivo}");
        bool ok = r.Ok;

        if (ok)
        {
            foreach (var c in new[] { original, consulta })
            {
                c.Resultado = ResultadoChamada.Transferida;
                c.MotivoLocal = "Transferida";
                if (c.Ativa) { Interlocked.Exchange(ref c.Encerrando, 1); try { c.UA.Hangup(); } catch { } }
                Encerrar(c, ResultadoChamada.Transferida, "Transferida");
            }
        }
        return r;
    }

    // =====================================================================
    // Internos
    // =====================================================================

    // Sem proxy configurado, as chamadas saem pelo próprio servidor SIP (o domínio pode não resolver para o PABX).
    private SIPUserAgent NovoUA() => new(_transporte!, _proxySaida ?? _servidorEP);

    private VoIPMediaSession NovaMidia(Chamada c)
    {
        var audio = _fabricaAudio.Criar();
        audio.Mudo = c.Mudo;
        c.Audio = audio;
        var config = new VoIPMediaSessionConfig { MediaEndPoint = new MediaEndPoints { AudioSource = audio, AudioSink = audio } };
        if (RtpPortaInicial > 0 && RtpPortaFinal > RtpPortaInicial) config.RtpPortRange = new SIPSorcery.Sys.PortRange(RtpPortaInicial, RtpPortaFinal);
        var midia = new VoIPMediaSession(config)
        {
            AcceptRtpFromAny = true, // NAT: aceita o RTP de onde ele vier (o PABX pode mandar de outra porta)
        };
        midia.OnRtpEvent += (_, evento, _) => { }; // DTMF recebido: não precisa tratar no softphone
        // qualidade: cada pacote de áudio que chega (perda, jitter) e os relatórios RTCP do PABX (ida e volta)
        midia.OnRtpPacketReceived += (_, tipo, pacote) =>
        {
            if (tipo == SDPMediaTypesEnum.audio) c.Medidor.Pacote(pacote.Header.SequenceNumber, pacote.Header.Timestamp, 8000);
        };
        midia.OnReceiveReport += (_, tipo, rtcp) =>
        {
            if (tipo != SDPMediaTypesEnum.audio) return;
            var rr = rtcp.ReceiverReport?.ReceptionReports?.FirstOrDefault() ?? rtcp.SenderReport?.ReceptionReports?.FirstOrDefault();
            if (rr != null) c.Medidor.RelatorioRemoto(rr.FractionLost, rr.LastSenderReportTimestamp, rr.DelaySinceLastSenderReport);
        };
        midia.OnAudioFormatsNegotiated += formatos => c.Codec = formatos.FirstOrDefault().FormatName;
        // Erro de microfone/alto-falante: vai para o log E para a tela (antes ficava escondido e o outro lado não ouvia).
        int avisado = 0;
        void Problema(string msg)
        {
            Registrar($"Áudio ({c.Numero}): {msg}");
            if (Interlocked.Exchange(ref avisado, 1) == 0) ProblemaAudio?.Invoke(c, msg);
        }
        audio.OnAudioSourceError += Problema;
        audio.OnAudioSinkError += Problema;
        return midia;
    }

    private void LigarEventosComuns(Chamada c)
    {
        c.UA.RemotePutOnHold += () => Registrar($"{c.Numero} colocou a chamada em espera.");
        c.UA.RemoteTookOffHold += () => Registrar($"{c.Numero} retomou a chamada.");
        c.UA.OnTransferNotify += s => Registrar($"Transferência ({c.Numero}): {s?.Trim()}");
    }

    private void Mudar(Chamada c, EstadoChamada estado)
    {
        if (!c.Ativa || c.Estado == estado) return;
        c.Estado = estado;
        ChamadaMudou?.Invoke(c);
    }

    private void Encerrar(Chamada c, ResultadoChamada resultado, string? motivo, bool manterToque = false)
    {
        lock (_trava)
        {
            if (c.Estado == EstadoChamada.Encerrada) return;
            c.Estado = EstadoChamada.Encerrada;
            c.Fim = DateTime.Now;
            if (c.Resultado != ResultadoChamada.Transferida)
            {
                if (c.Atendida != null) c.Resultado = resultado == ResultadoChamada.Transferida ? resultado : ResultadoChamada.Atendida;
                else c.Resultado = resultado;
            }
            c.Motivo = motivo;
            _chamadas.Remove(c);
        }

        // conferência: a outra perna continua sozinha, com o próprio áudio
        DesfazerConferencia(c);

        // Diagnóstico de mídia no log: com isso dá para ver na hora se o áudio saiu e chegou.
        if (c.Atendida != null)
        {
            string destino = "?";
            try { destino = c.Midia?.AudioDestinationEndPoint?.ToString() ?? "?"; } catch { }
            try { c.QualidadeFinal = c.Medidor.Resumo(); } catch { }
            var q = c.QualidadeFinal;
            Registrar($"Mídia {c.Numero}: codec {c.Codec ?? "?"}, RTP para {destino}, enviados {c.Audio?.QuadrosEnviados ?? 0} quadros, recebidos {c.Audio?.PacotesRecebidos ?? 0} pacotes em {c.Duracao:mm\\:ss}; " +
                (q == null ? "qualidade: sem dados" : $"qualidade {QualidadeInstante.NomeNivel(q.Nivel)} (nota média {q.MosMedio:N2}, mínima {q.MosMinimo:N2}, perda {q.PerdaPct:N1}%, variação {q.JitterMedioMs:N0} ms{(q.RttMedioMs is double r ? $", ida e volta {r:N0} ms" : "")})") +
                $"; voz: {c.Audio?.DescricaoVoz ?? "?"}");
        }

        // a ligação acabou: fecha a legenda (o que ficou no meio da fala sai como definitivo) e a gravação, antes de liberar o áudio
        PararLegenda(c);
        ConcluirGravacao(c, avisar: false);

        // A SIPSorcery dispara OnCallHungup de dentro do próprio Hangup (com uma trava interna);
        // liberar o UA aqui causaria deadlock. A limpeza roda fora desse callback.
        var midia = c.Midia;
        var audio = c.Audio;
        var ua = c.UA;
        _ = Task.Run(async () =>
        {
            await Task.Delay(200).ConfigureAwait(false);
            try { midia?.Close("fim"); } catch { }
            try { audio?.Dispose(); } catch { }
            try { ua.Dispose(); } catch { }
        });

        // Som: se ainda tem chamada tocando, mantém a campainha; senão para.
        var restantes = Chamadas;
        if (!manterToque)
        {
            if (restantes.Any(x => x.Estado == EstadoChamada.Recebendo))
                Toques.Tocar(restantes.Any(x => x.Conectada) ? TipoToque.ChamadaEmEspera : TipoToque.Campainha);
            else if (restantes.Any(x => x.Estado == EstadoChamada.Chamando)) { /* mantém retorno */ }
            else Toques.Parar();
        }

        // A consulta caiu: a original continua em espera (o usuário retoma). Se a original caiu, desfaz o vínculo.
        foreach (var r in restantes.Where(x => x.ConsultaDe == c)) r.ConsultaDe = null;

        Registrar($"Chamada {c.Direcao} {c.Numero} encerrada: {c.Resultado} ({motivo})");
        ChamadaEncerrada?.Invoke(c);
    }

    private static (ResultadoChamada, string) TraduzirFalha(SIPResponse? resp, string? erro)
    {
        if (resp == null) return (ResultadoChamada.Falhou, string.IsNullOrWhiteSpace(erro) ? "Sem resposta do PABX" : erro!);
        return resp.StatusCode switch
        {
            486 or 600 => (ResultadoChamada.Ocupado, "Ocupado"),
            603 => (ResultadoChamada.Recusada, "Recusada"),
            480 => (ResultadoChamada.NaoAtendida, "Indisponível"),
            408 => (ResultadoChamada.NaoAtendida, "Não atendeu"),
            404 or 484 => (ResultadoChamada.Falhou, "Número inexistente"),
            487 => (ResultadoChamada.Cancelada, "Cancelada"),
            401 or 403 or 407 => (ResultadoChamada.Falhou, "Não autorizado pelo PABX"),
            488 or 415 => (ResultadoChamada.Falhou, "Codec não aceito pelo PABX"),
            503 => (ResultadoChamada.Falhou, "PABX/rota indisponível"),
            _ => (ResultadoChamada.Falhou, $"Falhou ({resp.StatusCode} {resp.ReasonPhrase})"),
        };
    }

    /// <summary>Tira espaços, traços e parênteses; mantém dígitos, *, # e +.</summary>
    public static string LimparDestino(string destino)
    {
        destino = destino.Trim();
        if (destino.StartsWith("sip:", StringComparison.OrdinalIgnoreCase)) destino = destino[4..];
        if (destino.StartsWith("tel:", StringComparison.OrdinalIgnoreCase)) destino = destino[4..];
        var at = destino.IndexOf('@');
        if (at >= 0) destino = destino[..at];
        if (destino.Any(char.IsLetter)) return destino; // ex.: ramal alfanumérico
        return new string(destino.Where(ch => char.IsDigit(ch) || ch is '*' or '#' or '+').ToArray());
    }

    private void Registrar(string msg) => Log?.Invoke(msg);

    public async ValueTask DisposeAsync()
    {
        await _relogioQualidade.DisposeAsync().ConfigureAwait(false);
        await PararAsync().ConfigureAwait(false);
        Toques.Dispose();
    }
}
