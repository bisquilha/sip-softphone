using System.Globalization;
using System.Text;

namespace Softphone.Core;

/// <summary>
/// 1.3 (Claude Code, 10/11/2026): busca da aba Recentes e do histórico completo. Ignora acentos e maiúsculas, aceita número com ou
/// sem formatação (+55, parênteses, traços), preserva zeros à esquerda de ramal ("2001" não casa com "21" no começo por engano:
/// o texto digitado é comparado como sequência de dígitos contida no número) e entende data ("10/11", "10/11/2026", "hoje", "ontem").
/// Todos os termos digitados precisam casar (E), em qualquer campo.
/// </summary>
public static class BuscaHistorico
{
    public static string SemAcento(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var d = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(d.Length);
        foreach (var ch in d) if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark) sb.Append(ch);
        return sb.ToString().ToLowerInvariant();
    }

    public static string Digitos(string? s) => string.IsNullOrEmpty(s) ? "" : new string(s.Where(char.IsDigit).ToArray());

    /// <summary>true se a linha casa com todos os termos de <paramref name="consulta"/>. Consulta vazia casa com tudo.</summary>
    public static bool Combina(string? nome, string numero, string? setor, DateTime data, string? consulta, DateTime? hoje = null)
    {
        if (string.IsNullOrWhiteSpace(consulta)) return true;
        var dia = (hoje ?? DateTime.Today).Date;
        var textos = SemAcento((nome ?? "") + " " + (setor ?? "") + " " + numero);
        var numeroDigitos = Digitos(numero);
        var datas = new[] { data.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture), data.ToString("dd/MM", CultureInfo.InvariantCulture), data.ToString("HH:mm", CultureInfo.InvariantCulture) };
        foreach (var termo in consulta.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var t = SemAcento(termo);
            bool ok = textos.Contains(t, StringComparison.Ordinal)
                || (t == "hoje" && data.Date == dia)
                || (t == "ontem" && data.Date == dia.AddDays(-1))
                || datas.Any(x => x.StartsWith(t, StringComparison.Ordinal) || x.Contains(t, StringComparison.Ordinal) && t.Contains('/'));
            // número digitado com formatação ("(11) 3333-4444", "+55 11 ..."): compara só os dígitos
            if (!ok)
            {
                var dig = Digitos(termo);
                if (dig.Length >= 2 && !termo.Any(char.IsLetter) && numeroDigitos.Contains(dig, StringComparison.Ordinal)) ok = true;
            }
            if (!ok) return false;
        }
        return true;
    }
}
