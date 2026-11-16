using System;
using System.IO;
using Softphone.Core;
using Microsoft.Win32;

namespace Softphone.App;

/// <summary>
/// Clicar para ligar: registra o Softphone (só para este usuário, HKCU) como programa capaz de abrir links tel:, sip: e
/// callto:. O Windows não deixa um programa se pôr como padrão sozinho: no primeiro clique ele pergunta com o que abrir.
/// Quando o Softphone já está aberto, a segunda instância (aberta pelo link) deixa o número num arquivo e acorda a primeira.
/// </summary>
internal static class LinksTelefone
{
    private const string ProgId = "Softphone.Link";
    private const string Capacidades = @"Software\Softphone\Capabilities";
    private static string ArquivoPedido => Path.Combine(Armazenamento.Pasta, "pedido-ligar.txt");

    /// <summary>Testes (SOFTPHONE_DADOS) não mexem no registro do usuário de verdade.</summary>
    private static bool ModoTeste => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SOFTPHONE_DADOS"));

    public static void Aplicar(bool ligar)
    {
        if (ModoTeste) return;
        try
        {
            var exe = Environment.ProcessPath;
            if (exe is null || !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return;
            if (ligar) Registrar(exe); else Remover();
        }
        catch (Exception ex) { Log.Escrever("Links de telefone (registro): " + ex.Message); }
    }

    private static void Registrar(string exe)
    {
        using (var cls = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + ProgId))
        {
            cls.SetValue("", "Link de telefone (Softphone)");
            cls.SetValue("URL Protocol", "");
            using (var ic = cls.CreateSubKey("DefaultIcon")) ic.SetValue("", $"\"{exe}\",0");
            using var cmd = cls.CreateSubKey(@"shell\open\command");
            cmd.SetValue("", $"\"{exe}\" --link \"%1\"");
        }
        using (var cap = Registry.CurrentUser.CreateSubKey(Capacidades))
        {
            cap.SetValue("ApplicationName", "Softphone");
            cap.SetValue("ApplicationDescription", "Telefone (ramal do PABX) da empresa");
            using var url = cap.CreateSubKey("URLAssociations");
            foreach (var e in Discagem.Esquemas) url.SetValue(e, ProgId);
        }
        LimparNomeAntigo();
        using (var reg = Registry.CurrentUser.CreateSubKey(@"Software\RegisteredApplications")) reg.SetValue("Softphone", Capacidades);

        // Esquema que ninguém tratava ainda neste PC (nem para todos, nem para este usuário): o Softphone assume direto.
        foreach (var e in Discagem.Esquemas)
        {
            using var existente = Registry.ClassesRoot.OpenSubKey(e);
            if (existente != null && existente.OpenSubKey(@"shell\open\command") != null) continue;
            using var k = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + e);
            k.SetValue("", "URL:" + e);
            k.SetValue("URL Protocol", "");
            k.SetValue("Softphone", "1"); // marca: fomos nós que criamos (para remover depois)
            using var cmd = k.CreateSubKey(@"shell\open\command");
            cmd.SetValue("", $"\"{exe}\" --link \"%1\"");
        }
    }

    /// <summary>Registros feitos antes do renome (Softphone): sai tudo, para não ficarem dois programas na lista de padrões do Windows.</summary>
    private static void LimparNomeAntigo()
    {
        Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\Softphone.Link", false);
        Registry.CurrentUser.DeleteSubKeyTree(@"Software\Softphone", false);
        using (var reg = Registry.CurrentUser.OpenSubKey(@"Software\RegisteredApplications", true)) reg?.DeleteValue("Softphone", false);
    }

    private static void Remover()
    {
        Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\" + ProgId, false);
        Registry.CurrentUser.DeleteSubKeyTree(@"Software\Softphone", false);
        LimparNomeAntigo();
        using (var reg = Registry.CurrentUser.OpenSubKey(@"Software\RegisteredApplications", true)) reg?.DeleteValue("Softphone", false);
        foreach (var e in Discagem.Esquemas)
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Classes\" + e);
            bool nosso = k?.GetValue("Softphone") is string || k?.GetValue("Softphone") is string;
            k?.Dispose();
            if (nosso) Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\" + e, false);
        }
    }

    /// <summary>O link que veio na linha de comando ("--link tel:..." ou o próprio "tel:..."), ou null.</summary>
    public static string? LinkDosArgumentos(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].Equals("--link", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) return args[i + 1];
            if (Discagem.EhLink(args[i])) return args[i];
        }
        return null;
    }

    /// <summary>Segunda instância: deixa o link para a que já está aberta.</summary>
    public static void DeixarPedido(string link)
    {
        try { Directory.CreateDirectory(Armazenamento.Pasta); File.WriteAllText(ArquivoPedido, link); } catch { }
    }

    /// <summary>Instância aberta: pega (e apaga) o link deixado pela segunda instância.</summary>
    public static string? PegarPedido()
    {
        try
        {
            if (!File.Exists(ArquivoPedido)) return null;
            var t = File.ReadAllText(ArquivoPedido).Trim();
            File.Delete(ArquivoPedido);
            return t.Length == 0 ? null : t;
        }
        catch { return null; }
    }
}
