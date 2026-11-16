using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;

namespace Softphone.App;

/// <summary>
/// 1.3 (Claude Code, 10/11/2026): atalhos globais configuráveis por usuário. Cada ação tem um padrão; a pessoa troca na tela de
/// Configurações (salvo em config.json do usuário, "Restaurar padrão" volta ao original). Exige Ctrl ou Alt para não tomar teclas comuns.
/// </summary>
internal static class AtalhosApp
{
    public sealed record Acao(int Id, string Chave, string Nome, string Padrao);

    public static readonly Acao[] Acoes =
    {
        new(1, "atender", "Atender", "Ctrl+Alt+A"),
        new(2, "desligar", "Desligar", "Ctrl+Alt+D"),
        new(3, "mudo", "Mudo", "Ctrl+Alt+M"),
        new(4, "espera", "Espera", "Ctrl+Alt+E"),
        new(5, "transferir", "Transferir", "Ctrl+Alt+T"),
        new(6, "gravar", "Gravar a ligação", "Ctrl+Alt+G"),
        new(7, "discar", "Discagem rápida (abre o discador)", "Ctrl+Alt+N"),
        new(8, "fav1", "Ligar para o favorito 1", "Ctrl+Alt+1"),
        new(9, "fav2", "Ligar para o favorito 2", "Ctrl+Alt+2"),
        new(10, "fav3", "Ligar para o favorito 3", "Ctrl+Alt+3"),
    };

    public static string Efetivo(ConfigApp c, Acao a) =>
        c.Atalhos != null && c.Atalhos.TryGetValue(a.Chave, out var v) && Tentar(v, out _, out _) ? v : a.Padrao;

    /// <summary>Lê "Ctrl+Alt+Shift+Win+X". Precisa de Ctrl ou Alt e de uma tecla.</summary>
    public static bool Tentar(string? texto, out uint mods, out uint vk)
    {
        mods = 0; vk = 0;
        if (string.IsNullOrWhiteSpace(texto)) return false;
        var partes = texto.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (partes.Length < 2) return false;
        foreach (var p in partes[..^1])
        {
            switch (p.ToLowerInvariant())
            {
                case "ctrl": mods |= 0x2; break;
                case "alt": mods |= 0x1; break;
                case "shift": mods |= 0x4; break;
                case "win": mods |= 0x8; break;
                default: return false;
            }
        }
        if ((mods & 0x3) == 0) return false;                    // sem Ctrl nem Alt: não vale (tomaria uma tecla comum)
        if (!Enum.TryParse<Key>(partes[^1], true, out var tecla) || tecla == Key.None) return false;
        vk = (uint)KeyInterop.VirtualKeyFromKey(tecla);
        return vk != 0;
    }

    /// <summary>Texto do atalho a partir da tecla apertada; null se a tecla sozinha ainda não forma um atalho válido.</summary>
    public static string? Formatar(Key tecla, ModifierKeys mods)
    {
        if (tecla is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.System or Key.None) return null;
        if (!mods.HasFlag(ModifierKeys.Control) && !mods.HasFlag(ModifierKeys.Alt)) return null;
        var l = new List<string>();
        if (mods.HasFlag(ModifierKeys.Control)) l.Add("Ctrl");
        if (mods.HasFlag(ModifierKeys.Alt)) l.Add("Alt");
        if (mods.HasFlag(ModifierKeys.Shift)) l.Add("Shift");
        if (mods.HasFlag(ModifierKeys.Windows)) l.Add("Win");
        l.Add(tecla.ToString());
        return string.Join("+", l);
    }

    /// <summary>Atalhos repetidos entre ações (o Windows só deixa um programa ficar com cada combinação).</summary>
    public static string? Repetido(IEnumerable<(string Acao, string Atalho)> lista) =>
        lista.GroupBy(x => x.Atalho, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1) is { } g
            ? $"{g.Key} está em mais de uma ação ({string.Join(", ", g.Select(x => x.Acao))})." : null;
}
