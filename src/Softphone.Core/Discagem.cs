namespace Softphone.Core;

/// <summary>Converte links de telefone (tel:, sip:, callto:) no número que o PABX entende.</summary>
public static class Discagem
{
    public static readonly string[] Esquemas = { "tel", "sip", "sips", "callto" };

    /// <summary>É um link que o Softphone abre?</summary>
    public static bool EhLink(string? texto)
    {
        if (string.IsNullOrWhiteSpace(texto)) return false;
        var t = texto.Trim().Trim('"');
        return Esquemas.Any(e => t.StartsWith(e + ":", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// "tel:+55 (11) 3333-4444" -> "1133334444" (sem o +55; com <paramref name="prefixoExterno"/> na frente, se houver).
    /// "sip:1001@pabx" -> "1001". Ramal (até 5 dígitos) passa direto. Outro país: "+1 202..." -> "001202...".
    /// Devolve "" se não sobrar nada discável.
    /// </summary>
    public static string NumeroDoLink(string link, string? prefixoExterno = null)
    {
        var t = (link ?? "").Trim().Trim('"');
        try { t = Uri.UnescapeDataString(t); } catch { }
        foreach (var e in Esquemas)
            if (t.StartsWith(e + ":", StringComparison.OrdinalIgnoreCase)) { t = t[(e.Length + 1)..]; break; }
        t = t.TrimStart('/');
        int corte = t.IndexOfAny(new[] { ';', '?' });
        if (corte >= 0) t = t[..corte];
        int arroba = t.IndexOf('@');
        if (arroba >= 0) t = t[..arroba];
        if (t.Any(char.IsLetter) && !t.Any(char.IsDigit)) return ""; // ex.: sip:fulano@empresa (sem número)

        bool internacional = t.TrimStart().StartsWith('+');
        var n = new string(t.Where(ch => char.IsDigit(ch) || ch is '*' or '#').ToArray());
        if (n.Length == 0) return "";
        if (internacional)
        {
            if (n.StartsWith("55") && n.Length >= 12) n = n[2..];      // Brasil: fica DDD + número
            else return "00" + n;                                       // outro país
        }
        else if (n.StartsWith("55") && n.Length is 12 or 13) n = n[2..]; // "55 11 9xxxx-xxxx" sem o +

        bool externo = n.Length >= 8 && !n.StartsWith('*') && !n.StartsWith('#');
        var p = (prefixoExterno ?? "").Trim();
        if (externo && p.Length > 0 && !n.StartsWith(p)) n = p + n;
        return n;
    }
}
