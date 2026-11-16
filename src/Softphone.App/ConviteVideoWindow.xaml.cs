using System;
using System.Windows;
using System.Windows.Threading;
using Softphone.Contratos;

namespace Softphone.App;

/// <summary>Chamada de vídeo tocando (canto da tela). Some sozinha em 45 s, como chamada não atendida.</summary>
public partial class ConviteVideoWindow : Window
{
    private readonly DispatcherTimer _limite = new() { Interval = TimeSpan.FromSeconds(45) };
    private bool _respondido;

    public ConviteDto Convite { get; }
    public event Action<bool>? Respondeu;   // true = atender, false = recusar
    public event Action? Expirou;

    public ConviteVideoWindow(ConviteDto convite)
    {
        InitializeComponent();
        Convite = convite;
        TxtTipo.Text = convite.Grupo ? "Reunião do grupo" : "Chamada de vídeo";
        TxtNome.Text = convite.NomeDe;
        TxtTitulo.Text = convite.Grupo ? convite.Titulo : "está te chamando por vídeo";
        Loaded += (_, _) =>
        {
            var area = SystemParameters.WorkArea;
            Left = area.Right - ActualWidth - 8;
            Top = App.Atual.Config.AvisoNoTopo ? area.Top + 4 : area.Bottom - ActualHeight - 4;
        };
        _limite.Tick += (_, _) => { _limite.Stop(); if (!_respondido) { _respondido = true; Expirou?.Invoke(); Close(); } };
        _limite.Start();
        Closed += (_, _) => _limite.Stop();
    }

    private void Atender_Click(object sender, RoutedEventArgs e) { if (_respondido) return; _respondido = true; Respondeu?.Invoke(true); Close(); }
    private void Recusar_Click(object sender, RoutedEventArgs e) { if (_respondido) return; _respondido = true; Respondeu?.Invoke(false); Close(); }

    /// <summary>Quem chamou desistiu (ou atendi em outro PC).</summary>
    public void Cancelar() { if (_respondido) return; _respondido = true; Close(); }
}
