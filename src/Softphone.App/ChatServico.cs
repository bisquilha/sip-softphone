using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Softphone.Contratos;
using Softphone.Core;

namespace Softphone.App;

/// <summary>
/// Chat, presença e diretório no app: guarda as listas (na thread da tela), abre as janelas de conversa,
/// avisa mensagens novas e mantém a minha presença (escolhida + automática: em ligação / ausente por inatividade).
/// </summary>
public sealed class ChatServico
{
    private readonly App _app;
    private readonly Dispatcher _ui;
    private readonly Dictionary<string, PessoaVM> _porLogin = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<long, ChatWindow> _janelas = new();
    private readonly DispatcherTimer _relogio;
    private EstadoPresenca _enviada = EstadoPresenca.Offline;
    private string? _recadoEnviado;
    private bool _ausentePorInatividade;
    private readonly Dictionary<string, ConviteVideoWindow> _convites = new();
    private readonly Toques _toqueVideo = new();

    public ChatServico(App app)
    {
        _app = app;
        _ui = app.Dispatcher;
        _relogio = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _relogio.Tick += (_, _) => { VerificarInatividade(); foreach (var p in Pessoas.Where(x => !x.Online)) p.Recalcular(); };
        // em reunião = "Em ligação" para os colegas
        ReuniaoWindow.AlgumaMudou += () => _ui.BeginInvoke(() => _ = AtualizarMinhaPresencaAsync());
    }

    public HubCliente? Cliente { get; private set; }
    public ObservableCollection<PessoaVM> Pessoas { get; } = new();
    public ObservableCollection<ConversaVM> Conversas { get; } = new();
    public EstadoHub Estado => Cliente?.Estado ?? EstadoHub.Desligado;
    public string? Motivo => Cliente?.Motivo;
    public string? EuLogin => Cliente?.Eu?.Login;
    public int NaoLidasTotal => Conversas.Sum(c => c.NaoLidas);
    public bool Ativo => Cliente != null;

    /// <summary>Estado, lista ou não lidas mudaram (a janela principal atualiza abas, cabeçalho e bandeja).</summary>
    public event Action? Mudou;

    public PessoaVM? Pessoa(string login) => _porLogin.TryGetValue(login, out var p) ? p : null;

    public PessoaVM? PessoaPorRamal(string numero)
    {
        var n = Telefone.LimparDestino(numero);
        return n.Length == 0 ? null : Pessoas.FirstOrDefault(p => p.TemRamal && p.Ramal == n);
    }

    // ===================== conexão =====================

    public async Task IniciarAsync()
    {
        var url = _app.Config.HubUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            // R14: chat desligado nas Configurações: encerra a conexão que estava aberta e limpa a tela
            if (Cliente is { } velho)
            {
                Cliente = null;
                _relogio.Stop();
                foreach (var j in _janelas.Values.ToList()) j.Close();
                try { await velho.DisposeAsync(); } catch { }
                Pessoas.Clear(); _porLogin.Clear(); Conversas.Clear();
                MinhaPresenca = EstadoPresenca.Offline;
                Log.Escrever("Chat desligado nas Configurações.");
                Mudou?.Invoke();
            }
            return;
        }
        if (Cliente != null) await Cliente.DisposeAsync();

        var c = new HubCliente(url) { LoginTeste = Environment.GetEnvironmentVariable("SOFTPHONE_HUB_LOGIN_TESTE") };
        c.UsarTokenSalvo(Armazenamento.Abrir(_app.Config.HubTokenProtegido), _app.Config.HubTokenExpira);
        c.Log += Log.Escrever;
        c.TokenRenovado += (t, exp) => _ui.BeginInvoke(() =>
        {
            _app.Config.HubTokenProtegido = Armazenamento.Proteger(t);
            _app.Config.HubTokenExpira = exp;
            _app.SalvarConfig();
        });
        c.EstadoMudou += (e, m) => _ui.BeginInvoke(() => AoMudarEstado(e));
        c.Presenca += (l, e, r) => _ui.BeginInvoke(() => { Pessoa(l)?.MudarPresenca(e, r); foreach (var cv in Conversas.Where(x => !x.Grupo)) cv.Recalcular(); });
        c.Pessoa += p => _ui.BeginInvoke(() => AtualizarPessoa(p));
        c.Conversa += cv => _ui.BeginInvoke(() => AtualizarConversa(cv));
        c.Mensagem += m => _ui.BeginInvoke(() => AoReceberMensagem(m));
        c.Lida += (cv, l, u) => _ui.BeginInvoke(() => { if (_janelas.TryGetValue(cv, out var j)) j.AoLer(l, u); });
        c.Digitando += (cv, l) => _ui.BeginInvoke(() => { if (_janelas.TryGetValue(cv, out var j)) j.AoDigitar(l); });
        c.Convite += cv => _ui.BeginInvoke(() => AoReceberConvite(cv));
        c.ConversaRemovida += id => _ui.BeginInvoke(() => Remover(id));
        c.ConviteCancelado += sala => _ui.BeginInvoke(() => { if (_convites.TryGetValue(sala, out var w)) w.Cancelar(); });
        c.Gravacao += g => _ui.BeginInvoke(() => _app.Gravacoes.AoMudar(g));
        // 1.1
        c.MensagemAlterada += m => _ui.BeginInvoke(() => AoAlterarMensagem(m));
        c.Comunicado += x => _ui.BeginInvoke(() => ComunicadoWindow.Mostrar(x, ConfirmarComunicado));
        c.ComunicadoCancelado += id => _ui.BeginInvoke(() => ComunicadoWindow.Cancelar(id));
        c.EstadoIa += e => _ui.BeginInvoke(() => _app.Gravacoes.AoEstadoIa(e));
        c.ConviteResposta += (sala, login, aceitou) => _ui.BeginInvoke(() =>
        {
            if (!aceitou) _app.Notificar("Chamada de vídeo", $"{Pessoa(login)?.Nome ?? login} recusou a chamada.");
        });
        Cliente = c;
        _relogio.Start();
        Mudou?.Invoke();
        await c.IniciarAsync();
    }

    public async Task<string?> EntrarComSenhaAsync(string login, string senha) =>
        Cliente is null ? "Chat não configurado." : await Cliente.EntrarComSenhaAsync(login, senha);

    private async void AoMudarEstado(EstadoHub e)
    {
        Mudou?.Invoke();
        if (e != EstadoHub.Conectado || Cliente is null) return;
        // 1.1.8: antes de tudo, a conta SIP que a TI cadastrou no AD (se houver) configura o ramal e registra no PABX
        try { await _app.AplicarContaSipDoAdAsync(); } catch (Exception ex) { Log.Escrever("Conta SIP pelo AD: " + ex.Message); }
        try
        {
            // ao (re)conectar: recarrega listas e reenvia presença e ramal
            var pessoas = await Cliente.ListarPessoasAsync();
            Pessoas.Clear(); _porLogin.Clear();
            foreach (var p in pessoas.OrderBy(x => x.Nome, StringComparer.CurrentCultureIgnoreCase))
            {
                var vm = new PessoaVM(p);
                _porLogin[p.Login] = vm;
                Pessoas.Add(vm);
            }
            var convs = await Cliente.ListarConversasAsync();
            // R09: a conversa que está com janela aberta mantém o mesmo objeto (a janela continua recebendo as mudanças)
            var antigas = Conversas.Where(x => _janelas.ContainsKey(x.Id)).ToDictionary(x => x.Id);
            Conversas.Clear();
            foreach (var cv in convs.OrderByDescending(x => x.Ultima?.EnviadaEm ?? DateTime.MinValue))
            {
                if (antigas.TryGetValue(cv.Id, out var vm)) { vm.Atualizar(cv); Conversas.Add(vm); }
                else Conversas.Add(new ConversaVM(cv, EuLogin!, Pessoa));
            }
            // e busca o que chegou, foi editado ou apagado enquanto o chat estava fora
            foreach (var j in _janelas.Values.ToList()) _ = j.RessincronizarAsync();
            _enviada = EstadoPresenca.Offline; _recadoEnviado = null;
            await AtualizarMinhaPresencaAsync();
            await InformarRamalAsync();
            Mudou?.Invoke();
            await _app.Gravacoes.AoConectarAsync();   // recursos de IA do servidor + gravações que ficaram pendentes
            if (Cliente.ServidorV11)
            {
                try { await Cliente.InformarClienteAsync(typeof(App).Assembly.GetName().Version?.ToString(3), Environment.MachineName); } catch { }
                foreach (var x in await Cliente.ComunicadosPendentesAsync()) ComunicadoWindow.Mostrar(x, ConfirmarComunicado);
            }
        }
        catch (Exception ex) { Log.Escrever("Hub: falha ao carregar listas: " + ex.Message); }
    }

    // ===================== presença =====================

    public EstadoPresenca MinhaPresenca { get; private set; } = EstadoPresenca.Offline;

    /// <summary>Recalcula e envia: escolhida pela pessoa, mas "Em ligação" durante chamada e "Ausente" se parado há N minutos.</summary>
    public async Task AtualizarMinhaPresencaAsync()
    {
        var cfg = _app.Config;
        var e = cfg.PresencaEscolhida;
        if (_app.Telefone.NaoPerturbe) e = EstadoPresenca.NaoPerturbe;
        else if (ReuniaoWindow.Abertas > 0 || _app.Telefone.Chamadas.Any(c => c.Conectada || c.Estado is EstadoChamada.Discando or EstadoChamada.Chamando)) e = EstadoPresenca.EmLigacao;
        else if (e == EstadoPresenca.Disponivel && _ausentePorInatividade) e = EstadoPresenca.Ausente;
        MinhaPresenca = Estado == EstadoHub.Conectado ? e : EstadoPresenca.Offline;
        Mudou?.Invoke();
        if (Cliente is null || Estado != EstadoHub.Conectado || (e == _enviada && cfg.Recado == _recadoEnviado)) return;
        try
        {
            await Cliente.DefinirPresencaAsync(e, cfg.Recado ?? "");
            _enviada = e; _recadoEnviado = cfg.Recado;
            if (EuLogin != null) Pessoa(EuLogin)?.MudarPresenca(e, cfg.Recado);
        }
        catch (Exception ex) { Log.Escrever("Hub: falha ao enviar presença: " + ex.Message); }
    }

    public async Task EscolherPresencaAsync(EstadoPresenca e, string? recado)
    {
        _app.Config.PresencaEscolhida = e;
        if (recado != null) _app.Config.Recado = recado.Length == 0 ? null : recado;
        _app.SalvarConfig();
        await AtualizarMinhaPresencaAsync();
    }

    public async Task InformarRamalAsync()
    {
        if (Cliente is null || Estado != EstadoHub.Conectado) return;
        // 1.1.9: com a conta vinda do AD, o diretório recebe o ramal de USO (o que se disca), não o de login que registra no PABX
        var cfg = _app.Config;
        var ramal = _app.Telefone.EstadoRegistro == EstadoRegistro.Registrado
            ? (cfg.ContaGerenciadaAd && !string.IsNullOrWhiteSpace(cfg.RamalDeUso) ? cfg.RamalDeUso : cfg.Conta.Usuario)?.Trim()
            : null;
        if (string.IsNullOrEmpty(ramal)) return;
        try { await Cliente.InformarRamalAsync(ramal); } catch (Exception ex) { Log.Escrever("Hub: falha ao informar ramal: " + ex.Message); }
    }

    [StructLayout(LayoutKind.Sequential)] private struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }
    [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    private void VerificarInatividade()
    {
        int min = _app.Config.AusenteAposMinutos;
        bool parado = false;
        if (min > 0)
        {
            var li = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
            if (GetLastInputInfo(ref li)) parado = (uint)Environment.TickCount - li.dwTime > (uint)min * 60_000u;
        }
        if (parado != _ausentePorInatividade) { _ausentePorInatividade = parado; _ = AtualizarMinhaPresencaAsync(); }
    }

    // ===================== listas =====================

    private void AtualizarPessoa(PessoaDto p)
    {
        if (_porLogin.TryGetValue(p.Login, out var vm)) vm.Atualizar(p);
        else
        {
            vm = new PessoaVM(p);
            _porLogin[p.Login] = vm;
            int i = 0;
            while (i < Pessoas.Count && string.Compare(Pessoas[i].Nome, p.Nome, StringComparison.CurrentCultureIgnoreCase) < 0) i++;
            Pessoas.Insert(i, vm);
        }
        Mudou?.Invoke();
    }

    private ConversaVM AtualizarConversa(ConversaDto c)
    {
        var vm = Conversas.FirstOrDefault(x => x.Id == c.Id);
        if (vm is null) { vm = new ConversaVM(c, EuLogin!, Pessoa); Conversas.Insert(0, vm); }
        else vm.Atualizar(c);
        Reordenar(vm);
        Mudou?.Invoke();
        return vm;
    }

    private void Reordenar(ConversaVM vm)
    {
        int atual = Conversas.IndexOf(vm);
        int alvo = Conversas.Where(x => x != vm).Count(x => x.Quando > vm.Quando);
        if (atual >= 0 && atual != alvo) Conversas.Move(atual, alvo);
    }

    private async void AoReceberMensagem(MensagemDto m)
    {
        var vm = Conversas.FirstOrDefault(x => x.Id == m.Conversa);
        bool minha = string.Equals(m.Autor, EuLogin, StringComparison.OrdinalIgnoreCase);
        _janelas.TryGetValue(m.Conversa, out var janela);
        bool vendo = janela != null && janela.IsActive && janela.WindowState != WindowState.Minimized;

        if (vm is null)
        {
            try { vm = AtualizarConversa((await Cliente!.ListarConversasAsync()).First(x => x.Id == m.Conversa)); }
            catch { return; }
        }
        else
        {
            int naoLidas = minha || vendo ? 0 : vm.NaoLidas + 1;
            vm.Atualizar(vm.Dto with { Ultima = m, NaoLidas = minha || vendo ? 0 : naoLidas });
            Reordenar(vm);
        }
        janela?.AoReceber(m);
        if (vendo && !minha) _ = MarcarLidaAsync(vm, m.Id);
        Mudou?.Invoke();

        if (!minha && !vendo && _app.Config.NotificarMensagens && _app.Config.PresencaEscolhida != EstadoPresenca.NaoPerturbe && !_app.Telefone.NaoPerturbe)
        {
            var autor = Pessoa(m.Autor)?.Nome ?? m.Autor;
            bool mencao = m.Mencoes?.Any(x => string.Equals(x, EuLogin, StringComparison.OrdinalIgnoreCase)) == true;
            var titulo = mencao ? $"{autor} mencionou você" + (vm.Grupo ? $" em {vm.Titulo}" : "") : vm.Grupo ? $"{autor} em {vm.Titulo}" : autor;
            var texto = m.Arquivo != null && string.IsNullOrWhiteSpace(m.Texto) ? "📎 " + m.Arquivo.Nome : m.Texto;
            _app.NotificarMensagem(titulo, texto.Length > 180 ? texto[..180] + "…" : texto, m.Conversa);
            if (_app.Config.SomMensagem && _app.Telefone.Toques.Atual == TipoToque.Nenhum) System.Media.SystemSounds.Asterisk.Play();
        }
    }

    private void AoAlterarMensagem(MensagemDto m)
    {
        if (_janelas.TryGetValue(m.Conversa, out var j)) j.AoAlterar(m);
        var vm = Conversas.FirstOrDefault(x => x.Id == m.Conversa);
        if (vm?.Dto.Ultima?.Id == m.Id) vm.Atualizar(vm.Dto with { Ultima = m });
    }

    private void ConfirmarComunicado(long id)
    {
        if (Cliente is null || Estado != EstadoHub.Conectado) return;
        _ = Cliente.ConfirmarComunicadoAsync(id).ContinueWith(t => { if (t.Exception != null) Log.Escrever("Comunicado: " + t.Exception.GetBaseException().Message); }, TaskScheduler.Default);
    }

    /// <summary>Busca nas minhas conversas (Hub 1.1).</summary>
    public async Task<List<ResultadoBuscaDto>> BuscarAsync(string texto)
    {
        if (Cliente is null || Estado != EstadoHub.Conectado) throw new InvalidOperationException("O chat está sem conexão no momento.");
        if (!Cliente.ServidorV11) throw new InvalidOperationException("A busca precisa do servidor do chat atualizado (1.1).");
        return await Cliente.BuscarAsync(texto);
    }

    /// <summary>Abre a conversa e vai até a mensagem (resultado da busca).</summary>
    public async Task AbrirNaMensagemAsync(long conversa, long mensagem)
    {
        var vm = Conversas.FirstOrDefault(x => x.Id == conversa);
        if (vm is null && Cliente != null)
        {
            try { vm = AtualizarConversa((await Cliente.ListarConversasAsync()).First(x => x.Id == conversa)); } catch { return; }
        }
        if (vm is null) return;
        Abrir(vm);
        if (_janelas.TryGetValue(conversa, out var j)) await j.IrParaAsync(mensagem);
    }

    /// <summary>Qualidade da ligação para o painel da TI (Hub 1.1; sem Hub, nada).</summary>
    public async Task EnviarQualidadeAsync(Chamada c, QualidadeResumo q, bool conferencia)
    {
        if (Cliente is null || Estado != EstadoHub.Conectado || !Cliente.ServidorV11) return;
        try
        {
            await Cliente.EnviarQualidadeAsync(new QualidadeLigacaoDto(c.Numero, c.Direcao == DirecaoChamada.Entrada ? "entrada" : "saida",
                c.Atendida ?? c.Inicio, (int)c.Duracao.TotalSeconds, c.Codec, Math.Round(q.MosMedio, 2), Math.Round(q.MosMinimo, 2),
                Math.Round(q.PerdaPct, 2), Math.Round(q.JitterMedioMs, 1), q.RttMedioMs is double r ? Math.Round(r, 0) : null,
                conferencia, typeof(App).Assembly.GetName().Version?.ToString(3), q.Medido, "recebido", c.Id.ToString()));
        }
        catch (Exception ex) { Log.Escrever("Qualidade não enviada ao Hub: " + ex.Message); }
    }

    /// <summary>1.3: toda chamada encerrada (atendida, perdida, recusada, falha) vai ao painel da TI. Hub antigo ignora.</summary>
    public async Task EnviarChamadaAsync(ChamadaRegistroDto r)
    {
        if (Cliente is null || Estado != EstadoHub.Conectado || !Cliente.ServidorV13) return;
        try { await Cliente.EnviarChamadaAsync(r); }
        catch (Exception ex) { Log.Escrever("Chamada não enviada ao Hub: " + ex.Message); }
    }

    public async Task MarcarLidaAsync(ConversaVM vm, long ultimaId)
    {
        if (vm.NaoLidas != 0) { vm.Atualizar(vm.Dto with { NaoLidas = 0 }); Mudou?.Invoke(); }
        try { if (Cliente != null) await Cliente.MarcarLidaAsync(vm.Id, ultimaId); } catch { }
    }

    // ===================== janelas =====================

    public async Task AbrirComAsync(string login)
    {
        if (Cliente is null || Estado != EstadoHub.Conectado) { MessageBox.Show("O chat está sem conexão com o servidor no momento.", "Softphone"); return; }
        try { Abrir(AtualizarConversa(await Cliente.AbrirDiretaAsync(login))); }
        catch (Exception ex) { MessageBox.Show("Não foi possível abrir a conversa: " + ex.Message, "Softphone"); }
    }

    public async Task CriarGrupoAsync(string titulo, string[] logins)
    {
        if (Cliente is null) return;
        try { Abrir(AtualizarConversa(await Cliente.CriarGrupoAsync(titulo, logins))); }
        catch (Exception ex) { MessageBox.Show("Não foi possível criar o grupo: " + ex.Message, "Softphone"); }
    }

    public void Abrir(long conversaId)
    {
        var vm = Conversas.FirstOrDefault(x => x.Id == conversaId);
        if (vm != null) Abrir(vm);
    }

    public void Abrir(ConversaVM vm)
    {
        if (!_janelas.TryGetValue(vm.Id, out var j))
        {
            j = new ChatWindow(this, vm);
            j.Closed += (_, _) => _janelas.Remove(vm.Id);
            _janelas[vm.Id] = j;
            j.Show();
        }
        if (j.WindowState == WindowState.Minimized) j.WindowState = WindowState.Normal;
        j.Activate();
    }

    public IEnumerable<ChatWindow> JanelasAbertas => _janelas.Values;

    // ===================== ocultar / sair / excluir =====================

    public bool SouCriador(ConversaVM vm) => vm.Grupo && string.Equals(vm.Dto.Criador, EuLogin, StringComparison.OrdinalIgnoreCase);

    private void Remover(long id)
    {
        var vm = Conversas.FirstOrDefault(x => x.Id == id);
        if (vm != null) Conversas.Remove(vm);
        if (_janelas.TryGetValue(id, out var j)) j.Close();
        Mudou?.Invoke();
    }

    /// <summary>Executa a ação no servidor e tira a conversa da lista (o servidor também avisa os meus outros PCs).</summary>
    public async Task<bool> AcaoConversaAsync(ConversaVM vm, Func<HubCliente, Task> acao)
    {
        if (Cliente is null || Estado != EstadoHub.Conectado) { MessageBox.Show("O chat está sem conexão no momento.", "Softphone"); return false; }
        try { await acao(Cliente); Remover(vm.Id); return true; }
        catch (Exception ex)
        {
            var msg = ex.Message;
            var i = msg.IndexOf("HubException: ", StringComparison.Ordinal);
            MessageBox.Show(i >= 0 ? msg[(i + 14)..] : msg, "Softphone", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }
    }

    // ===================== reuniões / chamadas de vídeo =====================

    private bool PodeReunir()
    {
        if (Cliente is not null && Estado == EstadoHub.Conectado) return true;
        MessageBox.Show("As reuniões dependem do servidor do chat, que está sem conexão no momento.", "Softphone");
        return false;
    }

    private static void AbrirReuniao(ReuniaoDto r, IEnumerable<string>? chamados = null)
    {
        var j = new ReuniaoWindow(r, chamados);
        j.Show();
        j.Activate();
    }

    /// <summary>Botão de vídeo numa conversa: abre a sala, manda o convite no chat e toca para quem está online.</summary>
    public async Task VideoNaConversaAsync(ConversaVM conversa)
    {
        if (!PodeReunir()) return;
        try { AbrirReuniao(await Cliente!.IniciarReuniaoAsync(conversa.Id, null, tocar: true)); }
        catch (Exception ex) { MessageBox.Show("Não foi possível iniciar a chamada de vídeo: " + ex.Message, "Softphone"); }
    }

    /// <summary>Botão de vídeo numa pessoa (aba Pessoas): conversa direta + chamada de vídeo tocando para ela.</summary>
    public async Task VideoComAsync(string login)
    {
        if (!PodeReunir()) return;
        try
        {
            var conv = AtualizarConversa(await Cliente!.AbrirDiretaAsync(login));
            await VideoNaConversaAsync(conv);
        }
        catch (Exception ex) { MessageBox.Show("Não foi possível chamar por vídeo: " + ex.Message, "Softphone"); }
    }

    /// <summary>
    /// "Nova reunião" (1.1.3, como no Teams): escolhe as pessoas numa lista com busca e o Softphone de quem está online
    /// toca na hora; ou agenda no Outlook com o link. Sem ninguém escolhido, abre a sala sozinho (como o antigo
    /// "Reunir agora") e dá para chamar depois com "Adicionar pessoas".
    /// </summary>
    public async Task NovaReuniaoAsync(Window? dono)
    {
        if (!PodeReunir()) return;
        var j = new EscolherPessoasWindow(Pessoas, EuLogin, adicionar: false) { Owner = dono };
        if (j.ShowDialog() != true) return;
        var escolhidas = j.Escolhidas;
        var logins = escolhidas.Select(p => p.Login).ToArray();
        try
        {
            if (j.Resultado == EscolherPessoasWindow.Escolha.Outlook) { await AgendarNoOutlookAsync(escolhidas); return; }
            var r = await Cliente!.IniciarReuniaoAsync(null, logins.Length > 0 ? logins : null, tocar: logins.Length > 0);
            try { Clipboard.SetText(Cliente.LinkReuniao(r.Sala)); } catch { }
            AbrirReuniao(r, logins);
            var offline = escolhidas.Where(p => !p.Online).Select(p => p.Nome).ToList();
            if (logins.Length == 0)
                _app.Notificar("Reunião criada", "Use \"Adicionar pessoas\" na reunião para chamar alguém. O link também foi copiado.");
            else if (offline.Count > 0)
                _app.Notificar("Reunião criada", $"Chamando quem está online. Offline (não tocou): {string.Join(", ", offline)}. O link foi copiado.");
        }
        catch (Exception ex) { MessageBox.Show("Não foi possível criar a reunião: " + ex.Message, "Softphone"); }
    }

    /// <summary>Cria a sala (sem tocar) e abre o compromisso no Outlook com os convidados e o link do Hub, que funciona
    /// no navegador de qualquer PC da empresa (login do Windows). Sem Outlook clássico: copia o link.</summary>
    private async Task AgendarNoOutlookAsync(IReadOnlyList<PessoaVM> pessoas)
    {
        var r = await Cliente!.IniciarReuniaoAsync(null, pessoas.Select(p => p.Login).ToArray(), tocar: false);
        var link = Cliente.LinkReuniao(r.Sala);
        var emails = pessoas.Select(p => p.Email).Where(e => !string.IsNullOrWhiteSpace(e)).Select(e => e!).ToList();
        var semEmail = pessoas.Where(p => string.IsNullOrWhiteSpace(p.Email)).Select(p => p.Nome).ToList();
        var texto = "Reunião pelo Softphone.\r\n\r\n" +
                    $"Para entrar, na hora da reunião: {link}\r\n\r\n" +
                    "Abra o link num computador da empresa (entra com o seu usuário do Windows). Funciona na rede da empresa e nas filiais.";
        if (OutlookReuniao.Abrir("Reunião", link, texto, emails, out var erro))
        {
            if (semEmail.Count > 0)
                _app.Notificar("Agendar no Outlook", $"Sem e-mail no AD (acrescente no Outlook, se precisar): {string.Join(", ", semEmail)}.");
        }
        else
        {
            try { Clipboard.SetText(link); } catch { }
            MessageBox.Show($"Não deu para abrir o Outlook ({erro}).\n\nO link da reunião foi copiado. Cole no convite:\n{link}",
                "Softphone", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    /// <summary>Entrar por um convite do chat (link) ou de uma chamada tocando.</summary>
    public async Task EntrarAsync(string sala, string? titulo)
    {
        if (!PodeReunir()) return;
        try { AbrirReuniao(await Cliente!.EntrarReuniaoAsync(sala, titulo)); }
        catch (Exception ex) { MessageBox.Show("Não foi possível entrar na reunião: " + ex.Message, "Softphone"); }
    }

    private void AoReceberConvite(ConviteDto c)
    {
        if (_convites.ContainsKey(c.Sala)) return;
        // não perturbe: não toca; fica só o convite no chat e o aviso de chamada perdida
        if (_app.Telefone.NaoPerturbe || _app.Config.PresencaEscolhida == EstadoPresenca.NaoPerturbe)
        {
            _app.Notificar("Chamada de vídeo perdida", $"{c.NomeDe} te chamou por vídeo (você está em não perturbe).");
            return;
        }
        var w = new ConviteVideoWindow(c);
        _convites[c.Sala] = w;
        _toqueVideo.DispositivoCampainha = _app.Telefone.Toques.DispositivoCampainha;
        _toqueVideo.VolumeCampainha = _app.Telefone.Toques.VolumeCampainha;
        if (_app.Telefone.Toques.Atual == TipoToque.Nenhum) _toqueVideo.Tocar(TipoToque.Campainha);
        w.Respondeu += async atender =>
        {
            try { await Cliente!.ResponderConviteAsync(c.Sala, c.De, atender); } catch { }
            if (atender) await EntrarAsync(c.Sala, c.Titulo);
        };
        w.Expirou += () => _app.Notificar("Chamada de vídeo perdida", $"{c.NomeDe} te chamou por vídeo.");
        w.Closed += (_, _) => { _convites.Remove(c.Sala); if (_convites.Count == 0) _toqueVideo.Parar(); };
        w.Show();
        _app.MainWindow?.PiscarSePreciso();
    }
}

