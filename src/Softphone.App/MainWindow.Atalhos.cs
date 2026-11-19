using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Softphone.Contratos;
using Softphone.Core;

namespace Softphone.App;

/// <summary>
/// 1.2.4 (Claude Code, 11/11/2026, pedido do Ana): aba "Atalhos" (como a do MicroSIP, só que com cartões, presença ao vivo e temas)
/// e botão de temas do cabeçalho. A regra de validação fica em Core\AtalhosTela.cs (testada); aqui só a tela e as ações.
/// </summary>
public partial class MainWindow
{
    private DispatcherTimer? _relogioAtalhos;
    private readonly Dictionary<string, Action> _atualizadoresAtalhos = new();

    private List<AtalhoTela> ListaAtalhos => App.Config.BotoesAtalho ??= new();

    // ------------------------------------------------------------ aba
    private void MostrarAtalhos()
    {
        App.Config.BotoesAtalho = AtalhosTela.Normalizar(App.Config.BotoesAtalho);   // limpa o que veio do arquivo (inválidos, repetidos)
        MontarAtalhos();
        if (_relogioAtalhos == null)
        {
            _relogioAtalhos = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _relogioAtalhos.Tick += (_, _) => { foreach (var f in _atualizadoresAtalhos.Values) f(); };
        }
        _relogioAtalhos.Start();
    }

    private void PararAtalhos() => _relogioAtalhos?.Stop();

    private void MontarAtalhos()
    {
        GradeAtalhos.Children.Clear();
        _atualizadoresAtalhos.Clear();
        foreach (var a in ListaAtalhos) GradeAtalhos.Children.Add(CriarCartaoAtalho(a));
        bool vazio = ListaAtalhos.Count == 0;
        TxtDicaAtalhos.Text = vazio
            ? "Crie botões para ligar com um clique, abrir uma conversa, mudar seu status e mais."
            : "Clique para usar. Botão direito para editar, mover ou excluir.";
        if (vazio)
        {
            var caixa = new Border { Margin = new Thickness(4, 6, 4, 0), Padding = new Thickness(16, 14, 16, 14), CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1), Width = 300 };
            caixa.SetResourceReference(Border.BackgroundProperty, "BgSurface");
            caixa.SetResourceReference(Border.BorderBrushProperty, "BorderSubtle");
            var pilha = new StackPanel();
            pilha.Children.Add(Texto("Nenhum atalho ainda", 14, true, "TextPrimary"));
            pilha.Children.Add(Texto("Toque em Novo para criar o primeiro: por exemplo, a Recepção para ligar direto, ou \"Almoço\" para mudar seu status para ausente.", 12, false, "TextMuted", margem: new Thickness(0, 4, 0, 0)));
            caixa.Child = pilha;
            GradeAtalhos.Children.Add(caixa);
        }
        BtnNovoAtalho.IsEnabled = ListaAtalhos.Count < AtalhosTela.Maximo;
        BtnNovoAtalho.ToolTip = ListaAtalhos.Count < AtalhosTela.Maximo ? null : $"O máximo é {AtalhosTela.Maximo} atalhos.";
    }

    private static TextBlock Texto(string texto, double tamanho, bool negrito, string cor, Thickness? margem = null)
    {
        var t = new TextBlock { Text = texto, FontSize = tamanho, TextWrapping = TextWrapping.Wrap };
        if (negrito) t.FontWeight = FontWeights.SemiBold;
        if (margem is { } m) t.Margin = m;
        t.SetResourceReference(TextBlock.ForegroundProperty, cor);
        return t;
    }

    private static (Color, Color) CoresAtalho(string cor) => AtalhosTela.CorValida(cor) switch
    {
        "azul" => (Color.FromRgb(0x2A, 0x82, 0xCC), Color.FromRgb(0x14, 0x4E, 0x80)),
        "ambar" => (Color.FromRgb(0xE0, 0x8A, 0x22), Color.FromRgb(0x9A, 0x55, 0x0C)),
        "vermelho" => (Color.FromRgb(0xD6, 0x45, 0x36), Color.FromRgb(0x8E, 0x23, 0x1A)),
        "roxo" => (Color.FromRgb(0x91, 0x50, 0xD1), Color.FromRgb(0x56, 0x26, 0x8C)),
        "cinza" => (Color.FromRgb(0x73, 0x85, 0x9D), Color.FromRgb(0x46, 0x52, 0x63)),
        _ => (Color.FromRgb(0x23, 0xA0, 0x52), Color.FromRgb(0x12, 0x6B, 0x34)),
    };

    private static string IconeAtalho(string tipo) => tipo switch
    {
        AtalhosTela.Conversa => "",
        AtalhosTela.Transferir => "",
        AtalhosTela.Dtmf => "",
        AtalhosTela.Link => "",
        AtalhosTela.Status => "",
        _ => "",
    };

    private PessoaVM? PessoaDoAtalho(AtalhoTela a) => a.Tipo switch
    {
        AtalhosTela.Ligar or AtalhosTela.Transferir => App.Chat.PessoaPorRamal(a.Valor),
        AtalhosTela.Conversa => App.Chat.Pessoas.FirstOrDefault(p => string.Equals(p.Login, a.Valor, StringComparison.OrdinalIgnoreCase)),
        _ => null,
    };

    private Chamada? ChamadaAtiva() => Tel.Chamadas.FirstOrDefault(x => x.Estado == EstadoChamada.EmAndamento);

    private static string NomeDoEstado(string v) => v switch { "Disponivel" => "disponível", "Ausente" => "ausente", "NaoPerturbe" => "não perturbe", _ => v };

    private FrameworkElement CriarCartaoAtalho(AtalhoTela a)
    {
        var (c1, c2) = CoresAtalho(a.Cor);
        var disco = new Border { Width = 46, Height = 46, CornerRadius = new CornerRadius(23), Background = new LinearGradientBrush(c1, c2, 45) };
        var interno = new TextBlock { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.White };
        disco.Child = interno;
        var ponto = new Ellipse { Width = 13, Height = 13, StrokeThickness = 2, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, -1, -1), Visibility = Visibility.Collapsed };
        ponto.SetResourceReference(Shape.StrokeProperty, "BgSurface");
        var discoComPonto = new Grid { Width = 46, Height = 46, HorizontalAlignment = HorizontalAlignment.Center };
        discoComPonto.Children.Add(disco);
        discoComPonto.Children.Add(ponto);

        var nome = new TextBlock { Text = a.Nome, FontSize = 12.5, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 90, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 7, 0, 0) };
        nome.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
        var sub = new TextBlock { FontSize = 10.5, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 90, HorizontalAlignment = HorizontalAlignment.Center };
        sub.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");

        var pilha = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        pilha.Children.Add(discoComPonto);
        pilha.Children.Add(nome);
        pilha.Children.Add(sub);

        var cartao = new Border { Width = 104, Height = 104, Margin = new Thickness(4), CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1), Cursor = Cursors.Hand, Focusable = true, Child = pilha, ToolTip = a.Nome };
        cartao.SetResourceReference(Border.BackgroundProperty, "BgSurface");
        cartao.SetResourceReference(Border.BorderBrushProperty, "BorderSubtle");
        System.Windows.Automation.AutomationProperties.SetName(cartao, $"{a.Nome} ({AtalhosTela.NomeDoTipo(a.Tipo)})");
        cartao.MouseEnter += (_, _) => cartao.SetResourceReference(Border.BackgroundProperty, "BgHover");
        cartao.MouseLeave += (_, _) => cartao.SetResourceReference(Border.BackgroundProperty, "BgSurface");
        cartao.MouseLeftButtonUp += (_, e) => { if (e.ClickCount >= 1) ExecutarAtalho(a); };
        cartao.KeyDown += (_, e) => { if (e.Key is Key.Enter or Key.Space) { ExecutarAtalho(a); e.Handled = true; } };

        var menu = new ContextMenu();
        MenuItem Item(string titulo, Action acao) { var m = new MenuItem { Header = titulo }; m.Click += (_, _) => acao(); return m; }
        menu.Items.Add(Item("Editar...", () => EditarAtalho(a)));
        menu.Items.Add(Item("Mover para antes", () => MoverAtalho(a, -1)));
        menu.Items.Add(Item("Mover para depois", () => MoverAtalho(a, +1)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Excluir", () => ExcluirAtalho(a)));
        cartao.ContextMenu = menu;

        void Atualizar()
        {
            var pessoa = PessoaDoAtalho(a);
            if (pessoa != null && a.Tipo is AtalhosTela.Ligar or AtalhosTela.Conversa)
            {
                interno.ClearValue(TextBlock.FontFamilyProperty);   // fonte normal (iniciais)
                interno.Text = TextoPresenca.Iniciais(pessoa.Nome); interno.FontSize = 16; interno.FontWeight = FontWeights.SemiBold;
                ponto.Visibility = Visibility.Visible;
                ponto.SetResourceReference(Shape.FillProperty, pessoa.CorPresenca);
                ponto.ToolTip = pessoa.TextoStatus;
            }
            else
            {
                interno.FontFamily = (FontFamily)FindResource("Icones");
                interno.Text = IconeAtalho(a.Tipo); interno.FontSize = 19; interno.FontWeight = FontWeights.Normal;
                ponto.Visibility = Visibility.Collapsed;
            }
            sub.Text = a.Tipo switch
            {
                AtalhosTela.Ligar => pessoa != null ? "ramal " + a.Valor : a.Valor,
                AtalhosTela.Conversa => "conversa",
                AtalhosTela.Transferir => "transferir → " + (pessoa?.Nome ?? a.Valor),
                AtalhosTela.Dtmf => "tons " + a.Valor,
                AtalhosTela.Link => Uri.TryCreate(a.Valor, UriKind.Absolute, out var u) ? u.Host : "link",
                AtalhosTela.Status => NomeDoEstado(a.Valor),
                _ => a.Valor,
            };
            bool precisaChamada = a.Tipo is AtalhosTela.Transferir or AtalhosTela.Dtmf;
            bool ok = !precisaChamada || ChamadaAtiva() != null;
            cartao.Opacity = ok ? 1 : 0.5;
            cartao.ToolTip = ok ? $"{a.Nome}: {AtalhosTela.NomeDoTipo(a.Tipo).ToLowerInvariant()}" : $"{a.Nome}: só funciona durante uma ligação";
        }
        Atualizar();
        _atualizadoresAtalhos[a.Id] = Atualizar;
        return cartao;
    }

    // ------------------------------------------------------------ ações
    private async void ExecutarAtalho(AtalhoTela a)
    {
        try
        {
            switch (a.Tipo)
            {
                case AtalhosTela.Ligar:
                    await LigarAsync(a.Valor);
                    break;
                case AtalhosTela.Conversa:
                    await App.Chat.AbrirComAsync(a.Valor);
                    break;
                case AtalhosTela.Transferir:
                    if (ChamadaAtiva() is { } ct && Vm(ct) is { } vmT) await TransferirChamadaAsync(vmT, Telefone.LimparDestino(a.Valor));
                    else MostrarAvisoTemporario("Transferir só funciona durante uma ligação.");
                    break;
                case AtalhosTela.Dtmf:
                    if (ChamadaAtiva() is { } cd)
                        foreach (var ch in a.Valor) { Tel.Toques.Dtmf(ch); await Tel.EnviarDtmfAsync(cd, ch); await Task.Delay(140); }
                    else MostrarAvisoTemporario("Os tons só podem ser enviados durante uma ligação.");
                    break;
                case AtalhosTela.Link:
                    if (!AtalhosTela.LinkSeguro(a.Valor)) { MostrarAvisoTemporario("Link inválido: só http e https."); break; }
                    Process.Start(new ProcessStartInfo(a.Valor.Trim()) { UseShellExecute = true });
                    break;
                case AtalhosTela.Status:
                    if (Enum.TryParse<EstadoPresenca>(a.Valor, out var estado)) { await DefinirMinhaPresencaAsync(estado); MostrarAvisoTemporario("Status: " + NomeDoEstado(a.Valor) + "."); }
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Escrever("Atalho \"" + a.Nome + "\" falhou: " + ex.Message);
            MostrarAvisoTemporario("Não foi possível executar o atalho: " + ex.Message);
        }
    }

    // ------------------------------------------------------------ edição
    private void NovoAtalho_Click(object sender, RoutedEventArgs e)
    {
        if (ListaAtalhos.Count >= AtalhosTela.Maximo) return;
        var novo = AtalhoWindow.Editar(this, null);
        if (novo == null) return;
        ListaAtalhos.Add(novo);
        SalvarAtalhos();
    }

    private void EditarAtalho(AtalhoTela a)
    {
        var editado = AtalhoWindow.Editar(this, a);
        if (editado == null) return;
        int i = ListaAtalhos.FindIndex(x => x.Id == a.Id);
        if (i >= 0) ListaAtalhos[i] = editado;
        SalvarAtalhos();
    }

    private void MoverAtalho(AtalhoTela a, int delta)
    {
        if (AtalhosTela.Mover(ListaAtalhos, a.Id, delta)) SalvarAtalhos();
    }

    private void ExcluirAtalho(AtalhoTela a)
    {
        if (MessageBox.Show(this, $"Excluir o atalho \"{a.Nome}\"?", "Softphone", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        ListaAtalhos.RemoveAll(x => x.Id == a.Id);
        SalvarAtalhos();
    }

    private void SalvarAtalhos()
    {
        App.SalvarConfig();
        MontarAtalhos();
    }

    // ------------------------------------------------------------ temas
    private void Temas_Click(object sender, RoutedEventArgs e)
    {
        var atual = string.IsNullOrWhiteSpace(App.Config.Tema) ? "Sistema" : App.Config.Tema;
        var menu = new ContextMenu { PlacementTarget = BtnTemas, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        foreach (var t in Theme.Disponiveis)
        {
            var amostra = new Border
            {
                Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Margin = new Thickness(0, 0, 10, 0), BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Color.FromArgb(70, 128, 128, 128)),
                Background = new LinearGradientBrush((Color)ColorConverter.ConvertFromString(t.Cor1), (Color)ColorConverter.ConvertFromString(t.Cor2), 90),
            };
            var linha = new StackPanel { Orientation = Orientation.Horizontal };
            linha.Children.Add(amostra);
            linha.Children.Add(new TextBlock { Text = t.Nome, VerticalAlignment = VerticalAlignment.Center });
            var id = t.Id;
            var item = new MenuItem { Header = linha, IsCheckable = false, IsChecked = string.Equals(id, atual, StringComparison.OrdinalIgnoreCase) };
            item.Click += (_, _) =>
            {
                App.Config.Tema = id;
                App.SalvarConfig();
                Theme.Reaplicar();
            };
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        var cor = new MenuItem { Header = "Escolher outra cor...", IsChecked = string.Equals(atual, "Personalizado", StringComparison.OrdinalIgnoreCase) };
        cor.Click += (_, _) => new CorTemaWindow { Owner = this }.ShowDialog();
        menu.Items.Add(cor);
        menu.IsOpen = true;
    }
}
