using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Softphone.Core;
using Forms = System.Windows.Forms;

namespace Softphone.App;

public partial class App : Application
{
    // Uma instância por pasta de dados (normalmente %APPDATA%\Softphone). Com SOFTPHONE_DADOS os testes rodam ao lado da instância real.
    private static readonly string Sufixo = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(
        System.Text.Encoding.UTF8.GetBytes(Armazenamento.Pasta.ToLowerInvariant())))[..12];
    private static readonly string NomeMutex = @"Local\Softphone.Instancia." + Sufixo;
    private static readonly string NomeEventoMostrar = @"Local\Softphone.Mostrar." + Sufixo;
    private Mutex? _mutex;
    private EventWaitHandle? _eventoMostrar;
    private Forms.NotifyIcon? _bandeja;
    private Forms.ToolStripMenuItem? _menuNaoPerturbe;

    public static App Atual => (App)Current;
    public ConfigApp Config { get; private set; } = new();
    public List<RegistroHistorico> Historico { get; private set; } = new();
    public List<Contato> Contatos { get; private set; } = new();
    public Telefone Telefone { get; private set; } = null!;
    public FabricaAudioNAudio FabricaAudio { get; } = new();
    /// <summary>Chat, presença e diretório (Hub). Sem endereço do Hub configurado, fica inativo.</summary>
    public ChatServico Chat { get; private set; } = null!;
    public GravacoesServico Gravacoes { get; private set; } = null!;
    private long? _conversaNotificada;
    private long? _gravacaoNotificada;
    public bool Saindo { get; private set; }
    public new MainWindow MainWindow => (MainWindow)base.MainWindow;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Reinício depois de atualização: espera a versão antiga terminar de fechar antes de pegar o "uma instância só".
        var iDepois = Array.IndexOf(e.Args, "--depois-de");
        bool atualizado = iDepois >= 0 && iDepois + 1 < e.Args.Length && int.TryParse(e.Args[iDepois + 1], out var pidAntigo) && EsperarFechar(pidAntigo);

        // Clicar para ligar: o Windows abre "Softphone.exe --link tel:..."
        var link = LinksTelefone.LinkDosArgumentos(e.Args);

        // Uma instância por usuário: a segunda só pede para a primeira aparecer (dois registros no mesmo ramal dariam problema).
        _mutex = new Mutex(true, NomeMutex, out bool primeira);
        if (!primeira)
        {
            // --manter: relancada periódica da tarefa agendada; já está aberto, então não faz a janela aparecer.
            if (link == null && e.Args.Any(a => a.Equals("--manter", StringComparison.OrdinalIgnoreCase))) { Shutdown(); return; }
            if (link != null) LinksTelefone.DeixarPedido(link);
            try { EventWaitHandle.OpenExisting(NomeEventoMostrar).Set(); } catch { }
            Shutdown();
            return;
        }
        _linkPendente = link;
        _eventoMostrar = new EventWaitHandle(false, EventResetMode.AutoReset, NomeEventoMostrar);
        new Thread(() =>
        {
            while (_eventoMostrar.WaitOne())
            {
                if (Saindo) return;
                Dispatcher.BeginInvoke(() =>
                {
                    MostrarJanela();
                    if (LinksTelefone.PegarPedido() is { } l) AbrirLink(l);
                });
            }
        }) { IsBackground = true, Name = "Softphone.Mostrar" }.Start();

        DispatcherUnhandledException += (_, ex) =>
        {
            Log.Escrever("ERRO NÃO TRATADO: " + ex.Exception);
            MessageBox.Show("Ocorreu um erro inesperado no Softphone:\n\n" + ex.Exception.Message + "\n\nO detalhe foi gravado no log.", "Softphone", MessageBoxButton.OK, MessageBoxImage.Warning);
            ex.Handled = true;
        };
        TaskScheduler.UnobservedTaskException += (_, ex) => { Log.Escrever("Tarefa com erro: " + ex.Exception); ex.SetObserved(); };

        base.OnStartup(e);
        Theme.Initialize();
        Log.Limpar();
        Log.Escrever($"Softphone {typeof(App).Assembly.GetName().Version} iniciando");

        Config = Armazenamento.CarregarConfig();
        SalvarConfig(); // regrava já no formato atual: remove a senha em texto que a 0.1.0 gravava por engano
        // Mantém o "iniciar com o Windows" apontando para o .exe atual (ex.: saiu de um zip e foi instalado em Program Files).
        if (Config.IniciarComWindows) Armazenamento.AplicarInicioComWindows(true);
        Historico = Armazenamento.Carregar("historico.json", () => new List<RegistroHistorico>());
        Contatos = Armazenamento.Carregar("contatos.json", () => new List<Contato>());

        var toques = new Toques();
        Telefone = new Telefone(FabricaAudio, toques)
        {
            NaoPerturbe = Config.NaoPerturbe,
            LogSip = Config.LogSip,
            Bloqueado = numero => Config.Bloqueados.Any(b => Armazenamento.MesmoNumero(b, numero)),
        };
        Telefone.Log += Log.Escrever;
        AplicarAudio();

        Chat = new ChatServico(this);
        Gravacoes = new GravacoesServico(this);
        // presença automática: em ligação durante chamadas; ramal informado ao registrar no PABX
        Telefone.RegistroMudou += (estado, motivo) => Dispatcher.BeginInvoke(() => { if (estado == EstadoRegistro.Registrado) _ = Chat.InformarRamalAsync(); });
        Telefone.ChamadaNova += ch => Dispatcher.BeginInvoke(() => _ = Chat.AtualizarMinhaPresencaAsync());
        Telefone.ChamadaMudou += ch => Dispatcher.BeginInvoke(() => _ = Chat.AtualizarMinhaPresencaAsync());
        Telefone.ChamadaEncerrada += ch => Dispatcher.BeginInvoke(() => _ = Chat.AtualizarMinhaPresencaAsync());

        var janela = new MainWindow();
        base.MainWindow = janela;
        CriarBandeja();
        ModoTeste.Iniciar(janela);

        bool minimizado = e.Args.Any(a => a.Equals("--minimizado", StringComparison.OrdinalIgnoreCase)) && _linkPendente == null;
        if (!minimizado || !Config.Conta.Valida || string.IsNullOrEmpty(Config.SenhaProtegida)) janela.Show();
        // aberto por um link com o app fechado: liga assim que registrar no PABX (ou só preenche, conforme a opção)
        Telefone.RegistroMudou += (estado, _) =>
        {
            if (estado == EstadoRegistro.Registrado && _linkPendente is { } l) Dispatcher.BeginInvoke(() => { _linkPendente = null; AbrirLink(l); });
        };
        if (_linkPendente != null && !Config.LinkDiscaDireto) { var l = _linkPendente; _linkPendente = null; janela.Dispatcher.BeginInvoke(() => AbrirLink(l)); }

        if (!Config.Conta.Valida || string.IsNullOrEmpty(Config.SenhaProtegida))
        {
            _ = PrimeiraContaAsync(janela);
        }
        else
        {
            _ = ConectarAsync();
        }
        _ = Chat.IniciarAsync();
        IniciarVerificacaoDeAtualizacao();
        if (atualizado) Notificar("Softphone atualizado", $"Agora na versão {typeof(App).Assembly.GetName().Version?.ToString(3)}.");
        LimparVersoesAntigas();
    }

    private string? _linkPendente;

    /// <summary>Link de telefone clicado (tel:/sip:/callto:): liga na hora ou põe o número no discador.</summary>
    public void AbrirLink(string link)
    {
        var numero = Discagem.NumeroDoLink(link, Config.PrefixoExterno);
        Log.Escrever($"Link de telefone: {link} -> {(numero.Length == 0 ? "(sem número)" : numero)}");
        MostrarJanela();
        if (numero.Length == 0) { MainWindow.MostrarAviso("Esse link não tem um número para ligar.", false); return; }
        bool podeLigar = Config.LinkDiscaDireto && Telefone.EstadoRegistro == EstadoRegistro.Registrado && !Telefone.Chamadas.Any(c => c.Ativa);
        if (podeLigar) _ = MainWindow.LigarAsync(numero);
        else MainWindow.PreencherNumero(numero);
    }

    private static bool EsperarFechar(int pid)
    {
        try { using var p = System.Diagnostics.Process.GetProcessById(pid); p.WaitForExit(15000); } catch { }
        return true;
    }

    /// <summary>Apaga os .exe antigos que a Loja renomeou ao atualizar (se já não estiverem em uso).</summary>
    private static void LimparVersoesAntigas()
    {
        try
        {
            var pasta = System.IO.Path.GetDirectoryName(Environment.ProcessPath);
            if (pasta is null) return;
            foreach (var f in System.IO.Directory.EnumerateFiles(pasta, "Softphone.antigo-*.exe"))
                try { System.IO.File.Delete(f); } catch { } // em uso por outro usuário do PC, ou sem permissão: fica para a próxima
        }
        catch { }
    }

    /// <summary>
    /// 1.1.8: conta ainda sem ramal/senha. Com o servidor do chat configurado, espera até 20 s o Hub entregar o ramal e a senha que a
    /// TI cadastrou no AD (o login no Hub é automático, pelo Windows); só abre a tela de Configurações se isso não chegar.
    /// </summary>
    private async Task PrimeiraContaAsync(MainWindow janela)
    {
        if (!string.IsNullOrWhiteSpace(Config.HubUrl))
            for (int i = 0; i < 40; i++)
            {
                await Task.Delay(500);
                if (Config.Conta.Valida && !string.IsNullOrEmpty(Config.SenhaProtegida)) return; // o AD já configurou (AplicarContaSipDoAdAsync registrou no PABX)
            }
        _ = janela.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => janela.AbrirConfiguracoes(primeiraVez: true));
    }

    /// <summary>
    /// 1.1.8 (Hub 1.2.8): pede ao Hub o ramal e a senha do SIP que a TI cadastrou no AD para esta pessoa e, se vierem, passam a valer
    /// (o AD substitui o que estava configurado). Chamado a cada conexão com o Hub, então uma troca no AD chega no próximo login.
    /// AD ou servidor fora do ar = mantém a conta atual; AD sem dados = destrava a conta para configuração manual.
    /// Nunca escreve a senha no log.
    /// </summary>
    public async Task AplicarContaSipDoAdAsync()
    {
        var cli = Chat.Cliente;
        if (cli is null || cli.Estado != EstadoHub.Conectado) return;
        Softphone.Contratos.ContaSipAdDto d;
        try { d = await cli.ContaSipAdAsync(); }
        catch (Exception ex) { Log.Escrever("Conta SIP pelo AD indisponível agora: " + ex.Message); return; }

        if ((string.IsNullOrEmpty(d.Login) && string.IsNullOrEmpty(d.Ramal)) || string.IsNullOrEmpty(d.Senha))
        {
            if (Config.ContaGerenciadaAd) { Config.ContaGerenciadaAd = false; Config.RamalDeUso = null; SalvarConfig(); Log.Escrever("O AD não tem mais ramal e senha para você: configuração manual liberada."); }
            return;
        }
        var conta = Config.Conta;
        // 1.1.9: o AD manda o ramal de USO (ipPhone) e o de LOGIN (o que registra no PABX); sem login, vale o ramal
        string loginSip = (string.IsNullOrEmpty(d.Login) ? d.Ramal : d.Login)!;
        string? uso = string.IsNullOrEmpty(d.Ramal) ? null : d.Ramal;
        bool mudou = conta.Usuario != loginSip || !string.IsNullOrEmpty(conta.Login) || Config.Senha != d.Senha;
        bool usoMudou = Config.RamalDeUso != uso;
        if (!mudou && !usoMudou && Config.ContaGerenciadaAd) return;
        if (mudou && Telefone.Chamadas.Any(c => c.Conectada)) { Log.Escrever("Conta SIP do AD mudou; aplico quando a chamada terminar (próxima conexão com o servidor)."); return; }
        if (string.IsNullOrWhiteSpace(conta.Servidor)) { Log.Escrever("Conta SIP do AD recebida, mas este PC não tem o servidor do PABX (softphone.padrao.json)."); return; }

        conta.Usuario = loginSip;
        conta.Login = "";
        Config.RamalDeUso = uso;
        Config.Senha = d.Senha;
        if (string.IsNullOrWhiteSpace(conta.NomeExibicao)) conta.NomeExibicao = cli.Eu?.Nome ?? "";
        Config.ContaGerenciadaAd = true;
        SalvarConfig();
        Log.Escrever($"Conta SIP aplicada pelo AD: login {loginSip}, ramal de uso {uso ?? "(igual ao login)"}" + (mudou ? " (conta alterada, registrando de novo)" : ""));
        if (mudou) await ConectarAsync();
        else await Chat.InformarRamalAsync();   // só o ramal de uso mudou: avisa o diretório sem registrar de novo
    }

    /// <summary>(Re)conecta com a conta salva.</summary>
    public async Task ConectarAsync()
    {
        var conta = Config.Conta;
        conta.Senha = Config.Senha;
        Telefone.LogSip = Config.LogSip;
        try { await Telefone.IniciarAsync(conta); }
        catch (Exception ex)
        {
            Log.Escrever("Falha ao iniciar a conta: " + ex);
            MainWindow.MostrarAviso("Não foi possível iniciar a conta: " + ex.Message, true);
        }
    }

    public void AplicarAudio()
    {
        var mics = DispositivosAudio.Microfones();
        var saidas = DispositivosAudio.AltoFalantes();
        FabricaAudio.Entrada = DispositivosAudio.Indice(mics, Config.Microfone);
        FabricaAudio.Saida = DispositivosAudio.Indice(saidas, Config.AltoFalante);
        FabricaAudio.Codecs = Config.Codecs ?? new List<string>();
        FabricaAudio.Ganho = Math.Clamp(Config.GanhoMicrofone, 25, 400) / 100f;
        FabricaAudio.Volume = Math.Clamp(Config.VolumeConversa, 25, 300) / 100f;
        FabricaAudio.Voz = new OpcoesVoz
        {
            CancelarEco = Config.CancelarEco,
            Ruido = Enum.TryParse<NivelRuido>(Config.ReducaoRuido, out var nr) ? nr : NivelRuido.Moderado,
            VolumeAutomatico = Config.VolumeAutomatico,
        };
        LinksTelefone.Aplicar(Config.AbrirLinksTelefone);
        Telefone.Toques.DispositivoConversa = FabricaAudio.Saida;
        Telefone.Toques.DispositivoCampainha = DispositivosAudio.Indice(saidas, Config.Campainha);
        Telefone.Toques.VolumeCampainha = Math.Clamp(Config.VolumeCampainha, 0, 100) / 100f;
        Telefone.Toques.ArquivoCampainha = Config.ToqueArquivo;
        Telefone.Toques.PresetTecla = Config.SomTecla;
        // 1.3: toque por tipo de chamada (interna = ramal de até 5 dígitos; fila ou setor = números listados; o resto é externa)
        Telefone.EscolherToque = numero =>
        {
            string dig = new string((numero ?? "").Where(char.IsDigit).ToArray());
            bool fila = dig.Length > 0 && (Config.NumerosFila ?? "").Split(new[] { ',', ';', ' ', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                .Any(n => new string(n.Where(char.IsDigit).ToArray()) == dig);
            string? escolha = fila ? Config.ToqueFila : dig.Length is > 0 and <= 5 ? Config.ToqueInterno : Config.ToqueExterno;
            if (string.IsNullOrWhiteSpace(escolha)) return (Config.ToquePadrao, Config.ToqueArquivo);
            return escolha.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) && System.IO.File.Exists(escolha) ? (null, escolha) : (escolha, null);
        };
        // chamadas e rede
        Telefone.EncaminharSempre = Vazio(Config.EncaminharSempre);
        Telefone.EncaminharSeOcupado = Vazio(Config.EncaminharSeOcupado);
        Telefone.EncaminharSeNaoAtender = Vazio(Config.EncaminharSeNaoAtender);
        Telefone.EncaminharSegundos = Config.EncaminharSegundos;
        Telefone.GravarAutomaticamente = Config.GravarAutomatico;
        Telefone.RtpPortaInicial = Config.RtpPortaInicial;
        Telefone.RtpPortaFinal = Config.RtpPortaFinal;
        Theme.Reaplicar();
    }

    private static string? Vazio(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    public void SalvarConfig() => Armazenamento.Salvar("config.json", Config);
    public void SalvarHistorico() => Armazenamento.Salvar("historico.json", Historico);
    public void SalvarContatos() => Armazenamento.Salvar("contatos.json", Contatos);

    /// <summary>Nome para um número: primeiro os contatos locais, depois o diretório da empresa (ramal aprendido pelo Hub).</summary>
    /// <summary>1.3: quem está ligando, pelo número normalizado (+55, 0, operadora, DDD): contatos primeiro, depois o diretório pelo ramal.</summary>
    public (string? Nome, string? Detalhe)? Identificar(string numero)
    {
        var achados = Contatos.Where(c => NumeroTelefone.Igual(c.Numero, numero)).ToList();
        if (achados.Select(a => a.Nome).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1) return (null, "Mais de um contato com este número (ambíguo)");
        if (achados.Count > 0)
        {
            var c = achados[0];
            var pessoa = Chat?.PessoaPorRamal(numero);
            return (c.Nome, string.Join(" · ", new[] { c.Empresa, c.Setor ?? pessoa?.Setor }.Where(x => !string.IsNullOrWhiteSpace(x))));
        }
        var p = Chat?.PessoaPorRamal(numero);
        if (p != null) return (p.Nome, string.Join(" · ", new[] { "Minha Empresa", p.Setor, p.Unidade }.Where(x => !string.IsNullOrWhiteSpace(x))));
        return null;
    }

    public string? NomeDoContato(string numero) =>
        Contatos.FirstOrDefault(c => Armazenamento.MesmoNumero(c.Numero, numero))?.Nome ?? Chat?.PessoaPorRamal(numero)?.Nome;

    // ---------------- bandeja ----------------

    private void CriarBandeja()
    {
        System.Drawing.Icon icone;
        try
        {
            using var s = GetResourceStream(new Uri("pack://application:,,,/Assets/softphone.ico"))!.Stream;
            icone = new System.Drawing.Icon(s, Forms.SystemInformation.SmallIconSize);
        }
        catch { icone = System.Drawing.SystemIcons.Application; }

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Abrir Softphone", null, (_, _) => MostrarJanela());
        _menuNaoPerturbe = new Forms.ToolStripMenuItem("Não perturbe") { CheckOnClick = true, Checked = Config.NaoPerturbe };
        _menuNaoPerturbe.Click += (_, _) => Dispatcher.Invoke(() => MainWindow.DefinirNaoPerturbe(_menuNaoPerturbe.Checked));
        menu.Items.Add(_menuNaoPerturbe);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Sair", null, (_, _) => Dispatcher.Invoke(() => _ = SairAsync()));

        _bandeja = new Forms.NotifyIcon { Icon = icone, Text = "Softphone", Visible = true, ContextMenuStrip = menu };
        _bandeja.MouseClick += (_, ev) => { if (ev.Button == Forms.MouseButtons.Left) Dispatcher.Invoke(MostrarJanela); };
        _bandeja.BalloonTipClicked += (_, _) => Dispatcher.Invoke(() =>
        {
            if (_conversaNotificada is long id) Chat.Abrir(id);
            else if (_gravacaoNotificada is long g) GravacoesWindow.Abrir(g);
            else MostrarJanela();
        });
        _bandeja.BalloonTipClosed += (_, _) => { _conversaNotificada = null; _gravacaoNotificada = null; };
    }

    public void AtualizarBandeja(string texto, bool naoPerturbe)
    {
        if (_bandeja == null) return;
        _bandeja.Text = texto.Length > 63 ? texto[..63] : texto;
        if (_menuNaoPerturbe != null) _menuNaoPerturbe.Checked = naoPerturbe;
    }

    public void Notificar(string titulo, string texto)
    {
        _conversaNotificada = null; _gravacaoNotificada = null;
        try { _bandeja?.ShowBalloonTip(6000, titulo, texto, Forms.ToolTipIcon.Info); } catch { }
    }

    /// <summary>Andamento de uma gravação: clicar no balão abre a tela de gravações nela.</summary>
    public void NotificarGravacao(string titulo, string texto, long gravacao)
    {
        _conversaNotificada = null; _gravacaoNotificada = gravacao;
        try { _bandeja?.ShowBalloonTip(8000, titulo, texto.Length > 200 ? texto[..200] : texto, Forms.ToolTipIcon.Info); } catch { }
    }

    /// <summary>Aviso de mensagem nova: clicar no balão abre a conversa.</summary>
    public void NotificarMensagem(string titulo, string texto, long conversa)
    {
        _conversaNotificada = conversa; _gravacaoNotificada = null;
        try { _bandeja?.ShowBalloonTip(6000, titulo, texto, Forms.ToolTipIcon.None); } catch { }
        MainWindow?.PiscarSePreciso();
    }

    public void MostrarJanela()
    {
        var j = MainWindow;
        if (j == null) return;
        if (!j.IsVisible) j.Show();
        if (j.WindowState == WindowState.Minimized) j.WindowState = WindowState.Normal;
        j.Activate();
        j.Topmost = true; j.Topmost = false; // traz para a frente sem ficar sempre por cima
        j.Focus();
    }

    // ===================== atualização pela Loja =====================
    // A Loja não fecha o Softphone aberto (poderia derrubar uma ligação): ela renomeia o .exe em uso e põe o novo no lugar.
    // Aqui o app percebe a versão nova na pasta e reinicia sozinho quando não houver ligação nem reunião.

    private System.Windows.Threading.DispatcherTimer? _verificaAtualizacao;
    private bool _avisouAtualizacao;

    private void IniciarVerificacaoDeAtualizacao()
    {
        _verificaAtualizacao = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _verificaAtualizacao.Tick += (_, _) => VerificarAtualizacao();
        _verificaAtualizacao.Start();
        Telefone.ChamadaEncerrada += _ => Dispatcher.BeginInvoke(VerificarAtualizacao);
    }

    private void VerificarAtualizacao()
    {
        if (Saindo) return;
        try
        {
            var meu = Environment.ProcessPath;
            if (meu is null || !meu.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return; // rodando pelo dotnet (testes)
            var instalado = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(meu)!, "Softphone.exe");
            if (!System.IO.File.Exists(instalado)) return;
            if (!Version.TryParse(System.Diagnostics.FileVersionInfo.GetVersionInfo(instalado).FileVersion, out var nova)) return;
            var atual = typeof(App).Assembly.GetName().Version;
            if (atual is null || nova <= atual) return;
            // R05: também espera uma gravação que ainda está sendo salva (WAV + dados) depois de desligar
            if (Telefone.Chamadas.Count > 0 || ReuniaoWindow.Abertas > 0 || Telefone.GravacoesSendoSalvas > 0)
            {
                if (!_avisouAtualizacao) Notificar("Softphone atualizado", $"A versão {nova.ToString(3)} foi instalada. O Softphone reinicia sozinho quando você não estiver em ligação.");
                _avisouAtualizacao = true;
                return;
            }
            Log.Escrever($"Versão {nova} instalada na pasta (rodando {atual}): reiniciando");
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(instalado, $"--minimizado --depois-de {Environment.ProcessId}") { UseShellExecute = false });
            _ = SairAsync(perguntar: false);
        }
        catch (Exception ex) { Log.Escrever("Verificação de atualização: " + ex.Message); }
    }

    public async Task SairAsync(bool perguntar = true)
    {
        if (Saindo) return;
        if (perguntar && Telefone.Chamadas.Any(c => c.Conectada) &&
            MessageBox.Show("Há uma chamada em andamento. Sair mesmo assim? A chamada será desligada.", "Softphone",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        Saindo = true;
        _eventoMostrar?.Set();
        MainWindow?.SalvarPosicao();
        SalvarConfig();
        try { await Task.WhenAny(Telefone.PararAsync(), Task.Delay(3000)); } catch { }
        // R05: desligar ao sair conclui as gravações em segundo plano; espera elas ficarem salvas antes de fechar
        try { await Telefone.AguardarGravacoesAsync(TimeSpan.FromMinutes(2)); } catch { }
        try { if (Chat.Cliente != null) await Task.WhenAny(Chat.Cliente.DisposeAsync().AsTask(), Task.Delay(2000)); } catch { }
        if (_bandeja != null) { _bandeja.Visible = false; _bandeja.Dispose(); }
        Log.Escrever("Softphone encerrado");
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _mutex?.ReleaseMutex(); } catch { }
        base.OnExit(e);
    }
}
