using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Softphone.Core;
using Microsoft.Win32;

namespace Softphone.App;

public partial class ConfigWindow : Window
{
    private const string Padrao = "(padrão do Windows)";
    private readonly ConfigApp _cfg;
    private readonly ObservableCollection<CodecItem> _codecs = new();
    private bool _carregando = true;

    public sealed class CodecItem { public string Nome { get; init; } = ""; public bool Ativo { get; set; } public string Descricao => Core.Codecs.Descricao(Nome); }

    /// <summary>Dados da conta mudaram: precisa registrar de novo.</summary>
    public bool ContaMudou { get; private set; }
    public bool HubMudou { get; private set; }

    public ConfigWindow(ConfigApp cfg, bool primeiraVez)
    {
        InitializeComponent();
        Theme.AplicarBarraTitulo(this);
        _cfg = cfg;
        BoasVindas.Visibility = primeiraVez ? Visibility.Visible : Visibility.Collapsed;
        Carregar(cfg);
        ListaCodecs.ItemsSource = _codecs;
        _carregando = false;
        AtualizarRotulos();
        Loaded += (_, _) => (string.IsNullOrWhiteSpace(CaixaUsuario.Text) ? (Control)CaixaUsuario : CaixaSenha).Focus();
    }

    private void Carregar(ConfigApp cfg)
    {
        var c = cfg.Conta;
        CaixaUsuario.Text = c.Usuario;
        CaixaSenha.Password = cfg.Senha;
        CaixaNomeExibicao.Text = c.NomeExibicao;
        CaixaServidor.Text = c.Servidor;
        CaixaDominio.Text = c.Dominio;
        CaixaLogin.Text = c.Login;
        CaixaProxy.Text = c.Proxy;
        CaixaTransporte.SelectedIndex = string.Equals(c.Transporte, "TCP", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        CaixaRegistro.Text = c.RegistroSegundos.ToString();
        CaixaManterAtivo.Text = c.ManterAtivoSegundos.ToString();
        CaixaHub.Text = cfg.HubUrl ?? "";
        // ramal sugerido pelo diretório (Hub/AD), quando a conta ainda está vazia
        var sugerido = App.Atual.Chat?.Cliente?.Eu?.Ramal;
        if (string.IsNullOrWhiteSpace(c.Usuario) && !string.IsNullOrWhiteSpace(sugerido))
        {
            // 1.1.9: o ramal do diretório é o de USO; o que registra no PABX é o de LOGIN, que pode ser outro número: só sugere, não preenche
            TxtRamalSugerido.Text = $"No diretório da empresa o seu ramal é {sugerido}. Informe o ramal de LOGIN e a senha (se a TI os cadastrou no AD, eles chegam sozinhos).";
            TxtRamalSugerido.Visibility = Visibility.Visible;
        }
        // 1.1.8: ramal e senha cadastrados pela TI no AD: aparecem travados e se atualizam sozinhos a cada login
        if (cfg.ContaGerenciadaAd)
        {
            CaixaUsuario.IsEnabled = false; CaixaSenha.IsEnabled = false; CaixaLogin.IsEnabled = false;
            var uso = string.IsNullOrWhiteSpace(cfg.RamalDeUso) || cfg.RamalDeUso == c.Usuario ? "" : $"Seu ramal é {cfg.RamalDeUso}; o campo acima é o login que registra no PABX. ";
            TxtRamalSugerido.Text = uso + "Ramal, login e senha fornecidos pela TI (cadastro do AD): atualizam sozinhos a cada vez que você entra. Para mudar, peça à TI.";
            TxtRamalSugerido.Visibility = Visibility.Visible;
        }

        Preencher(CaixaMicrofone, DispositivosAudio.Microfones(), cfg.Microfone);
        var saidas = DispositivosAudio.AltoFalantes();
        Preencher(CaixaAltoFalante, saidas, cfg.AltoFalante);
        Preencher(CaixaCampainha, saidas, cfg.Campainha);
        SliderVolume.Value = cfg.VolumeCampainha;
        SliderGanho.Value = cfg.GanhoMicrofone;
        SliderVolumeConversa.Value = cfg.VolumeConversa;
        CaixaToque.Text = cfg.ToqueArquivo ?? "";
        PreencherToques(CbToqueInterno, cfg.ToqueInterno); PreencherToques(CbToqueExterno, cfg.ToqueExterno); PreencherToques(CbToqueFila, cfg.ToqueFila);
        CaixaNumerosFila.Text = cfg.NumerosFila ?? "";
        MontarSons(cfg);
        ChkEco.IsChecked = cfg.CancelarEco;
        foreach (ComboBoxItem it in CaixaRuido.Items) if ((string)it.Tag == (cfg.ReducaoRuido ?? "Moderado")) CaixaRuido.SelectedItem = it;
        if (CaixaRuido.SelectedItem is null) CaixaRuido.SelectedIndex = 2;
        ChkVolumeAuto.IsChecked = cfg.VolumeAutomatico;
        if (!ProcessadorVoz.Disponivel)
            TxtVozInfo.Text = "O tratamento da voz não carregou neste PC (" + (ProcessadorVoz.ErroCarga ?? "?") + "). As ligações funcionam, só sem cancelar eco.";

        ChkGravarAuto.IsChecked = cfg.GravarAutomatico;
        ChkTranscreverAuto.IsChecked = cfg.TranscreverAutomatico;
        ChkAtenderAuto.IsChecked = cfg.AtenderAutomatico;
        CaixaAtenderSeg.Text = cfg.AtenderAutomaticoSegundos.ToString();
        ChkAtenderSoInternos.IsChecked = cfg.AtenderAutomaticoSoInternos;
        CaixaEncSempre.Text = cfg.EncaminharSempre ?? "";
        CaixaEncOcupado.Text = cfg.EncaminharSeOcupado ?? "";
        CaixaEncNaoAtende.Text = cfg.EncaminharSeNaoAtender ?? "";
        CaixaEncSeg.Text = cfg.EncaminharSegundos.ToString();
        _codecs.Clear();
        var ordem = (cfg.Codecs ?? new List<string>()).Where(x => Core.Codecs.Todos.Contains(x)).ToList();
        foreach (var n in ordem) _codecs.Add(new CodecItem { Nome = n, Ativo = true });
        foreach (var n in Core.Codecs.Todos.Where(x => !ordem.Contains(x))) _codecs.Add(new CodecItem { Nome = n, Ativo = false });
        CaixaBloqueados.Text = string.Join(Environment.NewLine, cfg.Bloqueados);

        ChkNotificar.IsChecked = cfg.NotificarMensagens;
        ChkSomMensagem.IsChecked = cfg.SomMensagem;
        ChkAvisoTopo.IsChecked = cfg.AvisoNoTopo;
        CaixaAusenteMin.Text = cfg.AusenteAposMinutos.ToString();

        CaixaPortaSip.Text = c.PortaLocal.ToString();
        CaixaRtpIni.Text = cfg.RtpPortaInicial.ToString();
        CaixaRtpFim.Text = cfg.RtpPortaFinal.ToString();
        ChkLogSip.IsChecked = cfg.LogSip;

        foreach (ComboBoxItem it in CaixaTema.Items) if ((string)it.Tag == (cfg.Tema ?? "Sistema")) CaixaTema.SelectedItem = it;
        if (CaixaTema.SelectedItem is null) CaixaTema.SelectedIndex = 0;
        ChkIniciar.IsChecked = cfg.IniciarComWindows;
        ChkAtalhos.IsChecked = cfg.AtalhosGlobais;
        MontarAtalhos(cfg);
        CaixaFav1.Text = cfg.Favoritos?.ElementAtOrDefault(0) ?? ""; CaixaFav2.Text = cfg.Favoritos?.ElementAtOrDefault(1) ?? ""; CaixaFav3.Text = cfg.Favoritos?.ElementAtOrDefault(2) ?? "";
        ChkLinksTel.IsChecked = cfg.AbrirLinksTelefone;
        ChkLinkDiscaDireto.IsChecked = cfg.LinkDiscaDireto;
        CaixaPrefixoExterno.Text = cfg.PrefixoExterno ?? "";
        TxtVersao.Text = $"Softphone {typeof(App).Assembly.GetName().Version?.ToString(3)} · dados em {Armazenamento.Pasta}";
    }

    private static void Preencher(ComboBox caixa, IReadOnlyList<string> nomes, string? atual)
    {
        var itens = new List<string> { Padrao };
        itens.AddRange(nomes);
        caixa.ItemsSource = itens;
        caixa.SelectedItem = !string.IsNullOrWhiteSpace(atual) && nomes.Contains(atual) ? atual : Padrao;
    }

    private static string? Escolhido(ComboBox caixa) => caixa.SelectedItem is string s && s != Padrao ? s : null;

    private void Slider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) { if (!_carregando) AtualizarRotulos(); }

    private void AtualizarRotulos()
    {
        if (TxtGanho == null) return;
        TxtGanho.Text = $"{SliderGanho.Value:0}%";
        TxtVolumeConversa.Text = $"{SliderVolumeConversa.Value:0}%";
        TxtVolume.Text = $"{SliderVolume.Value:0}%";
    }

    private bool Inteiro(TextBox caixa, int min, int max, string nome, TabItem aba, out int valor)
    {
        if (int.TryParse(caixa.Text.Trim(), out valor) && valor >= min && valor <= max) return true;
        Abas.SelectedItem = aba;
        Erro($"{nome}: de {min} a {max}.");
        caixa.Focus();
        return false;
    }

    private void Salvar_Click(object sender, RoutedEventArgs e)
    {
        var abaConta = (TabItem)Abas.Items[0];
        var abaChamadas = (TabItem)Abas.Items[2];
        var abaChat = (TabItem)Abas.Items[3];
        var abaRede = (TabItem)Abas.Items[4];
        string usuario = CaixaUsuario.Text.Trim(), servidor = CaixaServidor.Text.Trim();
        if (usuario.Length == 0) { Abas.SelectedItem = abaConta; Erro("Informe o ramal."); CaixaUsuario.Focus(); return; }
        if (CaixaSenha.Password.Length == 0) { Abas.SelectedItem = abaConta; Erro("Informe a senha do ramal."); CaixaSenha.Focus(); return; }
        if (servidor.Length == 0) { Abas.SelectedItem = abaConta; Erro("Informe o servidor SIP."); CaixaServidor.Focus(); return; }
        if (!Inteiro(CaixaRegistro, 60, 3600, "Registro", abaConta, out int registro)) return;
        if (!Inteiro(CaixaManterAtivo, 0, 300, "Manter ativo", abaConta, out int manter)) return;
        if (!Inteiro(CaixaAtenderSeg, 0, 60, "Atender depois de", abaChamadas, out int atenderSeg)) return;
        if (!Inteiro(CaixaEncSeg, 5, 120, "Encaminhar depois de", abaChamadas, out int encSeg)) return;
        if (!Inteiro(CaixaAusenteMin, 0, 240, "Ausente depois de", abaChat, out int ausente)) return;
        if (!Inteiro(CaixaPortaSip, 0, 65535, "Porta SIP", abaRede, out int portaSip)) return;
        if (!Inteiro(CaixaRtpIni, 0, 65535, "Porta RTP inicial", abaRede, out int rtpIni)) return;
        if (!Inteiro(CaixaRtpFim, 0, 65535, "Porta RTP final", abaRede, out int rtpFim)) return;
        if (rtpIni > 0 && rtpFim - rtpIni < 10) { Abas.SelectedItem = abaRede; Erro("Faixa RTP precisa de pelo menos 10 portas."); return; }
        var codecs = _codecs.Where(x => x.Ativo).Select(x => x.Nome).ToList();
        if (codecs.Count == 0) { Abas.SelectedItem = abaChamadas; Erro("Deixe pelo menos um codec ligado."); return; }
        var hub = CaixaHub.Text.Trim();
        if (hub.Length > 0 && !Uri.TryCreate(hub, UriKind.Absolute, out _)) { Abas.SelectedItem = abaConta; Erro("Endereço do servidor do chat inválido (ex.: https://softphone.corp.example.com)."); return; }

        var c = _cfg.Conta;
        string transporte = CaixaTransporte.SelectedIndex == 1 ? "TCP" : "UDP";
        ContaMudou = c.Usuario != usuario || _cfg.Senha != CaixaSenha.Password || c.Servidor != servidor ||
                     c.Dominio != CaixaDominio.Text.Trim() || c.Login != CaixaLogin.Text.Trim() || c.Proxy != CaixaProxy.Text.Trim() ||
                     c.Transporte != transporte || c.RegistroSegundos != registro || c.ManterAtivoSegundos != manter ||
                     c.NomeExibicao != CaixaNomeExibicao.Text.Trim() || _cfg.LogSip != (ChkLogSip.IsChecked == true) || c.PortaLocal != portaSip ||
                     App.Atual.Telefone.EstadoRegistro is EstadoRegistro.Falha or EstadoRegistro.Desligado;
        HubMudou = !string.Equals(_cfg.HubUrl ?? "", hub, StringComparison.OrdinalIgnoreCase);

        c.Usuario = usuario;
        c.NomeConta = CaixaNomeExibicao.Text.Trim();
        c.NomeExibicao = CaixaNomeExibicao.Text.Trim();
        c.Servidor = servidor;
        c.Dominio = CaixaDominio.Text.Trim();
        c.Login = CaixaLogin.Text.Trim();
        c.Proxy = CaixaProxy.Text.Trim();
        c.Transporte = transporte;
        c.RegistroSegundos = registro;
        c.ManterAtivoSegundos = manter;
        c.PortaLocal = portaSip;
        _cfg.Senha = CaixaSenha.Password;
        // R14: vazio = chat desligado de propósito (fica salvo como ""); null quer dizer "ainda não configurado" e
        // recebe o endereço padrão da TI ao abrir
        _cfg.HubUrl = hub;
        _cfg.LogSip = ChkLogSip.IsChecked == true;
        _cfg.Microfone = Escolhido(CaixaMicrofone);
        _cfg.AltoFalante = Escolhido(CaixaAltoFalante);
        _cfg.Campainha = Escolhido(CaixaCampainha);
        _cfg.VolumeCampainha = (int)SliderVolume.Value;
        _cfg.GanhoMicrofone = (int)SliderGanho.Value;
        _cfg.VolumeConversa = (int)SliderVolumeConversa.Value;
        _cfg.ToqueArquivo = string.IsNullOrWhiteSpace(CaixaToque.Text) ? null : CaixaToque.Text;
        _cfg.ToquePadrao = _toquePadrao == "classico" ? null : _toquePadrao;
        _cfg.SomTecla = _somTecla == "dtmf" ? null : _somTecla;
        _cfg.ToqueInterno = (CbToqueInterno.SelectedItem as ComboBoxItem)?.Tag as string;
        _cfg.ToqueExterno = (CbToqueExterno.SelectedItem as ComboBoxItem)?.Tag as string;
        _cfg.ToqueFila = (CbToqueFila.SelectedItem as ComboBoxItem)?.Tag as string;
        _cfg.NumerosFila = string.IsNullOrWhiteSpace(CaixaNumerosFila.Text) ? null : CaixaNumerosFila.Text.Trim();
        _cfg.Codecs = codecs;
        _cfg.GravarAutomatico = ChkGravarAuto.IsChecked == true;
        _cfg.TranscreverAutomatico = ChkTranscreverAuto.IsChecked == true;
        _cfg.AtenderAutomatico = ChkAtenderAuto.IsChecked == true;
        _cfg.AtenderAutomaticoSegundos = atenderSeg;
        _cfg.AtenderAutomaticoSoInternos = ChkAtenderSoInternos.IsChecked == true;
        _cfg.EncaminharSempre = Vazio(CaixaEncSempre.Text);
        _cfg.EncaminharSeOcupado = Vazio(CaixaEncOcupado.Text);
        _cfg.EncaminharSeNaoAtender = Vazio(CaixaEncNaoAtende.Text);
        _cfg.EncaminharSegundos = encSeg;
        _cfg.Bloqueados = CaixaBloqueados.Text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Distinct().ToList();
        _cfg.NotificarMensagens = ChkNotificar.IsChecked == true;
        _cfg.SomMensagem = ChkSomMensagem.IsChecked == true;
        _cfg.AvisoNoTopo = ChkAvisoTopo.IsChecked == true;
        _cfg.AusenteAposMinutos = ausente;
        _cfg.RtpPortaInicial = rtpIni;
        _cfg.RtpPortaFinal = rtpFim;
        _cfg.Tema = (CaixaTema.SelectedItem as ComboBoxItem)?.Tag as string ?? "Sistema";
        _cfg.IniciarComWindows = ChkIniciar.IsChecked == true;
        _cfg.AtalhosGlobais = ChkAtalhos.IsChecked == true;
        var atalhos = new Dictionary<string, string>();
        var lista = new List<(string, string)>();
        foreach (var caixa in PainelAtalhos.Children.OfType<Grid>().SelectMany(g => g.Children.OfType<TextBox>()))
        {
            var ac = (AtalhosApp.Acao)caixa.Tag;
            lista.Add((ac.Nome, caixa.Text));
            if (!string.Equals(caixa.Text, ac.Padrao, StringComparison.OrdinalIgnoreCase)) atalhos[ac.Chave] = caixa.Text;
        }
        if (AtalhosApp.Repetido(lista) is { } rep) { Erro("Atalhos repetidos: " + rep); return; }
        _cfg.Atalhos = atalhos.Count == 0 ? null : atalhos;
        _cfg.Favoritos = new[] { CaixaFav1.Text.Trim(), CaixaFav2.Text.Trim(), CaixaFav3.Text.Trim() };
        _cfg.CancelarEco = ChkEco.IsChecked == true;
        _cfg.ReducaoRuido = (CaixaRuido.SelectedItem as ComboBoxItem)?.Tag as string ?? "Moderado";
        _cfg.VolumeAutomatico = ChkVolumeAuto.IsChecked == true;
        _cfg.AbrirLinksTelefone = ChkLinksTel.IsChecked == true;
        _cfg.LinkDiscaDireto = ChkLinkDiscaDireto.IsChecked == true;
        var prefixo = new string(CaixaPrefixoExterno.Text.Where(ch => char.IsDigit(ch) || ch is '*' or '#').ToArray());
        _cfg.PrefixoExterno = prefixo.Length == 0 ? null : prefixo;
        DialogResult = true;
    }

    private static string? Vazio(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    private void Erro(string msg) => TxtErro.Text = msg;

    // ===================== codecs =====================

    // 1.1.6: o perfil fica no servidor (não é configuração deste PC), por isso abre numa janela à parte e salva na hora

    private void CodecSubir_Click(object sender, RoutedEventArgs e) => Mover(-1);
    private void CodecDescer_Click(object sender, RoutedEventArgs e) => Mover(+1);

    private void Mover(int passo)
    {
        if (ListaCodecs.SelectedItem is not CodecItem it) return;
        int i = _codecs.IndexOf(it), j = i + passo;
        if (j < 0 || j >= _codecs.Count) return;
        _codecs.Move(i, j);
        ListaCodecs.SelectedItem = it;
    }

    // ===================== toque =====================

    private void EscolherToque_Click(object sender, RoutedEventArgs e)
    {
        var d = new OpenFileDialog { Title = "Toque da campainha", Filter = "Áudio WAV (*.wav)|*.wav" };
        if (d.ShowDialog(this) != true) return;
        try { using var r = new NAudio.Wave.WaveFileReader(d.FileName); }
        catch { Erro("Esse arquivo .wav não pôde ser lido. Escolha outro."); return; }
        // copia para a pasta do usuário (o arquivo original pode sumir ou estar em rede)
        var destino = Path.Combine(Armazenamento.Pasta, "toque" + Path.GetExtension(d.FileName));
        Directory.CreateDirectory(Armazenamento.Pasta);
        File.Copy(d.FileName, destino, true);
        CaixaToque.Text = destino;
    }

    private void LimparToque_Click(object sender, RoutedEventArgs e) => CaixaToque.Text = "";

    // 1.3: atalhos configuráveis
    private void MontarAtalhos(ConfigApp cfg)
    {
        PainelAtalhos.Children.Clear();
        foreach (var a in AtalhosApp.Acoes)
        {
            var linha = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            linha.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            linha.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            linha.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            linha.Children.Add(new TextBlock { Text = a.Nome, VerticalAlignment = VerticalAlignment.Center });
            var caixa = new TextBox { Text = AtalhosApp.Efetivo(cfg, a), IsReadOnly = true, Tag = a, Cursor = System.Windows.Input.Cursors.Hand };
            System.Windows.Automation.AutomationProperties.SetName(caixa, "Atalho de " + a.Nome);
            caixa.PreviewKeyDown += (s, e) =>
            {
                var tecla = e.Key == System.Windows.Input.Key.System ? e.SystemKey : e.Key;
                var texto = AtalhosApp.Formatar(tecla, System.Windows.Input.Keyboard.Modifiers);
                if (texto != null) caixa.Text = texto;
                e.Handled = true;
            };
            Grid.SetColumn(caixa, 1);
            linha.Children.Add(caixa);
            var padrao = new Button { Content = "Padrão", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(6, 0, 0, 0), Style = (Style)FindResource("BtnContorno") };
            padrao.Click += (_, _) => caixa.Text = a.Padrao;
            Grid.SetColumn(padrao, 2);
            linha.Children.Add(padrao);
            PainelAtalhos.Children.Add(linha);
        }
    }

    private void RestaurarAtalhos_Click(object sender, RoutedEventArgs e)
    {
        foreach (var caixa in PainelAtalhos.Children.OfType<Grid>().SelectMany(g => g.Children.OfType<TextBox>())) caixa.Text = ((AtalhosApp.Acao)caixa.Tag).Padrao;
    }

    // 1.3: toque por tipo de chamada
    private static void PreencherToques(ComboBox cb, string? valor)
    {
        cb.Items.Clear();
        cb.Items.Add(new ComboBoxItem { Content = "Padrão (o toque acima)", Tag = null });
        foreach (var (id, nome) in Toques.Presets) cb.Items.Add(new ComboBoxItem { Content = nome, Tag = id });
        if (!string.IsNullOrWhiteSpace(valor) && Toques.Presets.All(p => p.Id != valor)) cb.Items.Add(new ComboBoxItem { Content = "Arquivo personalizado", Tag = valor });
        cb.SelectedIndex = cb.Items.Cast<ComboBoxItem>().ToList().FindIndex(i => (i.Tag as string) == valor) is var k and >= 0 ? k : 0;
    }

    private async void OuvirToqueTipo_Click(object sender, RoutedEventArgs e)
    {
        if (App.Atual.Telefone.Toques.Atual != TipoToque.Nenhum || sender is not Button b) return;
        var cb = (b.Tag as string) switch { "interno" => CbToqueInterno, "externo" => CbToqueExterno, _ => CbToqueFila };
        var escolha = (cb.SelectedItem as ComboBoxItem)?.Tag as string;
        var saidas = DispositivosAudio.AltoFalantes();
        bool ehArquivo = escolha != null && escolha.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) && File.Exists(escolha);
        using var teste = new Toques
        {
            DispositivoCampainha = DispositivosAudio.Indice(saidas, Escolhido(CaixaCampainha)),
            VolumeCampainha = (float)(SliderVolume.Value / 100),
            Preset = ehArquivo ? null : escolha,
            ArquivoCampainha = ehArquivo ? escolha : escolha == null && !string.IsNullOrWhiteSpace(CaixaToque.Text) ? CaixaToque.Text : null,
        };
        b.IsEnabled = false;
        teste.Tocar(TipoToque.Campainha);
        await Task.Delay(3000);
        teste.Parar();
        b.IsEnabled = true;
    }

    // ---------------- 1.2.7: aba Sons (listas para escolher o toque da chamada e o som das teclas) ----------------
    private string _toquePadrao = "classico", _somTecla = "dtmf";
    private bool _montandoSons;
    private Toques? _previa;

    private void MontarSons(ConfigApp cfg)
    {
        _montandoSons = true;
        _toquePadrao = Toques.Presets.Any(p => p.Id == cfg.ToquePadrao) ? cfg.ToquePadrao! : "classico";
        _somTecla = Toques.SonsTecla.Any(p => p.Id == cfg.SomTecla) ? cfg.SomTecla! : "dtmf";
        bool temArquivo = !string.IsNullOrWhiteSpace(cfg.ToqueArquivo);   // um .wav próprio vale mais que o toque pronto
        MontarLista(ListaToquePadrao, Toques.Presets, temArquivo ? "" : _toquePadrao, "toque",
            id => { _toquePadrao = id; CaixaToque.Text = ""; AtualizarTxtArquivoToque(); }, OuvirToquePadraoAsync);
        MontarLista(ListaSomTecla, Toques.SonsTecla, _somTecla, "tecla", id => _somTecla = id, OuvirSomTeclaAsync);
        AtualizarTxtArquivoToque();
        Closed += (_, _) => PararPrevia();
        _montandoSons = false;
    }

    private void MontarLista(Panel painel, (string Id, string Nome)[] opcoes, string escolhido, string grupo, Action<string> aoEscolher, Func<string, Task> ouvir)
    {
        painel.Children.Clear();
        foreach (var (id, nome) in opcoes)
        {
            var linha = new Grid { Margin = new Thickness(0, 1, 0, 1) };
            linha.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            linha.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var opcao = new RadioButton { Content = nome, GroupName = grupo, IsChecked = id == escolhido, VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(6, 5, 0, 5), Tag = id };
            opcao.SetResourceReference(ForegroundProperty, "TextPrimary");   // sem isso o texto sai preto nos temas escuros
            opcao.Checked += (_, _) => { if (_montandoSons) return; aoEscolher(id); _ = ouvir(id); };
            var botao = new Button { Content = "Ouvir", Padding = new Thickness(12, 3, 12, 3), Margin = new Thickness(8, 0, 0, 0) };
            botao.SetResourceReference(StyleProperty, "BtnContorno");
            botao.Click += (_, _) => _ = ouvir(id);
            Grid.SetColumn(botao, 1);
            linha.Children.Add(opcao);
            linha.Children.Add(botao);
            painel.Children.Add(linha);
        }
    }

    private async Task OuvirToquePadraoAsync(string id)
    {
        if (App.Atual.Telefone.Toques.Atual != TipoToque.Nenhum) return;   // não atrapalha uma chamada tocando
        PararPrevia();
        var saidas = DispositivosAudio.AltoFalantes();
        var t = new Toques
        {
            DispositivoCampainha = DispositivosAudio.Indice(saidas, Escolhido(CaixaCampainha)),
            VolumeCampainha = (float)(SliderVolume.Value / 100),
            Preset = id,
        };
        _previa = t;
        t.Tocar(TipoToque.Campainha);
        await Task.Delay(3500);
        if (_previa == t) PararPrevia();
    }

    private void PararPrevia()
    {
        var p = _previa;
        _previa = null;
        try { p?.Parar(); p?.Dispose(); } catch { }
    }

    private async Task OuvirSomTeclaAsync(string id)
    {
        var saidas = DispositivosAudio.AltoFalantes();
        var t = new Toques { DispositivoConversa = DispositivosAudio.Indice(saidas, Escolhido(CaixaAltoFalante)), PresetTecla = id };
        foreach (var tecla in "1590#") { t.Dtmf(tecla); await Task.Delay(230); }
    }

    private void ArquivoDeToque_Click(object sender, RoutedEventArgs e)
    {
        var antes = CaixaToque.Text;
        EscolherToque_Click(sender, e);
        if (CaixaToque.Text == antes) return;                  // cancelou
        PararPrevia();
        foreach (var rb in ListaToquePadrao.Children.OfType<Grid>().SelectMany(g => g.Children.OfType<RadioButton>())) rb.IsChecked = false;
        AtualizarTxtArquivoToque();
    }

    private void AtualizarTxtArquivoToque() =>
        TxtArquivoToque.Text = string.IsNullOrWhiteSpace(CaixaToque.Text) ? "" : "Usando o seu arquivo: " + Path.GetFileName(CaixaToque.Text) + ". Escolha um da lista para voltar aos toques prontos.";

    private async void TestarCampainha_Click(object sender, RoutedEventArgs e)
    {
        if (App.Atual.Telefone.Toques.Atual != TipoToque.Nenhum) return; // não atrapalha uma chamada tocando
        var saidas = DispositivosAudio.AltoFalantes();
        using var teste = new Toques
        {
            DispositivoCampainha = DispositivosAudio.Indice(saidas, Escolhido(CaixaCampainha)),
            VolumeCampainha = (float)(SliderVolume.Value / 100),
            ArquivoCampainha = string.IsNullOrWhiteSpace(CaixaToque.Text) ? null : CaixaToque.Text,
        };
        BtnTestar.IsEnabled = false;
        teste.Tocar(TipoToque.Campainha);
        await Task.Delay(3500);
        teste.Parar();
        BtnTestar.IsEnabled = true;
    }

    // ===================== microfone =====================

    private TesteMicrofone? _testeMic;
    private float _picoTeste;

    private void TestarMicrofone_Click(object sender, RoutedEventArgs e)
    {
        if (_testeMic != null) { PararTesteMic(); return; }
        _picoTeste = 0;
        _testeMic = new TesteMicrofone();
        float ganho = (float)(SliderGanho.Value / 100);
        _testeMic.Nivel += n => Dispatcher.BeginInvoke(() =>
        {
            NivelMic.Value = Math.Min(1, n * ganho);
            _picoTeste = Math.Max(_picoTeste, n);
            if (_picoTeste > 0.02f) MostrarMic("Microfone funcionando.", false);
        });
        var erro = _testeMic.Iniciar(DispositivosAudio.Indice(DispositivosAudio.Microfones(), Escolhido(CaixaMicrofone)));
        if (erro != null) { _testeMic = null; MostrarMic(erro, true); return; }
        BtnTestarMic.Content = "Parar teste";
        MostrarMic("Fale algo: a barra verde deve se mexer.", false);
    }

    private void PararTesteMic()
    {
        _testeMic?.Dispose();
        _testeMic = null;
        NivelMic.Value = 0;
        BtnTestarMic.Content = "Testar microfone";
    }

    private void MostrarMic(string texto, bool erro)
    {
        TxtMic.Text = texto;
        TxtMic.SetResourceReference(TextBlock.ForegroundProperty, erro ? "BadText" : "TextSecondary");
        TxtMic.Visibility = Visibility.Visible;
    }

    private void Microfone_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (_testeMic != null) PararTesteMic(); }

    protected override void OnClosed(EventArgs e)
    {
        PararTesteMic();
        base.OnClosed(e);
    }

    // ===================== geral =====================

    private void AbrirLogs_Click(object sender, RoutedEventArgs e)
    {
        var pasta = Path.Combine(Armazenamento.Pasta, "logs");
        Directory.CreateDirectory(pasta);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{pasta}\"") { UseShellExecute = true });
    }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private void Exportar_Click(object sender, RoutedEventArgs e)
    {
        var d = new SaveFileDialog { Title = "Exportar configurações", Filter = "Configurações do Softphone (*.json)|*.json", FileName = "softphone-config.json" };
        if (d.ShowDialog(this) != true) return;
        // sem segredos: a senha e o token são do usuário/PC (DPAPI) e não servem em outro lugar
        var copia = JsonSerializer.Deserialize<ConfigApp>(JsonSerializer.Serialize(_cfg, Json), Json)!;
        copia.SenhaProtegida = null; copia.HubTokenProtegido = null; copia.HubTokenExpira = default;
        copia.JanelaEsquerda = null; copia.JanelaTopo = null;
        File.WriteAllText(d.FileName, JsonSerializer.Serialize(copia, Json));
        Erro("");
        MessageBox.Show(this, "Configurações exportadas (sem a senha).", "Softphone");
    }

    private void Importar_Click(object sender, RoutedEventArgs e)
    {
        var d = new OpenFileDialog { Title = "Importar configurações", Filter = "Configurações do Softphone (*.json)|*.json" };
        if (d.ShowDialog(this) != true) return;
        try
        {
            var outra = JsonSerializer.Deserialize<ConfigApp>(File.ReadAllText(d.FileName), Json) ?? throw new InvalidDataException();
            var senha = CaixaSenha.Password;
            _carregando = true;
            Carregar(outra);
            CaixaSenha.Password = senha; // a senha nunca vem no arquivo
            _carregando = false;
            AtualizarRotulos();
            Erro("Importado. Confira e clique em Salvar.");
        }
        catch { Erro("Arquivo de configurações inválido."); }
    }

    private void Restaurar_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Voltar áudio, chamadas, chat e aparência ao padrão? A conta (ramal e senha) é mantida.", "Restaurar padrões",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        var p = new ConfigApp { Conta = _cfg.Conta, HubUrl = _cfg.HubUrl, Bloqueados = _cfg.Bloqueados };
        var senha = CaixaSenha.Password;
        _carregando = true;
        Carregar(p);
        CaixaSenha.Password = senha;
        _carregando = false;
        AtualizarRotulos();
        Erro("Padrões carregados. Clique em Salvar para aplicar.");
    }
}
