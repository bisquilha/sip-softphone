namespace Softphone.Core;

/// <summary>
/// 1.3: decide se uma fala do interlocutor é uma pergunta (a transcrição quase nunca traz "?", então olha também como a frase começa).
/// É só o gatilho da busca na base: errar para mais custa uma busca inútil; errar para menos o atendente ainda digita a pergunta.
/// </summary>
public static class DeteccaoPergunta
{
    private static readonly string[] Inicio =
    {
        "qual", "quais", "quanto", "quantos", "quantas", "como", "quando", "onde", "quem", "por que", "porque", "o que", "que dia", "que horas",
        "voces", "tem ", "tem?", "temos", "fazem", "faz ", "aceitam", "vendem", "entregam", "trabalham", "atendem", "existe", "da pra", "da para",
        "e qual", "e quanto", "e quantos", "e como", "e quando", "e onde", "e voces", "e tem", "e fazem", "pode ", "podem ", "consegue", "conseguem",
        "voce faz", "voce tem", "queria saber", "gostaria de saber", "preciso saber",
    };

    public static bool Parece(string? texto)
    {
        if (string.IsNullOrWhiteSpace(texto)) return false;
        var t = texto.Trim();
        if (t.Contains('?')) return true;
        var n = BuscaHistorico.SemAcento(t).TrimStart(' ', '.', ',', '-');
        if (n.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length < 3) return false;   // "oi", "sim", "ok": não é pergunta
        return Inicio.Any(p => n.StartsWith(p, StringComparison.Ordinal));
    }
}
