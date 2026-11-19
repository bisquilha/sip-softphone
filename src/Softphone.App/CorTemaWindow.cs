using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Softphone.App;

/// <summary>1.2.10: tabela de cores para escolher a cor do tema. Clicar numa cor já mostra o tema ao vivo; Cancelar volta ao anterior.</summary>
internal sealed class CorTemaWindow : Window
{
    private readonly string _temaAntes;
    private readonly string? _corAntes;
    private readonly TextBox _hex = new() { Width = 96, MaxLength = 7, VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(4, 3, 4, 3) };
    private readonly Border _previa = new() { Width = 34, Height = 28, CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1) };
    private Button? _marcado;

    public CorTemaWindow()
    {
        _temaAntes = App.Atual.Config.Tema;
        _corAntes = App.Atual.Config.CorTema;

        Title = "Cor do tema - Softphone";
        Width = 420; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "BgWindow");
        Theme.AplicarBarraTitulo(this);

        var raiz = new StackPanel { Margin = new Thickness(18) };
        var titulo = new TextBlock { Text = "Escolha a cor do tema", FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) };
        titulo.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
        var dica = new TextBlock { Text = "Clique numa cor da tabela. O app muda na hora; só vale de verdade quando você clicar em Salvar.", TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 0, 0, 12) };
        dica.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
        raiz.Children.Add(titulo);
        raiz.Children.Add(dica);

        // tabela: 12 matizes x 5 tons + linha de cinzas
        const int colunas = 12;
        var grade = new UniformGrid { Columns = colunas };
        double[] tons = { 0.62, 0.52, 0.42, 0.34, 0.26 };
        foreach (var l in tons)
            for (int i = 0; i < colunas; i++)
                grade.Children.Add(Amostra(DeHsl(i * 30.0, 0.62, l)));
        for (int i = 0; i < colunas; i++)
            grade.Children.Add(Amostra(DeHsl(0, 0, 0.82 - i * 0.065)));
        raiz.Children.Add(new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(2), Child = grade, BorderBrush = new SolidColorBrush(Color.FromArgb(70, 128, 128, 128)) });

        var linha = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
        var rotulo = new TextBlock { Text = "Outra cor (#RRGGBB):", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        rotulo.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        linha.Children.Add(rotulo);
        linha.Children.Add(_hex);
        _previa.Margin = new Thickness(8, 0, 0, 0);
        linha.Children.Add(_previa);
        raiz.Children.Add(linha);
        _hex.TextChanged += (_, _) =>
        {
            if (TentarCor(_hex.Text, out var c)) { _previa.Background = new SolidColorBrush(c); if (_hex.IsKeyboardFocusWithin) Aplicar(c); }
        };

        var botoes = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var salvar = new Button { Content = "Salvar", Padding = new Thickness(16, 5, 16, 5), Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        if (Application.Current.TryFindResource("BtnPrimario") is Style sp) salvar.Style = sp;
        var cancelar = new Button { Content = "Cancelar", Padding = new Thickness(14, 5, 14, 5), IsCancel = true };
        if (Application.Current.TryFindResource("BtnContorno") is Style sc) cancelar.Style = sc;
        salvar.Click += (_, _) => { App.Atual.SalvarConfig(); DialogResult = true; };
        cancelar.Click += (_, _) => { Reverter(); DialogResult = false; };
        botoes.Children.Add(salvar);
        botoes.Children.Add(cancelar);
        raiz.Children.Add(botoes);
        Content = raiz;

        Closing += (_, _) => { if (DialogResult != true) Reverter(); };

        if (TentarCor(_corAntes, out var atual)) { _hex.Text = Hex(atual); _previa.Background = new SolidColorBrush(atual); }
    }

    private Button Amostra(Color c)
    {
        var b = new Button
        {
            Height = 28, Margin = new Thickness(1.5), Padding = new Thickness(0), BorderThickness = new Thickness(2), BorderBrush = Brushes.Transparent,
            Background = new SolidColorBrush(c), Cursor = Cursors.Hand, ToolTip = Hex(c), Focusable = true,
            Template = ModeloAmostra(),
        };
        b.Click += (_, _) =>
        {
            if (_marcado != null) _marcado.BorderBrush = Brushes.Transparent;
            _marcado = b; b.BorderBrush = Brushes.White;
            _hex.Text = Hex(c); _previa.Background = new SolidColorBrush(c);
            Aplicar(c);
        };
        System.Windows.Automation.AutomationProperties.SetName(b, "Cor " + Hex(c));
        return b;
    }

    private static ControlTemplate ModeloAmostra()
    {
        var t = new ControlTemplate(typeof(Button));
        var f = new FrameworkElementFactory(typeof(Border));
        f.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        f.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding("BorderBrush") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        f.SetBinding(Border.BorderThicknessProperty, new System.Windows.Data.Binding("BorderThickness") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        f.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        t.VisualTree = f;
        return t;
    }

    private static void Aplicar(Color c)
    {
        App.Atual.Config.CorTema = Hex(c);
        App.Atual.Config.Tema = "Personalizado";
        Theme.Reaplicar();
    }

    private void Reverter()
    {
        App.Atual.Config.Tema = _temaAntes;
        App.Atual.Config.CorTema = _corAntes;
        Theme.Reaplicar();
    }

    internal static bool TentarCor(string? s, out Color cor)
    {
        cor = default;
        if (string.IsNullOrWhiteSpace(s)) return false;
        s = s.Trim();
        if (!s.StartsWith('#')) s = "#" + s;
        if (s.Length != 7) return false;
        try { cor = (Color)ColorConverter.ConvertFromString(s); return true; } catch { return false; }
    }

    internal static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    internal static Color DeHsl(double h, double s, double l)
    {
        h = ((h % 360) + 360) % 360;
        double c = (1 - Math.Abs(2 * l - 1)) * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = l - c / 2;
        (double r, double g, double b) = h switch { < 60 => (c, x, 0.0), < 120 => (x, c, 0.0), < 180 => (0.0, c, x), < 240 => (0.0, x, c), < 300 => (x, 0.0, c), _ => (c, 0.0, x) };
        return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }

    internal static (double h, double s, double l) ParaHsl(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), l = (max + min) / 2, d = max - min;
        if (d < 1e-9) return (0, 0, l);
        double s = d / (1 - Math.Abs(2 * l - 1));
        double h = max == r ? 60 * (((g - b) / d) % 6) : max == g ? 60 * ((b - r) / d + 2) : 60 * ((r - g) / d + 4);
        return ((h + 360) % 360, s, l);
    }
}
