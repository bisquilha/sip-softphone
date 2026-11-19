using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace Softphone.App;

/// <summary>Segue o tema claro/escuro do Windows (só lê HKCU). SOFTPHONE_TEMA=claro|escuro força um dos dois.</summary>
internal static class Theme
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    public static bool IsLight { get; private set; } = true;
    /// <summary>Id do tema em uso (Claro, Escuro, Oliva...), já resolvido (nunca "Sistema").</summary>
    public static string Atual { get; private set; } = "Claro";
    public static event Action? Changed;

    /// <summary>1.2.4: temas do botão de temas. Cor1/Cor2 são as cores do degradê (amostra no menu). Sistema segue o Windows (claro ou escuro).</summary>
    public sealed record Info(string Id, string Nome, string Cor1, string Cor2);
    public static readonly Info[] Disponiveis =
    {
        new("Sistema", "Seguir o Windows", "#F3F6F4", "#0F1613"),
        new("Claro", "Claro", "#D5E8E0", "#F3F6F4"),
        new("Escuro", "Escuro", "#14423B", "#030807"),
        new("Oliva", "Oliva", "#6B7F2A", "#000000"),
        new("Oceano", "Oceano", "#1F6FB2", "#000000"),
        new("Grafite", "Grafite", "#4A4F57", "#000000"),
        new("Vinho", "Vinho", "#A3283F", "#000000"),
        new("Roxo", "Roxo", "#7B3FB8", "#000000"),
        new("Ambar", "Âmbar", "#C2701A", "#000000"),
    };

    public static void Initialize()
    {
        Apply(Resolver());
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        Application.Current.Exit += (_, _) => SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
    }

    private static void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is not (UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle)) return;
        Application.Current?.Dispatcher.BeginInvoke(() => Apply(Resolver()));
    }

    private static string? Conhecido(string? id) => string.Equals(id?.Trim(), "Personalizado", StringComparison.OrdinalIgnoreCase) ? "Personalizado" : Disponiveis.FirstOrDefault(t => t.Id != "Sistema" && string.Equals(t.Id, id?.Trim(), StringComparison.OrdinalIgnoreCase))?.Id;

    private static string Resolver()
    {
        // SOFTPHONE_TEMA força um tema (testes); depois vem a escolha nas Configurações / no botão de temas
        if (Conhecido(Environment.GetEnvironmentVariable("SOFTPHONE_TEMA")) is { } forcado) return forcado;
        if (Conhecido((Application.Current as App)?.Config?.Tema) is { } escolhido) return escolhido;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey, writable: false);
            return key?.GetValue("AppsUseLightTheme") is not int value || value != 0 ? "Claro" : "Escuro";
        }
        catch { return "Claro"; }
    }

    /// <summary>Reaplica depois de mudar o tema nas Configurações ou no botão de temas.</summary>
    public static void Reaplicar() { if (Application.Current != null) Apply(Resolver()); }

    private static void Apply(string id)
    {
        var dictionaries = Application.Current.Resources.MergedDictionaries;
        var arquivo = id switch { "Claro" => "Light", "Escuro" => "Dark", _ => id };   // os dois primeiros mantêm os nomes antigos dos arquivos
        if (id == "Personalizado") arquivo = "Oliva";   // base: paleta escura; as cores de destaque são trocadas pela cor escolhida
        var uri = new Uri($"pack://application:,,,/Themes/{arquivo}.xaml", UriKind.Absolute);
        var dic = new ResourceDictionary { Source = uri };
        if (id == "Personalizado") AplicarCorPersonalizada(dic);
        if (dictionaries.Count > 0) dictionaries[0] = dic;
        else dictionaries.Add(dic);
        Atual = id;
        IsLight = id == "Claro";
        Changed?.Invoke();
    }

    /// <summary>1.2.10: gera as cores do tema "Personalizado" a partir da cor escolhida (matiz da cor + fundos escuros).</summary>
    private static void AplicarCorPersonalizada(ResourceDictionary d)
    {
        var cfg = (Application.Current as App)?.Config;
        if (!CorTemaWindow.TentarCor(cfg?.CorTema, out var escolhida)) return;
        var (h, s, l) = CorTemaWindow.ParaHsl(escolhida);
        double sf = Math.Min(s, 0.45);          // fundos: pouca saturação
        double sa = Math.Min(s, 0.85);
        double la = Math.Clamp(l, 0.30, 0.46);  // destaque com contraste para texto branco
        Color C(double sat, double lum) => CorTemaWindow.DeHsl(h, s < 0.05 ? 0 : sat, lum);
        void P(string k, Color c) => d[k] = new SolidColorBrush(c);
        P("BgWindow", C(sf, 0.06)); P("BgSurface", C(sf, 0.10)); P("BgSurfaceAlt", C(sf, 0.08));
        P("BgHover", C(sf, 0.14)); P("BgPressed", C(sf, 0.18)); P("BorderSubtle", C(sf, 0.22));
        P("Accent", C(sa, la)); P("AccentHover", C(sa, la + 0.06));
        P("AccentText", C(Math.Min(sa, 0.7), 0.72)); P("AccentSoft", C(sf, 0.15));
        P("HeaderBg", C(Math.Min(sa, 0.7), 0.12)); P("HeaderButtonHover", C(Math.Min(sa, 0.7), 0.2));
        P("CallCardBg", C(sf, 0.11)); P("CallCardBorder", C(sa, 0.28)); P("FocusRing", C(Math.Min(sa, 0.7), 0.72));
        d["DialerBg"] = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0), EndPoint = new Point(0, 1),
            GradientStops = { new GradientStop(C(sa, la), 0), new GradientStop(C(sa, 0.17), 0.55), new GradientStop(Colors.Black, 1) },
        };
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>Barra de título escura/clara conforme o tema (Windows 10 20H1+ / 11).</summary>
    public static void AplicarBarraTitulo(Window janela)
    {
        void Aplicar()
        {
            var hwnd = new WindowInteropHelper(janela).Handle;
            if (hwnd == IntPtr.Zero) return;
            int escuro = IsLight ? 0 : 1;
            DwmSetWindowAttribute(hwnd, 20, ref escuro, sizeof(int));
        }
        janela.SourceInitialized += (_, _) => Aplicar();
        Changed += Aplicar;
        janela.Closed += (_, _) => Changed -= Aplicar;
        Aplicar();
    }
}
