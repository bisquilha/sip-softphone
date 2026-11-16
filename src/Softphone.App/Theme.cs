using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
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

    private static string? Conhecido(string? id) => Disponiveis.FirstOrDefault(t => t.Id != "Sistema" && string.Equals(t.Id, id?.Trim(), StringComparison.OrdinalIgnoreCase))?.Id;

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
        var uri = new Uri($"pack://application:,,,/Themes/{arquivo}.xaml", UriKind.Absolute);
        if (dictionaries.Count > 0) dictionaries[0] = new ResourceDictionary { Source = uri };
        else dictionaries.Add(new ResourceDictionary { Source = uri });
        Atual = id;
        IsLight = id == "Claro";
        Changed?.Invoke();
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
