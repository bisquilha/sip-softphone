using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Softphone.Contratos;
using Microsoft.Win32;

namespace Softphone.App;

public partial class ChatWindow : Window
{
    private readonly ChatServico _chat;
    private readonly ConversaVM _conversa;
    private readonly ObservableCollection<MensagemVM> _msgs = new();
    private readonly DispatcherTimer _fimDigitando = new() { Interval = TimeSpan.FromSeconds(4) };
    private DateTime _ultimoAvisoDigitando;
    private bool _carregando;
    private bool _temMais = true;
    private long _lidaPorTodos;
    // 1.1: responder (citar) / editar / @menções
    private MensagemVM? _respondendo, _editando;
    private readonly Dictionary<string, string> _mencoes = new(StringComparer.CurrentCultureIgnoreCase); // "@Nome" -> login
    private int _inicioMencao = -1;
    private bool V11 => _chat.Cliente?.ServidorV11 == true;

    public ChatWindow(ChatServico chat, ConversaVM conversa)
    {
        InitializeComponent();
        Theme.AplicarBarraTitulo(this);
        _chat = chat;
        _conversa = conversa;
        _lidaPorTodos = conversa.Dto.UltimaLidaPorTodos;
        ListaMensagens.ItemsSource = _msgs;
        var cfg = App.Atual.Config;
        if (cfg.ChatLargura is double w && cfg.ChatAltura is double h) { Width = w; Height = h; }
        _fimDigitando.Tick += (_, _) => { _fimDigitando.Stop(); TxtDigitando.Visibility = Visibility.Collapsed; };
        conversa.PropertyChanged += (_, _) => AtualizarCabecalho();
        if (conversa.Outra != null) conversa.Outra.PropertyChanged += (_, _) => AtualizarCabecalho();
        AtualizarCabecalho();
        Loaded += async (_, _) => { await CarregarAsync(); Caixa.Focus(); };
        Activated += (_, _) => MarcarLida();
        Closing += (_, _) => { if (WindowState == WindowState.Normal) { cfg.ChatLargura = Width; cfg.ChatAltura = Height; } };
    }

    private void AtualizarCabecalho()
    {
        Title = _conversa.Titulo + " - Softphone";
        TxtTitulo.Text = _conversa.Titulo;
        TxtIniciais.Text = _conversa.Iniciais;
        TxtIniciais.FontFamily = _conversa.IniciaisSaoIcone ? (System.Windows.Media.FontFamily)FindResource("Icones") : new System.Windows.Media.FontFamily("Segoe UI");
        var outra = _conversa.Outra;
        if (_conversa.Grupo)
        {
            TxtSub.Text = $"{_conversa.Dto.Membros.Length} participantes: " + string.Join(", ", _conversa.Dto.Membros.Take(6).Select(m => _chat.Pessoa(m)?.Nome.Split(' ')[0] ?? m));
            PontoPresenca.Visibility = Visibility.Collapsed;
            BtnLigar.Visibility = Visibility.Collapsed;
        }
        else
        {
            TxtSub.Text = outra is null ? "" : string.Join(" · ", new[] { outra.TextoStatus, outra.Setor, outra.TemRamal ? "ramal " + outra.Ramal : null }.Where(x => !string.IsNullOrWhiteSpace(x)));
            PontoPresenca.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, outra?.CorPresenca ?? "DotOff");
            BtnLigar.Visibility = outra?.TemRamal == true ? Visibility.Visible : Visibility.Collapsed;
            BtnLigar.ToolTip = outra?.TemRamal == true ? $"Ligar para o ramal {outra.Ramal}" : null;
        }
    }

    // ===================== mensagens =====================

    private string NomeDe(string login) => _chat.Pessoa(login)?.Nome ?? login;
    private bool EhMinha(string login) => string.Equals(login, _chat.EuLogin, StringComparison.OrdinalIgnoreCase);

    private MensagemVM Criar(MensagemDto m, MensagemDto? anterior)
    {
        bool minha = EhMinha(m.Autor);
        bool mostrarAutor = _conversa.Grupo && !minha && (anterior is null || anterior.Autor != m.Autor);
        return new MensagemVM(m, minha, NomeDe(m.Autor), mostrarAutor, NomeDe, _chat.EuLogin) { Lida = minha && m.Id <= _lidaPorTodos };
    }

    private async Task CarregarAsync()
    {
        if (_chat.Cliente is null || _carregando) return;
        _carregando = true;
        try
        {
            long antes = _msgs.FirstOrDefault()?.Id ?? 0;
            var lote = await _chat.Cliente.HistoricoAsync(_conversa.Id, antes, 50);
            _temMais = lote.Count == 50;
            double alturaAntes = Rolagem.ExtentHeight;
            MensagemDto? ant = null;
            var novos = lote.Select(m => { var vm = Criar(m, ant); ant = m; return vm; }).ToList();
            for (int i = novos.Count - 1; i >= 0; i--) _msgs.Insert(0, novos[i]);
            BtnAnteriores.Visibility = _temMais ? Visibility.Visible : Visibility.Collapsed;
            TxtVazio.Visibility = _msgs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
            if (antes == 0) Rolagem.ScrollToEnd();
            else Rolagem.ScrollToVerticalOffset(Rolagem.ExtentHeight - alturaAntes);
            MarcarLida();
        }
        catch (Exception ex) { TxtVazio.Text = "Não foi possível carregar as mensagens: " + ex.Message; TxtVazio.Visibility = Visibility.Visible; }
        finally { _carregando = false; }
    }

    private void Anteriores_Click(object sender, RoutedEventArgs e) => _ = CarregarAsync();

    private void Rolagem_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.VerticalOffset == 0 && e.VerticalChange < 0 && _temMais) _ = CarregarAsync();
    }

    /// <summary>Mensagem nova chegou (do outro ou a minha confirmada pelo servidor).</summary>
    public void AoReceber(MensagemDto m)
    {
        if (_msgs.Any(x => x.Id == m.Id)) return;
        bool noFim = Rolagem.VerticalOffset >= Rolagem.ScrollableHeight - 40;
        _msgs.Add(Criar(m, _msgs.LastOrDefault()?.Dto));
        TxtVazio.Visibility = Visibility.Collapsed;
        if (!EhMinha(m.Autor)) { TxtDigitando.Visibility = Visibility.Collapsed; _fimDigitando.Stop(); }
        if (noFim || EhMinha(m.Autor)) Dispatcher.BeginInvoke(Rolagem.ScrollToEnd, DispatcherPriority.Loaded);
        if (IsActive) MarcarLida();
    }

    /// <summary>Depois de reconectar (R09): busca de novo as mensagens mais recentes; acrescenta as que chegaram com o chat
    /// fora e atualiza as que foram editadas ou apagadas nesse tempo (antes a janela aberta ficava sem elas).</summary>
    public async Task RessincronizarAsync()
    {
        if (_chat.Cliente is null) return;
        try
        {
            var recentes = await _chat.Cliente.HistoricoAsync(_conversa.Id, 0, 50);
            foreach (var m in recentes.OrderBy(x => x.Id))
            {
                var vm = _msgs.FirstOrDefault(x => x.Id == m.Id);
                if (vm is null)
                {
                    if (_msgs.Count == 0 || m.Id > _msgs[^1].Id) { AoReceber(m); continue; }
                    if (m.Id < _msgs[0].Id) continue; // mais antiga do que o que está na tela: vem com "anteriores"
                    int i = 0;
                    while (i < _msgs.Count && _msgs[i].Id < m.Id) i++;
                    _msgs.Insert(i, Criar(m, i > 0 ? _msgs[i - 1].Dto : null));
                }
                else if (vm.Dto.Texto != m.Texto || vm.Dto.Apagada != m.Apagada) AoAlterar(m);
            }
            TxtVazio.Visibility = _msgs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (IsActive) MarcarLida();
        }
        catch (Exception ex) { Log.Escrever($"Chat: não deu para atualizar a conversa {_conversa.Id} depois de reconectar: {ex.Message}"); }
    }

    /// <summary>Mensagem editada ou apagada pelo autor.</summary>
    public void AoAlterar(MensagemDto m)
    {
        _msgs.FirstOrDefault(x => x.Id == m.Id)?.Substituir(m);
        // citações desta mensagem nas respostas: recarrega o trecho mostrado
        foreach (var r in _msgs.Where(x => x.Dto.RespostaA == m.Id))
            r.Substituir(r.Dto with { RespostaTrecho = m.Apagada ? m.Texto : (m.Texto.Length > 140 ? m.Texto[..140] + "…" : m.Texto) });
        if (_editando?.Id == m.Id && m.Apagada) CancelarBarra();
    }

    /// <summary>Vai até a mensagem (carrega o histórico mais antigo até achar) e destaca por alguns segundos.</summary>
    public async Task IrParaAsync(long id)
    {
        for (int i = 0; i < 40 && !_msgs.Any(x => x.Id == id) && _temMais; i++)
        {
            if (!IsLoaded) await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
            await CarregarAsync();
        }
        var vm = _msgs.FirstOrDefault(x => x.Id == id);
        if (vm is null) return;
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        if (ListaMensagens.ItemContainerGenerator.ContainerFromItem(vm) is FrameworkElement el) el.BringIntoView();
        vm.Destacada = true;
        await Task.Delay(2500);
        vm.Destacada = false;
    }

    private void Citacao_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is MensagemVM { Dto.RespostaA: long id }) _ = IrParaAsync(id);
    }

    // ----- menu do balão: responder, copiar, editar, apagar -----

    private void Balao_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: MensagemVM m, ContextMenu: { } menu }) return;
        foreach (var item in menu.Items.OfType<MenuItem>())
            item.Visibility = (item.Tag as string) switch
            {
                "responder" => V11 && m.PodeResponder ? Visibility.Visible : Visibility.Collapsed,
                "copiar" => m.TemTexto && !m.Apagada ? Visibility.Visible : Visibility.Collapsed,
                "editar" or "apagar" => V11 && m.PodeMudar ? Visibility.Visible : Visibility.Collapsed,
                _ => Visibility.Visible,
            };
        foreach (var s in menu.Items.OfType<Separator>()) s.Visibility = V11 && m.PodeMudar ? Visibility.Visible : Visibility.Collapsed;
        if (!menu.Items.OfType<MenuItem>().Any(i => i.Visibility == Visibility.Visible)) e.Handled = true;
    }

    private async void MenuMensagem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string acao } item || item.DataContext is not MensagemVM m) return;
        switch (acao)
        {
            case "responder": Responder(m); break;
            case "copiar": try { Clipboard.SetText(m.Texto); } catch { } break;
            case "editar": Editar(m); break;
            case "apagar":
                if (MessageBox.Show(this, "Apagar esta mensagem para todos? Aparece \"Mensagem apagada\" no lugar.", "Softphone",
                        MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                try { AoAlterar(await _chat.Cliente!.ApagarMensagemAsync(m.Id)); }
                catch (Exception ex) { MessageBox.Show(this, "Não foi possível apagar: " + LimparErro(ex), "Softphone"); }
                break;
        }
    }

    // ----- só para o modo de teste (ModoTeste.cs) -----
    internal void TesteResponderUltima() { if (_msgs.LastOrDefault(m => !m.Minha && m.PodeResponder) is { } m) Responder(m); }
    internal void TesteEditarUltima() { if (_msgs.LastOrDefault(m => m.PodeMudar) is { } m) Editar(m); }
    internal async Task TesteApagarUltimaAsync()
    {
        if (_msgs.LastOrDefault(m => m.PodeMudar) is { } m && _chat.Cliente != null) AoAlterar(await _chat.Cliente.ApagarMensagemAsync(m.Id));
    }

    private static string LimparErro(Exception ex)
    {
        var msg = ex.Message;
        var i = msg.IndexOf("HubException: ", StringComparison.Ordinal);
        return i >= 0 ? msg[(i + 14)..] : msg;
    }

    private void Responder(MensagemVM m)
    {
        _editando = null;
        _respondendo = m;
        TxtBarraTitulo.Text = "Respondendo a " + (m.Minha ? "você mesmo" : m.AutorNome);
        TxtBarraTrecho.Text = m.TemArquivo && !m.TemTexto ? "📎 " + m.ArquivoNome : m.Texto.Replace('\n', ' ');
        BarraResposta.Visibility = Visibility.Visible;
        Caixa.Focus();
    }

    private void Editar(MensagemVM m)
    {
        _respondendo = null;
        _editando = m;
        TxtBarraTitulo.Text = "Editando a mensagem (Enter salva, Esc cancela)";
        TxtBarraTrecho.Text = m.Texto.Replace('\n', ' ');
        BarraResposta.Visibility = Visibility.Visible;
        Caixa.Text = m.Texto;
        Caixa.CaretIndex = Caixa.Text.Length;
        Caixa.Focus();
    }

    private void CancelarBarra()
    {
        bool estavaEditando = _editando != null;
        _respondendo = null; _editando = null;
        BarraResposta.Visibility = Visibility.Collapsed;
        if (estavaEditando) Caixa.Clear();
    }

    private void CancelarBarra_Click(object sender, RoutedEventArgs e) { CancelarBarra(); Caixa.Focus(); }

    // ----- @menção: ao digitar "@" aparece a lista de quem está na conversa -----

    private IEnumerable<PessoaVM> Participantes() => _conversa.Dto.Membros
        .Where(l => !string.Equals(l, _chat.EuLogin, StringComparison.OrdinalIgnoreCase))
        .Select(l => _chat.Pessoa(l)).Where(p => p != null)!;

    private void AtualizarMencao()
    {
        if (!V11) return;
        int pos = Caixa.CaretIndex;
        var texto = Caixa.Text;
        int arroba = pos > 0 ? texto.LastIndexOf('@', pos - 1) : -1;
        // "@" no começo ou depois de espaço, e até o cursor sem quebra de linha (permite nome com espaço: "@Ana Pa")
        if (arroba < 0 || (arroba > 0 && !char.IsWhiteSpace(texto[arroba - 1])) || texto[arroba..pos].Contains('\n') || pos - arroba > 30)
        { PopupMencao.IsOpen = false; _inicioMencao = -1; return; }
        var filtro = texto[(arroba + 1)..pos];
        var lista = Participantes().Where(p => filtro.Length == 0 || p.Nome.Contains(filtro, StringComparison.CurrentCultureIgnoreCase)).Take(8).ToList();
        if (lista.Count == 0) { PopupMencao.IsOpen = false; _inicioMencao = -1; return; }
        _inicioMencao = arroba;
        ListaMencao.ItemsSource = lista;
        ListaMencao.SelectedIndex = 0;
        PopupMencao.IsOpen = true;
    }

    private void EscolherMencao(PessoaVM p)
    {
        if (_inicioMencao < 0) return;
        int pos = Caixa.CaretIndex;
        var nome = "@" + p.Nome;
        Caixa.Text = Caixa.Text[.._inicioMencao] + nome + " " + Caixa.Text[pos..];
        Caixa.CaretIndex = _inicioMencao + nome.Length + 1;
        _mencoes[nome] = p.Login;
        PopupMencao.IsOpen = false;
        _inicioMencao = -1;
        Caixa.Focus();
    }

    private void ListaMencao_Click(object sender, MouseButtonEventArgs e) { if (ListaMencao.SelectedItem is PessoaVM p) EscolherMencao(p); }

    /// <summary>Logins das menções que continuam no texto na hora de enviar.</summary>
    private string[] MencoesNoTexto(string texto) => _mencoes.Where(kv => texto.Contains(kv.Key, StringComparison.CurrentCultureIgnoreCase)).Select(kv => kv.Value).Distinct().ToArray();

    public void AoLer(string login, long ultimaId)
    {
        if (EhMinha(login)) return;
        // ✓✓ quando todos os outros leram: em conversa direta é o próprio; em grupo, o mínimo de todos (vem no ConversaDto)
        if (!_conversa.Grupo) _lidaPorTodos = Math.Max(_lidaPorTodos, ultimaId);
        else _lidaPorTodos = Math.Max(_lidaPorTodos, _conversa.Dto.UltimaLidaPorTodos);
        foreach (var m in _msgs.Where(x => x.Minha && x.Id <= _lidaPorTodos)) m.Lida = true;
    }

    public void AoDigitar(string login)
    {
        if (EhMinha(login)) return;
        TxtDigitando.Text = $"{NomeDe(login).Split(' ')[0]} está digitando...";
        TxtDigitando.Visibility = Visibility.Visible;
        _fimDigitando.Stop(); _fimDigitando.Start();
    }

    private void MarcarLida()
    {
        var ultimaDeOutro = _msgs.LastOrDefault(m => !m.Minha);
        if (ultimaDeOutro != null && (IsActive || !IsLoaded)) _ = _chat.MarcarLidaAsync(_conversa, ultimaDeOutro.Id);
    }

    // ===================== enviar =====================

    private void Caixa_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (PopupMencao.IsOpen)
        {
            int n = ListaMencao.Items.Count;
            if (e.Key == Key.Down) { ListaMencao.SelectedIndex = (ListaMencao.SelectedIndex + 1) % n; e.Handled = true; return; }
            if (e.Key == Key.Up) { ListaMencao.SelectedIndex = (ListaMencao.SelectedIndex - 1 + n) % n; e.Handled = true; return; }
            if (e.Key is Key.Enter or Key.Tab && ListaMencao.SelectedItem is PessoaVM p) { EscolherMencao(p); e.Handled = true; return; }
            if (e.Key == Key.Escape) { PopupMencao.IsOpen = false; e.Handled = true; return; }
        }
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None) { e.Handled = true; _ = EnviarAsync(); }
        else if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control && Clipboard.ContainsImage()) { e.Handled = true; _ = EnviarPrintAsync(); }
        else if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control && Clipboard.ContainsFileDropList())
        {
            // arquivos copiados no Explorer (Ctrl+C) e colados aqui
            e.Handled = true;
            var arquivos = Clipboard.GetFileDropList().Cast<string>().Where(File.Exists).Take(5).ToList();
            _ = EnviarVariosAsync(arquivos);
        }
        else if (e.Key == Key.Up && Caixa.Text.Length == 0 && V11 && _msgs.LastOrDefault(m => m.PodeMudar) is { } ultima) { e.Handled = true; Editar(ultima); }
        else if (e.Key == Key.Escape)
        {
            if (BarraResposta.Visibility == Visibility.Visible) { CancelarBarra(); e.Handled = true; }
            else Close();
        }
    }

    private void Caixa_TextChanged(object sender, TextChangedEventArgs e)
    {
        DicaCaixa.Visibility = Caixa.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        AtualizarMencao();
        if (Caixa.Text.Length > 0 && DateTime.Now - _ultimoAvisoDigitando > TimeSpan.FromSeconds(3) && _chat.Cliente is { } c)
        {
            _ultimoAvisoDigitando = DateTime.Now;
            _ = c.DigitarAsync(_conversa.Id).ContinueWith(_ => { }, TaskScheduler.Default);
        }
    }

    private void Enviar_Click(object sender, RoutedEventArgs e) => _ = EnviarAsync();

    private bool _enviando;

    private async Task EnviarAsync(string? arquivoId = null, string? texto = null)
    {
        // R10: Enter repetido enquanto a mensagem ainda vai não manda duas vezes
        if (_enviando) return;
        texto ??= Caixa.Text.Trim();
        if (texto.Length == 0 && arquivoId is null) return;
        if (_chat.Cliente is null) return;
        _enviando = true;
        BtnEnviar.IsEnabled = false;
        // R10: só limpa a caixa se ela ainda tiver o texto que foi enviado (o que a pessoa começou a digitar depois fica)
        void LimparSeAindaFor(string enviado) { if (Caixa.Text.Trim() == enviado) { Caixa.Clear(); _mencoes.Clear(); } }
        try
        {
            if (_editando is { } ed && arquivoId is null)
            {
                if (texto.Length > 0 && texto != ed.Texto) AoAlterar(await _chat.Cliente.EditarMensagemAsync(ed.Id, texto));
                LimparSeAindaFor(texto);
                CancelarBarra();
                return;
            }
            MensagemDto m;
            if (V11)
            {
                var resp = _respondendo?.Id;
                m = await _chat.Cliente.EnviarMensagemAsync(_conversa.Id, texto, arquivoId, resp, MencoesNoTexto(texto));
                _respondendo = null;
                BarraResposta.Visibility = Visibility.Collapsed;
            }
            else m = await _chat.Cliente.EnviarAsync(_conversa.Id, texto, arquivoId);
            LimparSeAindaFor(texto);
            AoReceber(m);
        }
        catch (Exception ex) { MessageBox.Show(this, "Mensagem não enviada: " + LimparErro(ex), "Softphone", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { _enviando = false; BtnEnviar.IsEnabled = true; Caixa.Focus(); }
    }

    private async Task EnviarArquivoAsync(string caminho)
    {
        if (_chat.Cliente is null) return;
        var info = new FileInfo(caminho);
        if (info.Length > 25L * 1024 * 1024) { MessageBox.Show(this, "O arquivo passa de 25 MB.", "Softphone"); return; }
        try
        {
            TxtDigitando.Text = $"Enviando {info.Name}...";
            TxtDigitando.Visibility = Visibility.Visible;
            var arq = await _chat.Cliente.EnviarArquivoAsync(caminho);
            await EnviarAsync(arq.Id, Caixa.Text.Trim());
        }
        catch (Exception ex) { MessageBox.Show(this, "Arquivo não enviado: " + ex.Message, "Softphone", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { TxtDigitando.Visibility = Visibility.Collapsed; }
    }

    private async Task EnviarVariosAsync(List<string> arquivos) { foreach (var a in arquivos) await EnviarArquivoAsync(a); }

    private void Anexar_Click(object sender, RoutedEventArgs e)
    {
        var d = new OpenFileDialog { Title = "Enviar arquivo", Multiselect = false };
        if (d.ShowDialog(this) == true) _ = EnviarArquivoAsync(d.FileName);
    }

    private async Task EnviarPrintAsync()
    {
        var img = Clipboard.GetImage();
        if (img is null) return;
        var caminho = Path.Combine(Path.GetTempPath(), $"print-{DateTime.Now:yyyyMMdd-HHmmss}.png");
        using (var fs = File.Create(caminho))
        {
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(img));
            enc.Save(fs);
        }
        await EnviarArquivoAsync(caminho);
        try { File.Delete(caminho); } catch { }
    }

    private void Janela_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Janela_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] arquivos)
            foreach (var a in arquivos.Where(File.Exists).Take(5)) await EnviarArquivoAsync(a);
    }

    private async void Arquivo_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not MensagemVM m || m.Dto.Arquivo is null || _chat.Cliente is null) return;
        var pasta = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Softphone");
        Directory.CreateDirectory(pasta);
        var nome = string.Concat(m.Dto.Arquivo.Nome.Split(Path.GetInvalidFileNameChars()));
        var destino = Path.Combine(pasta, nome);
        if (File.Exists(destino)) destino = Path.Combine(pasta, $"{Path.GetFileNameWithoutExtension(nome)} ({m.Id}){Path.GetExtension(nome)}");
        try
        {
            if (!File.Exists(destino)) await _chat.Cliente.BaixarArquivoAsync(m.Dto.Arquivo.Id, destino);
            // abre a pasta com o arquivo selecionado (não executa o arquivo direto, por segurança)
            System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{destino}\"");
        }
        catch (Exception ex) { MessageBox.Show(this, "Não foi possível baixar: " + ex.Message, "Softphone"); }
    }

    private void Video_Click(object sender, RoutedEventArgs e) => _ = _chat.VideoNaConversaAsync(_conversa);

    private void EntrarReuniao_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is MensagemVM { EhReuniao: true } m) _ = _chat.EntrarAsync(m.SalaReuniao, m.TituloReuniao);
    }

    private void Ligar_Click(object sender, RoutedEventArgs e)
    {
        var ramal = _conversa.Outra?.Ramal;
        if (string.IsNullOrWhiteSpace(ramal)) return;
        App.Atual.MostrarJanela();
        _ = App.Atual.MainWindow.LigarAsync(ramal);
    }
}
