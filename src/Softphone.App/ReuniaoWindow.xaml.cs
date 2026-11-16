using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Softphone.Contratos;
using Softphone.Core;
using Microsoft.Web.WebView2.Core;

namespace Softphone.App;

/// <summary>
/// Reunião / chamada de vídeo (Jitsi) dentro do Softphone. Usa o WebView2 do Windows (assinado pela Microsoft), que libera
/// câmera, microfone e tela só para o endereço de reuniões da empresa.
/// </summary>
public partial class ReuniaoWindow : Window
{
    private readonly ReuniaoDto _reuniao;
    private readonly string _host;
    private bool _saiu;

    /// <summary>Janela fechada (para a presença voltar ao normal).</summary>
    public static event Action? AlgumaMudou;
    public static int Abertas { get; private set; }

    /// <summary>Quem já foi chamado (não aparece de novo em "Adicionar pessoas").</summary>
    private readonly HashSet<string> _chamados;

    public ReuniaoWindow(ReuniaoDto reuniao, IEnumerable<string>? chamados = null)
    {
        InitializeComponent();
        Theme.AplicarBarraTitulo(this);
        _reuniao = reuniao;
        _chamados = new HashSet<string>(chamados ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        _host = new Uri(reuniao.UrlConvite).Host;
        Title = $"{reuniao.Titulo} - Softphone";
        TxtTitulo.Text = reuniao.Titulo;
        Loaded += async (_, _) => await AbrirAsync();
        Closing += async (_, e) =>
        {
            if (_saiu) return;
            // sai da sala antes de fechar (para não ficar "fantasma" para os outros)
            e.Cancel = true; _saiu = true;
            try { await Navegador.ExecuteScriptAsync("try{APP.conference.hangup&&APP.conference.hangup(false)}catch(e){}"); await Task.Delay(300); } catch { }
            Close();
        };
        Closed += (_, _) => { Abertas--; AlgumaMudou?.Invoke(); try { Navegador.Dispose(); } catch { } };
        Abertas++;
        AlgumaMudou?.Invoke();
    }

    /// <summary>1.1.3: chamar mais gente com a reunião aberta (toca no Softphone de quem está online), como no Teams.</summary>
    private async void Adicionar_Click(object sender, RoutedEventArgs e)
    {
        var chat = App.Atual.Chat;
        if (chat.Cliente is not { Estado: EstadoHub.Conectado } cli)
        {
            MessageBox.Show(this, "Sem conexão com o servidor do chat agora. Tente de novo em instantes.", "Softphone", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var j = new EscolherPessoasWindow(chat.Pessoas, chat.EuLogin, adicionar: true, _chamados) { Owner = this };
        if (j.ShowDialog() != true) return;
        var pessoas = j.Escolhidas;
        try
        {
            var offline = new HashSet<string>(await cli.ConvidarParaReuniaoAsync(_reuniao.Sala, _reuniao.Titulo, pessoas.Select(p => p.Login).ToArray()),
                StringComparer.OrdinalIgnoreCase);
            foreach (var p in pessoas) _chamados.Add(p.Login);
            var nomesOff = pessoas.Where(p => offline.Contains(p.Login)).Select(p => p.Nome).ToList();
            var chamando = pessoas.Where(p => !offline.Contains(p.Login)).Select(p => p.Nome).ToList();
            App.Atual.Notificar("Reunião",
                (chamando.Count > 0 ? $"Chamando {string.Join(", ", chamando)}." : "") +
                (nomesOff.Count > 0 ? $" Offline (não tocou): {string.Join(", ", nomesOff)}. Use \"Copiar link\" para mandar." : ""));
        }
        catch (Exception ex)
        {
            var msg = ex.Message.Contains("does not exist", StringComparison.OrdinalIgnoreCase)
                ? "O servidor do chat ainda não tem essa função (precisa do Hub 1.2.2)."
                : "Não deu para chamar: " + ex.Message;
            MessageBox.Show(this, msg, "Softphone", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void CopiarLink_Click(object sender, RoutedEventArgs e)
    {
        var link = App.Atual.Chat.Cliente?.LinkReuniao(_reuniao.Sala) ?? _reuniao.UrlConvite;
        try { Clipboard.SetText(link); App.Atual.Notificar("Reunião", "Link copiado. Abre no navegador de qualquer PC da empresa."); }
        catch (Exception ex) { MessageBox.Show(this, "Não deu para copiar: " + ex.Message, "Softphone"); }
    }

    private async Task AbrirAsync()
    {
        try
        {
            // dados do navegador por usuário (Program Files não é gravável)
            var pasta = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Softphone", "WebView2");
            var amb = await CoreWebView2Environment.CreateAsync(null, pasta);
            await Navegador.EnsureCoreWebView2Async(amb);
            var core = Navegador.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.PermissionRequested += (_, e) =>
            {
                // só o endereço de reuniões da empresa ganha câmera/microfone/tela/notificação
                bool nosso = Uri.TryCreate(e.Uri, UriKind.Absolute, out var u) && string.Equals(u.Host, _host, StringComparison.OrdinalIgnoreCase);
                e.State = nosso && e.PermissionKind is CoreWebView2PermissionKind.Camera or CoreWebView2PermissionKind.Microphone
                                     or CoreWebView2PermissionKind.Notifications or CoreWebView2PermissionKind.Autoplay
                    ? CoreWebView2PermissionState.Allow
                    : CoreWebView2PermissionState.Deny;
            };
            // links externos (ex.: ajuda) abrem no navegador padrão, não aqui dentro
            core.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var u) && u.Scheme == "https")
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(u.ToString()) { UseShellExecute = true });
            };
            core.NavigationStarting += (_, e) =>
            {
                if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var u) || !string.Equals(u.Host, _host, StringComparison.OrdinalIgnoreCase)) e.Cancel = true;
            };
            core.NavigationCompleted += (_, e) =>
            {
                if (e.IsSuccess) Carregando.Visibility = Visibility.Collapsed;
                else TxtCarregando.Text = "Não foi possível abrir a reunião. O servidor de reuniões está no ar? (" + e.WebErrorStatus + ")";
            };
            // fechou a reunião pelo botão vermelho do Jitsi: fecha a janela também
            core.SourceChanged += (_, _) =>
            {
                var src = core.Source ?? "";
                if (src.Contains("/static/close", StringComparison.OrdinalIgnoreCase) || src.EndsWith("/close.html", StringComparison.OrdinalIgnoreCase))
                    Dispatcher.BeginInvoke(() => { _saiu = true; Close(); });
            };

            var cfg = App.Atual.Config;
            var ajustes = string.Join("&", new[]
            {
                "config.prejoinConfig.enabled=false",            // entra direto (como o Teams)
                "config.disableDeepLinking=true",                // não oferece o app do Jitsi
                "config.defaultLanguage=%22ptBR%22",
                "config.enableWelcomePage=false",
                "config.enableClosePage=true",
                "interfaceConfig.SHOW_JITSI_WATERMARK=false",
                "interfaceConfig.MOBILE_APP_PROMO=false",
            });
            core.Navigate(_reuniao.Url + "#" + ajustes);
        }
        catch (WebView2RuntimeNotFoundException)
        {
            TxtCarregando.Text = "Falta o componente WebView2 do Windows neste computador. Peça para a TI instalar o \"Microsoft Edge WebView2 Runtime\".";
        }
        catch (Exception ex)
        {
            TxtCarregando.Text = "Erro ao abrir a reunião: " + ex.Message;
            Log.Escrever("Reunião: " + ex);
        }
    }
}
