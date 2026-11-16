using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using Softphone.Contratos;

namespace Softphone.App;

/// <summary>
/// Comunicado da TI: aparece por cima de tudo, no canto de cima da tela, até a pessoa clicar em "Ciente".
/// Urgente = faixa vermelha e som; importante = amarelo; informativo = cor do tema. Vários ficam empilhados.
/// </summary>
public partial class ComunicadoWindow : Window
{
    private static readonly Dictionary<long, ComunicadoWindow> Abertos = new();
    private readonly ComunicadoDto _c;
    private readonly Action<long> _confirmar;

    public static int Quantos => Abertos.Count;

    public static void Mostrar(ComunicadoDto c, Action<long> confirmar)
    {
        if (Abertos.ContainsKey(c.Id) || c.ExpiraEm < DateTime.UtcNow) return;
        var w = new ComunicadoWindow(c, confirmar);
        Abertos[c.Id] = w;
        w.Show();
        Reposicionar();
        if (c.Nivel == NivelComunicado.Urgente) System.Media.SystemSounds.Exclamation.Play();
        App.Atual.Notificar("Comunicado da TI", c.Titulo);
    }

    /// <summary>A TI cancelou: some sem precisar de "Ciente".</summary>
    public static void Cancelar(long id)
    {
        if (Abertos.TryGetValue(id, out var w)) w.Close();
    }

    private static void Reposicionar()
    {
        var area = SystemParameters.WorkArea;
        double topo = area.Top + 8;
        foreach (var w in Abertos.Values.OrderBy(x => x._c.Id))
        {
            w.UpdateLayout();
            w.Left = area.Right - w.ActualWidth - 8;
            w.Top = topo;
            topo += Math.Max(w.ActualHeight, 120) - 6;
            if (topo > area.Bottom - 120) topo = area.Top + 40; // muitos: começa a sobrepor
        }
    }

    public ComunicadoWindow(ComunicadoDto c, Action<long> confirmar)
    {
        InitializeComponent();
        _c = c;
        _confirmar = confirmar;
        TxtTitulo.Text = c.Titulo;
        TxtTexto.Text = c.Texto;
        BtnCiente.Content = c.ExigirCiencia ? "Ciente" : "Fechar";
        TxtRodape.Text = $"{c.NomeAutor} · {c.CriadoEm.ToLocalTime():dd/MM HH:mm}" + (c.Unidade is { } u ? $" · para a unidade {u}" : " · para todos");
        (string nivel, string icone, string cor) = c.Nivel switch
        {
            NivelComunicado.Urgente => ("Comunicado URGENTE da TI", "", "CallRed"),
            NivelComunicado.Importante => ("Comunicado importante da TI", "", "WarnBorder"),
            _ => ("Comunicado da TI", "", "Accent"),
        };
        TxtNivel.Text = nivel;
        TxtIcone.Text = icone;
        Faixa.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, cor);
        Moldura.SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty, cor);
        Closed += (_, _) => { Abertos.Remove(_c.Id); Reposicionar(); };
        // arrastar pela janela inteira
        MouseLeftButtonDown += (_, e) => { if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed) try { DragMove(); } catch { } };
    }

    private void Ciente_Click(object sender, RoutedEventArgs e)
    {
        if (_c.ExigirCiencia) _confirmar(_c.Id);   // 1.3: sem ciência exigida, fechar só fecha (não vira "ciente" no painel)
        Close();
    }
}
