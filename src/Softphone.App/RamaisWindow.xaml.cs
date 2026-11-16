using System;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using Softphone.Core;

namespace Softphone.App;

/// <summary>
/// Painel de ramais (recepção): todos os ramais da empresa em blocos coloridos pela presença (livre, em ligação, ausente,
/// sem Softphone), por unidade. Com uma ligação em andamento, arrasta a ligação até a pessoa para transferir direto, ou usa
/// os botões do bloco (transferir / consultar). Sem ligação: ligar ou conversar.
/// A presença vem do Hub, então só aparece "em ligação" para quem usa o Softphone.
/// </summary>
public partial class RamaisWindow : Window, INotifyPropertyChanged
{
    private static RamaisWindow? _aberta;
    private const string Todas = "Todas as unidades";
    private const string FormatoArrasto = "Softphone.Chamada";
    private readonly ICollectionView _visao;
    private readonly DispatcherTimer _relogio = new() { Interval = TimeSpan.FromSeconds(1) };
    private App App => App.Atual;

    public event PropertyChangedEventHandler? PropertyChanged;

    public static void Abrir()
    {
        if (_aberta is { } j) { if (j.WindowState == WindowState.Minimized) j.WindowState = WindowState.Normal; j.Activate(); return; }
        _aberta = new RamaisWindow();
        _aberta.Closed += (_, _) => _aberta = null;
        _aberta.Show();
    }

    public RamaisWindow()
    {
        InitializeComponent();
        Theme.AplicarBarraTitulo(this);
        DataContext = this;
        _visao = new ListCollectionView(App.Chat.Pessoas) { Filter = Filtrar };
        _visao.GroupDescriptions.Add(new PropertyGroupDescription(nameof(PessoaVM.Unidade), new UnidadeOuOutros()));
        _visao.SortDescriptions.Add(new SortDescription(nameof(PessoaVM.Unidade), ListSortDirection.Ascending));
        _visao.SortDescriptions.Add(new SortDescription(nameof(PessoaVM.Nome), ListSortDirection.Ascending));
        Blocos.ItemsSource = _visao;
        PreencherUnidades();
        // presença muda o tempo todo; refazer o filtro a cada poucos segundos basta (os blocos já mudam de cor na hora)
        int tiques = 0;
        _relogio.Tick += (_, _) => { AtualizarChamada(); if (++tiques % 5 == 0 && ChkSoOnline.IsChecked == true) _visao.Refresh(); };
        _relogio.Start();
        App.Chat.Mudou += AoMudarChat;
        Closed += (_, _) => { _relogio.Stop(); App.Chat.Mudou -= AoMudarChat; };
        AtualizarChamada();
        AtualizarTotais();
        Loaded += (_, _) => CaixaBusca.Focus();
    }

    private void AoMudarChat()
    {
        if (CaixaUnidade.Items.Count <= 1) PreencherUnidades();
        AtualizarTotais();
    }

    private void PreencherUnidades()
    {
        var atual = CaixaUnidade.SelectedItem as string;
        var unidades = App.Chat.Pessoas.Select(p => p.Unidade).Where(u => !string.IsNullOrWhiteSpace(u)).Distinct(StringComparer.CurrentCultureIgnoreCase)
            .OrderBy(u => u, StringComparer.CurrentCultureIgnoreCase).ToList();
        CaixaUnidade.ItemsSource = new[] { Todas }.Concat(unidades!).ToList();
        CaixaUnidade.SelectedItem = atual != null && unidades.Contains(atual) ? atual : Todas;
    }

    private bool Filtrar(object o)
    {
        if (o is not PessoaVM p) return false;
        if (ChkSoRamal.IsChecked == true && !p.TemRamal) return false;
        if (ChkSoOnline.IsChecked == true && !p.Online) return false;
        if (CaixaUnidade.SelectedItem is string u && u != Todas && !string.Equals(p.Unidade, u, StringComparison.CurrentCultureIgnoreCase)) return false;
        var busca = CaixaBusca.Text.Trim();
        return p.Combina(busca);
    }

    private void Filtro_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized || _visao is null) return;
        DicaBusca.Visibility = CaixaBusca.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _visao.Refresh();
        AtualizarTotais();
    }

    private void AtualizarTotais()
    {
        int n = _visao.Cast<object>().Count();
        int livres = _visao.Cast<PessoaVM>().Count(p => p.Presenca == Softphone.Contratos.EstadoPresenca.Disponivel);
        int ocupados = _visao.Cast<PessoaVM>().Count(p => p.Presenca == Softphone.Contratos.EstadoPresenca.EmLigacao);
        TxtTotal.Text = $"{n} ramais · {livres} livres · {ocupados} em ligação";
        TxtVazio.Text = App.Chat.Estado != EstadoHub.Conectado ? "O painel precisa do servidor do chat (sem conexão agora)." : "Ninguém com esse filtro.";
        TxtVazio.Visibility = n == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ===================== ligação em andamento =====================

    /// <summary>A ligação que será transferida: a que está em andamento (senão, a primeira em espera).</summary>
    private Chamada? ChamadaAtual => App.Telefone.Chamadas.FirstOrDefault(c => c.Estado == EstadoChamada.EmAndamento && c.ConsultaDe == null)
                                     ?? App.Telefone.Chamadas.FirstOrDefault(c => c.Estado == EstadoChamada.EmEspera);
    public bool TemChamada => ChamadaAtual != null;
    public bool SemChamada => !TemChamada;
    private bool _tinhaChamada;

    private void AtualizarChamada()
    {
        var c = ChamadaAtual;
        FaixaChamada.Visibility = c != null ? Visibility.Visible : Visibility.Collapsed;
        if (c != null)
        {
            var nome = App.NomeDoContato(c.Numero) ?? c.Nome ?? c.Numero;
            var d = c.Duracao;
            TxtChamada.Text = $"{nome} · {(d.TotalHours >= 1 ? d.ToString(@"h\:mm\:ss") : d.ToString(@"m\:ss"))}" + (c.Estado == EstadoChamada.EmEspera ? " · em espera" : "");
        }
        if ((c != null) != _tinhaChamada)
        {
            _tinhaChamada = c != null;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TemChamada)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SemChamada)));
        }
    }

    private void Chip_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || ChamadaAtual is not { } c) return;
        DragDrop.DoDragDrop(ChipChamada, new DataObject(FormatoArrasto, c.Id.ToString()), DragDropEffects.Move);
    }

    private void Bloco_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(FormatoArrasto) && (sender as FrameworkElement)?.DataContext is PessoaVM { TemRamal: true } ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Bloco_Drop(object sender, DragEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PessoaVM p || !p.TemRamal) return;
        var id = e.Data.GetData(FormatoArrasto) as string;
        var c = App.Telefone.Chamadas.FirstOrDefault(x => x.Id.ToString() == id);
        if (c != null) await TransferirAsync(c, p);
    }

    private PessoaVM? PessoaDe(object sender) => (sender as FrameworkElement)?.DataContext as PessoaVM;

    private async System.Threading.Tasks.Task TransferirAsync(Chamada c, PessoaVM p)
    {
        if (!p.TemRamal) return;
        var r = await App.Telefone.TransferirAsync(c, p.Ramal!);
        if (r.Ok) App.Notificar("Transferida", $"Ligação passada para {p.Nome} (ramal {p.Ramal}).");
        else MessageBox.Show(this, (r.Motivo ?? "O PABX não aceitou a transferência.") + " A ligação continua com você.", "Softphone", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async void Transferir_Click(object sender, RoutedEventArgs e)
    {
        if (PessoaDe(sender) is { } p && ChamadaAtual is { } c) await TransferirAsync(c, p);
    }

    private async void Consultar_Click(object sender, RoutedEventArgs e)
    {
        if (PessoaDe(sender) is not { TemRamal: true } p || ChamadaAtual is not { } c) return;
        try { await App.Telefone.IniciarConsultaAsync(c, p.Ramal!); App.MostrarJanela(); }
        catch (Exception ex) { MessageBox.Show(this, "Não foi possível ligar: " + ex.Message, "Softphone"); }
    }

    private void Ligar_Click(object sender, RoutedEventArgs e)
    {
        if (PessoaDe(sender) is not { TemRamal: true } p) return;
        App.MostrarJanela();
        _ = App.MainWindow.LigarAsync(p.Ramal!);
    }

    private void Conversar_Click(object sender, RoutedEventArgs e)
    {
        if (PessoaDe(sender) is { } p) _ = App.Chat.AbrirComAsync(p.Login);
    }

    /// <summary>Dois cliques: em ligação = consultar (fala antes); sem ligação = ligar.</summary>
    private void Bloco_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2) return;
        if (TemChamada) Consultar_Click(sender, e); else Ligar_Click(sender, e);
        e.Handled = true;
    }

    private sealed class UnidadeOuOutros : IValueConverter
    {
        public object Convert(object value, Type t, object p, System.Globalization.CultureInfo c) => string.IsNullOrWhiteSpace(value as string) ? "Sem unidade" : value!;
        public object ConvertBack(object value, Type t, object p, System.Globalization.CultureInfo c) => throw new NotSupportedException();
    }
}
