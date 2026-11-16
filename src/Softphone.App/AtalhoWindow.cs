using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Softphone.Core;

namespace Softphone.App;

/// <summary>
/// 1.2.4: janela de criar/editar um atalho da tela inicial. Montada em código (sem XAML) com as cores do tema atual.
/// Validação: Core\AtalhosTela.Validar (a mesma que o teste automático cobre).
/// </summary>
public sealed class AtalhoWindow : Window
{
    private readonly TextBox _nome = new() { MaxLength = AtalhosTela.MaxNome };
    private readonly ComboBox _tipo = new();
    private readonly TextBox _valor = new() { MaxLength = 500 };
    private readonly ComboBox _estado = new();
    private readonly Button _escolher = new() { Content = "Escolher pessoa...", Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBlock _rotuloValor = new();
    private readonly TextBlock _dica = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11.5, Margin = new Thickness(0, 4, 0, 0) };
    private readonly TextBlock _erro = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
    private readonly StackPanel _cores = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
    private string _cor = "verde";
    private string _login = "";   // login escolhido (tipo conversa)
    private readonly AtalhoTela? _original;
    private AtalhoTela? _resultado;

    private AtalhoWindow(AtalhoTela? original)
    {
        _original = original;
        Title = original == null ? "Novo atalho" : "Editar atalho";
        Width = 400; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        this.SetResourceReference(BackgroundProperty, "BgWindow");
        this.SetResourceReference(ForegroundProperty, "TextPrimary");
        Theme.AplicarBarraTitulo(this);

        foreach (var t in AtalhosTela.Tipos) _tipo.Items.Add(new ComboBoxItem { Content = AtalhosTela.NomeDoTipo(t), Tag = t });
        foreach (var s in new[] { ("Disponível", "Disponivel"), ("Ausente", "Ausente"), ("Não perturbe", "NaoPerturbe") }) _estado.Items.Add(new ComboBoxItem { Content = s.Item1, Tag = s.Item2 });
        _estado.SelectedIndex = 0;

        var raiz = new StackPanel { Margin = new Thickness(20, 16, 20, 18) };
        raiz.Children.Add(Rotulo("Nome do botão"));
        raiz.Children.Add(_nome);
        raiz.Children.Add(Rotulo("O que ele faz", 12));
        raiz.Children.Add(_tipo);
        _rotuloValor.FontSize = 12; _rotuloValor.Margin = new Thickness(0, 12, 0, 4);
        _rotuloValor.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        raiz.Children.Add(_rotuloValor);
        raiz.Children.Add(_valor);
        raiz.Children.Add(_estado);
        raiz.Children.Add(_escolher);
        _dica.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
        raiz.Children.Add(_dica);
        raiz.Children.Add(Rotulo("Cor", 12));
        raiz.Children.Add(_cores);
        _erro.SetResourceReference(TextBlock.ForegroundProperty, "BadText");
        raiz.Children.Add(_erro);

        var botoes = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var cancelar = new Button { Content = "Cancelar", Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
        var salvar = new Button { Content = "Salvar", Padding = new Thickness(18, 6, 18, 6), IsDefault = true };
        salvar.SetResourceReference(StyleProperty, "BtnVerde");
        cancelar.SetResourceReference(StyleProperty, "BtnContorno");
        salvar.Click += Salvar_Click;
        botoes.Children.Add(cancelar); botoes.Children.Add(salvar);
        raiz.Children.Add(botoes);
        Content = raiz;

        MontarCores();
        _tipo.SelectionChanged += (_, _) => AjustarCampos();
        _escolher.Click += Escolher_Click;
        _valor.TextChanged += (_, _) => { _erro.Visibility = Visibility.Collapsed; if (Tipo() == AtalhosTela.Conversa) _login = _valor.Text.Trim(); };

        if (original != null)
        {
            _nome.Text = original.Nome; _cor = AtalhosTela.CorValida(original.Cor);
            foreach (ComboBoxItem it in _tipo.Items) if ((string)it.Tag == original.Tipo) _tipo.SelectedItem = it;
            if (original.Tipo == AtalhosTela.Status) { foreach (ComboBoxItem it in _estado.Items) if ((string)it.Tag == original.Valor) _estado.SelectedItem = it; }
            else _valor.Text = original.Valor;
            _login = original.Tipo == AtalhosTela.Conversa ? original.Valor : "";
        }
        else _tipo.SelectedIndex = 0;
        AjustarCampos();
        MarcarCor();
        Loaded += (_, _) => _nome.Focus();
    }

    /// <summary>Abre a janela. Retorna o atalho novo/alterado (mesmo Id do original) ou null se cancelou.</summary>
    public static AtalhoTela? Editar(Window dono, AtalhoTela? original)
    {
        var j = new AtalhoWindow(original) { Owner = dono };
        return j.ShowDialog() == true ? j._resultado : null;
    }

    private TextBlock Rotulo(string texto, double acima = 0)
    {
        var t = new TextBlock { Text = texto, FontSize = 12, Margin = new Thickness(0, acima, 0, 4) };
        t.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        return t;
    }

    private string Tipo() => (_tipo.SelectedItem as ComboBoxItem)?.Tag as string ?? AtalhosTela.Ligar;

    private void AjustarCampos()
    {
        var t = Tipo();
        _estado.Visibility = t == AtalhosTela.Status ? Visibility.Visible : Visibility.Collapsed;
        _valor.Visibility = t == AtalhosTela.Status ? Visibility.Collapsed : Visibility.Visible;
        _escolher.Visibility = t is AtalhosTela.Ligar or AtalhosTela.Conversa or AtalhosTela.Transferir ? Visibility.Visible : Visibility.Collapsed;
        (_rotuloValor.Text, _dica.Text) = t switch
        {
            AtalhosTela.Ligar => ("Ramal ou número", "Liga na hora. Se for um colega, a bolinha mostra se ele está livre ou em ligação."),
            AtalhosTela.Conversa => ("Pessoa (login de rede)", "Abre a conversa com a pessoa. Use \"Escolher pessoa\"."),
            AtalhosTela.Transferir => ("Ramal ou número de destino", "Durante uma ligação, transfere para esse destino com um clique."),
            AtalhosTela.Dtmf => ("Tons (0-9, * e #)", "Durante uma ligação, envia os tons, por exemplo para a URA ou uma senha de sala."),
            AtalhosTela.Link => ("Endereço (http ou https)", "Abre no navegador, por exemplo o GLPI ou o painel de ramais."),
            _ => ("Status", "Muda seu status para os colegas com um clique."),
        };
        _erro.Visibility = Visibility.Collapsed;
    }

    private void Escolher_Click(object sender, RoutedEventArgs e)
    {
        var t = Tipo();
        var escolhido = EntradaWindow.Perguntar(this, "Escolher pessoa", "Escolha quem:", "Escolher");
        if (escolhido == null) return;
        if (t == AtalhosTela.Conversa)
        {
            var p = App.Atual.Chat.PessoaPorRamal(escolhido);
            if (p == null) { Erro("Essa pessoa não tem ramal cadastrado no diretório; use o login de rede."); return; }
            _login = p.Login; _valor.Text = p.Login;
            if (_nome.Text.Trim().Length == 0) _nome.Text = Primeiro(p.Nome);
        }
        else
        {
            _valor.Text = escolhido;
            var p = App.Atual.Chat.PessoaPorRamal(escolhido);
            if (p != null && _nome.Text.Trim().Length == 0) _nome.Text = Primeiro(p.Nome);
        }
    }

    private static string Primeiro(string nome) { var p = (nome ?? "").Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries); return p.Length == 0 ? "" : p[0]; }

    private void Erro(string msg) { _erro.Text = msg; _erro.Visibility = Visibility.Visible; }

    private void MontarCores()
    {
        foreach (var nome in AtalhosTela.Cores)
        {
            var c = nome;
            var cor = (Color)ColorConverter.ConvertFromString(c switch
            {
                "azul" => "#2A82CC", "ambar" => "#E08A22", "vermelho" => "#D64536", "roxo" => "#9150D1", "cinza" => "#73859D", _ => "#23A052",
            });
            var b = new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(15), Margin = new Thickness(0, 0, 8, 0), Background = new SolidColorBrush(cor), BorderThickness = new Thickness(3), Cursor = Cursors.Hand, Tag = c, Focusable = true, ToolTip = c };
            b.MouseLeftButtonUp += (_, _) => { _cor = c; MarcarCor(); };
            b.KeyDown += (_, e) => { if (e.Key is Key.Enter or Key.Space) { _cor = c; MarcarCor(); e.Handled = true; } };
            System.Windows.Automation.AutomationProperties.SetName(b, "Cor " + c);
            _cores.Children.Add(b);
        }
    }

    private void MarcarCor()
    {
        foreach (Border b in _cores.Children)
        {
            if ((string)b.Tag == _cor) b.SetResourceReference(Border.BorderBrushProperty, "TextPrimary");
            else b.BorderBrush = Brushes.Transparent;
        }
    }

    private void Salvar_Click(object sender, RoutedEventArgs e)
    {
        var t = Tipo();
        var valor = t == AtalhosTela.Status ? ((_estado.SelectedItem as ComboBoxItem)?.Tag as string ?? "") : _valor.Text.Trim();
        var a = new AtalhoTela { Id = _original?.Id ?? Guid.NewGuid().ToString("N"), Nome = _nome.Text.Trim(), Tipo = t, Valor = valor, Cor = _cor };
        if (AtalhosTela.Validar(a) is { } erro) { Erro(erro); return; }
        _resultado = a;
        DialogResult = true;
    }
}
