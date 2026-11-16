using System.Windows;
using System.Windows.Shapes;

namespace Softphone.App;

/// <summary>
/// app:Pincel.Recurso="{Binding CorPresenca}": pinta o Fill de um Shape com o pincel do TEMA cujo nome vem do binding
/// (ex.: "DotOk"). Assim a cor acompanha o tema claro/escuro, coisa que um Binding direto para Brush não faria.
/// </summary>
public static class Pincel
{
    public static readonly DependencyProperty RecursoProperty = DependencyProperty.RegisterAttached(
        "Recurso", typeof(string), typeof(Pincel), new PropertyMetadata(null, (d, e) =>
        {
            if (e.NewValue is not string chave || chave.Length == 0) return;
            if (d is Shape s) s.SetResourceReference(Shape.FillProperty, chave);
            else if (d is System.Windows.Controls.Border b) b.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, chave);
            else if (d is System.Windows.Controls.Panel p) p.SetResourceReference(System.Windows.Controls.Panel.BackgroundProperty, chave);
            else if (d is System.Windows.Controls.TextBlock t) t.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, chave);
        }));

    public static string? GetRecurso(DependencyObject o) => (string?)o.GetValue(RecursoProperty);
    public static void SetRecurso(DependencyObject o, string? v) => o.SetValue(RecursoProperty, v);
}
