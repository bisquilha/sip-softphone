namespace Softphone.Core;

/// <summary>
/// 1.3: normaliza número de telefone brasileiro para identificar quem está ligando: tira "+55"/"0055", o "0" de tronco, o código de
/// operadora ("0 15 11 9...") e deixa DDD + número. Ramal (até 5 dígitos) NUNCA é mexido: "1001" continua "1001" e não casa com "258".
/// </summary>
public static class NumeroTelefone
{
    public static string Digitos(string? s) => string.IsNullOrEmpty(s) ? "" : new string(s.Where(char.IsDigit).ToArray());

    public static bool EhRamal(string? s) { var d = Digitos(s); return d.Length is > 0 and <= 5; }

    /// <summary>DDD + número (10 ou 11 dígitos) quando der; senão os dígitos como vieram.</summary>
    public static string Nacional(string? s)
    {
        var d = Digitos(s);
        if (d.Length <= 5) return d;                                  // ramal: preserva zeros à esquerda
        if (d.StartsWith("00") && d.Length > 4) d = d[2..];           // prefixo internacional
        if (d.StartsWith("55") && d.Length is 12 or 13) d = d[2..];   // país
        if (d.StartsWith('0') && d.Length > 6)
        {
            d = d[1..];                                               // "0" de tronco
            if (d.Length is 12 or 13) d = d[2..];                     // código da operadora (015, 021, 031...)
        }
        return d;
    }

    /// <summary>Mesmo telefone? Ramais só casam com o mesmo ramal; números longos casam pelo nacional (ou, sem DDD, pelos últimos 8 dígitos).</summary>
    public static bool Igual(string? a, string? b)
    {
        var da = Digitos(a); var db = Digitos(b);
        if (da.Length == 0 || db.Length == 0) return false;
        if (da.Length <= 5 || db.Length <= 5) return da == db;
        var na = Nacional(a); var nb = Nacional(b);
        if (na == nb) return true;
        // um dos dois sem DDD: compara os últimos 8 dígitos (o 9 extra do celular fica de fora)
        if ((na.Length is 8 or 9) != (nb.Length is 8 or 9)) return na.Length >= 8 && nb.Length >= 8 && na[^8..] == nb[^8..];
        return false;
    }
}
