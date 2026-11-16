using System.Text.RegularExpressions;

namespace Softphone.Core;

/// <summary>
/// 1.2.4 (Claude Code, 11/11/2026, pedido do Ana): botões personalizáveis da aba "Atalhos" (como no MicroSIP, só que melhor).
/// Cada atalho tem nome, tipo, valor e cor. A regra de validação mora aqui (e não na tela) para ser testada sem abrir janela.
/// Segurança: "link" só aceita http/https (nunca abre programa nem arquivo); DTMF só aceita dígitos, * e #.
/// </summary>
public sealed class AtalhoTela
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Nome { get; set; } = "";
    /// <summary>ligar | conversa | transferir | dtmf | link | status</summary>
    public string Tipo { get; set; } = AtalhosTela.Ligar;
    public string Valor { get; set; } = "";
    /// <summary>verde | azul | ambar | vermelho | roxo | cinza</summary>
    public string Cor { get; set; } = "verde";
}

public static class AtalhosTela
{
    public const string Ligar = "ligar", Conversa = "conversa", Transferir = "transferir", Dtmf = "dtmf", Link = "link", Status = "status";
    public const int Maximo = 40, MaxNome = 24;

    public static readonly string[] Tipos = [Ligar, Conversa, Transferir, Dtmf, Link, Status];
    public static readonly string[] Cores = ["verde", "azul", "ambar", "vermelho", "roxo", "cinza"];
    public static readonly string[] Estados = ["Disponivel", "Ausente", "NaoPerturbe"];

    private static readonly Regex NumeroOk = new(@"^[0-9+*#() \-]{1,32}$", RegexOptions.Compiled);
    private static readonly Regex DtmfOk = new(@"^[0-9*#]{1,32}$", RegexOptions.Compiled);
    private static readonly Regex LoginOk = new(@"^[A-Za-z0-9._\-]{1,64}$", RegexOptions.Compiled);

    public static string NomeDoTipo(string tipo) => tipo switch
    {
        Ligar => "Ligar", Conversa => "Conversa", Transferir => "Transferir a ligação", Dtmf => "Enviar tons (DTMF)",
        Link => "Abrir link", Status => "Mudar meu status", _ => tipo,
    };

    /// <summary>Texto de erro para mostrar ao usuário, ou null se o atalho está bom.</summary>
    public static string? Validar(AtalhoTela a)
    {
        var nome = (a.Nome ?? "").Trim();
        if (nome.Length == 0) return "Dê um nome ao atalho.";
        if (nome.Length > MaxNome) return $"O nome pode ter até {MaxNome} letras.";
        if (!Tipos.Contains(a.Tipo)) return "Escolha o que o atalho faz.";
        var v = (a.Valor ?? "").Trim();
        switch (a.Tipo)
        {
            case Ligar or Transferir:
                if (v.Length == 0) return "Informe o ramal ou número.";
                if (!NumeroOk.IsMatch(v) || NumeroTelefone.Digitos(v).Length == 0 && !v.Any(c => c is '*' or '#'))
                    return "Use só números, + * # e espaços (até 32 caracteres).";
                break;
            case Dtmf:
                if (!DtmfOk.IsMatch(v)) return "Os tons aceitam só 0-9, * e # (até 32).";
                break;
            case Conversa:
                if (!LoginOk.IsMatch(v)) return "Escolha uma pessoa da empresa (login de rede).";
                break;
            case Link:
                if (!LinkSeguro(v)) return "O link precisa começar com http:// ou https://.";
                break;
            case Status:
                if (!Estados.Contains(v)) return "Escolha o status.";
                break;
        }
        return null;
    }

    public static bool LinkSeguro(string? v) =>
        Uri.TryCreate((v ?? "").Trim(), UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps) && u.Host.Length > 0 && v!.Trim().Length <= 500;

    public static string CorValida(string? cor) => Cores.Contains(cor ?? "") ? cor! : "verde";

    /// <summary>Limpa o que veio do arquivo de configuração: tira inválidos e repetidos, corta no máximo, arruma cor e espaços.</summary>
    public static List<AtalhoTela> Normalizar(IEnumerable<AtalhoTela>? lista)
    {
        var res = new List<AtalhoTela>();
        var ids = new HashSet<string>();
        foreach (var a in lista ?? [])
        {
            if (a == null) continue;
            a.Nome = (a.Nome ?? "").Trim(); a.Valor = (a.Valor ?? "").Trim(); a.Cor = CorValida(a.Cor);
            if (string.IsNullOrWhiteSpace(a.Id) || !ids.Add(a.Id)) { a.Id = Guid.NewGuid().ToString("N"); ids.Add(a.Id); }
            if (Validar(a) != null) continue;
            res.Add(a);
            if (res.Count >= Maximo) break;
        }
        return res;
    }

    /// <summary>Move o atalho <paramref name="delta"/> casas (-1 = para antes). Fica no lugar se já está na ponta.</summary>
    public static bool Mover(List<AtalhoTela> lista, string id, int delta)
    {
        int i = lista.FindIndex(x => x.Id == id);
        int j = i + delta;
        if (i < 0 || j < 0 || j >= lista.Count) return false;
        (lista[i], lista[j]) = (lista[j], lista[i]);
        return true;
    }

    /// <summary>Iniciais para o círculo do cartão (duas letras, ou o número).</summary>
    public static string Iniciais(string nome)
    {
        var p = (nome ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (p.Length == 0) return "?";
        return p.Length == 1 ? p[0][..Math.Min(2, p[0].Length)].ToUpperInvariant() : (p[0][..1] + p[^1][..1]).ToUpperInvariant();
    }
}
