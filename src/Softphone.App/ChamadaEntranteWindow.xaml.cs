using System;
using System.Linq;
using System.Windows;
using Softphone.Core;

namespace Softphone.App;

/// <summary>Aviso de chamada recebida no canto da tela (não rouba o foco de quem está digitando).</summary>
public partial class ChamadaEntranteWindow : Window
{
    private static int _abertos;
    private readonly int _posicao;

    public event Action? Atender;
    public event Action? Recusar;

    public ChamadaEntranteWindow(ChamadaVM vm)
    {
        InitializeComponent();
        DataContext = vm;
        _posicao = _abertos++;
        if (App.Atual.Telefone.Chamadas.Any(c => c.Conectada)) TxtEspera.Visibility = Visibility.Visible;
        Loaded += (_, _) =>
        {
            var area = SystemParameters.WorkArea;
            Left = area.Right - ActualWidth - 8;
            Top = App.Atual.Config.AvisoNoTopo ? area.Top + 4 + (ActualHeight + 4) * _posicao : area.Bottom - (ActualHeight + 4) * (_posicao + 1);
        };
        Closed += (_, _) => _abertos = Math.Max(0, _abertos - 1);
    }

    private void Atender_Click(object sender, RoutedEventArgs e) { Atender?.Invoke(); Close(); App.Atual.MostrarJanela(); }
    private void Recusar_Click(object sender, RoutedEventArgs e) { Recusar?.Invoke(); Close(); }
}
