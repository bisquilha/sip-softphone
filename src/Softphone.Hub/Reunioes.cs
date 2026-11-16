using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Softphone.Contratos;
using Microsoft.IdentityModel.Tokens;

namespace Softphone.Hub;

/// <summary>
/// Quem foi chamado para cada sala (1.2.2): numa sala de conversa, quem foi convidado também entra, mesmo sem ser
/// membro da conversa ("Adicionar pessoas" com a reunião aberta). Fica só na memória, por 24 h (o passe dura até isso).
/// </summary>
public sealed class ConvitesReuniao
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (HashSet<string> Logins, DateTime Ate)> _salas = new();

    public void Registrar(string sala, IEnumerable<string> logins)
    {
        var agora = DateTime.UtcNow;
        foreach (var velha in _salas.Where(x => x.Value.Ate < agora).Select(x => x.Key).ToList()) _salas.TryRemove(velha, out _);
        var e = _salas.GetOrAdd(sala, _ => (new HashSet<string>(StringComparer.OrdinalIgnoreCase), agora.AddHours(24)));
        lock (e.Logins) foreach (var l in logins) e.Logins.Add(l);
    }

    public bool Convidado(string sala, string login) =>
        _salas.TryGetValue(sala, out var e) && e.Ate > DateTime.UtcNow && LerComTrava(e.Logins, login);

    private static bool LerComTrava(HashSet<string> logins, string login) { lock (logins) return logins.Contains(login); }

    /// <summary>Sala de conversa ("softphone-c&lt;id&gt;-xxxx"): o id da conversa; avulsa: null.</summary>
    public static long? ConversaDaSala(string sala)
    {
        var partes = sala.Split('-');
        return partes.Length >= 3 && partes[1].StartsWith('c') && long.TryParse(partes[1][1..], out var conv) ? conv : null;
    }
}

/// <summary>
/// Emite o passe (JWT) do Jitsi para quem está logado no Hub. O Jitsi confere a assinatura com o mesmo segredo,
/// então só entra na sala quem recebeu o passe por aqui (ninguém de fora, nem quem só tem o link).
/// </summary>
public sealed class Reunioes
{
    private readonly OpcoesJitsi _op;

    public Reunioes(OpcoesHub op) => _op = op.Jitsi;

    public bool Configurado => !string.IsNullOrWhiteSpace(_op.Url) && _op.Segredo.Length >= 32; // HS256 exige chave de 256 bits ou mais

    /// <summary>Nome de sala novo e difícil de adivinhar (ex.: "softphone-c12-k3f9x2").</summary>
    public static string NovaSala(long? conversa)
    {
        const string letras = "abcdefghjkmnpqrstuvwxyz23456789";
        var b = RandomNumberGenerator.GetBytes(8);
        var sufixo = new string(b.Select(x => letras[x % letras.Length]).ToArray());
        return conversa is long c ? $"softphone-c{c}-{sufixo}" : $"softphone-{sufixo}";
    }

    public static bool SalaValida(string sala) =>
        sala.Length is > 3 and <= 64 && sala.All(ch => ch is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-');

    public ReuniaoDto Passe(string sala, string titulo, string login, string nome, string? email)
    {
        if (!Configurado) throw new InvalidOperationException("Reuniões não configuradas no servidor (Hub:Jitsi).");
        var expira = DateTime.UtcNow.AddHours(Math.Clamp(_op.HorasValidade, 1, 24));
        var usuario = new Dictionary<string, object?> { ["id"] = login, ["name"] = nome, ["email"] = email ?? "" };
        var claims = new List<Claim>
        {
            new("room", sala),
            new("sub", _op.Dominio),
            new("context", JsonSerializer.Serialize(new { user = usuario }), JsonClaimValueTypes.Json),
            new("moderator", "true", ClaimValueTypes.Boolean),
        };
        var chave = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_op.Segredo));
        var jwt = new JwtSecurityToken(issuer: _op.AppId, audience: "jitsi", claims: claims,
            notBefore: DateTime.UtcNow.AddMinutes(-2), expires: expira,
            signingCredentials: new SigningCredentials(chave, SecurityAlgorithms.HmacSha256));
        var token = new JwtSecurityTokenHandler().WriteToken(jwt);
        var baseUrl = _op.Url.TrimEnd('/');
        return new ReuniaoDto(sala, titulo, $"{baseUrl}/{sala}?jwt={token}", $"{baseUrl}/{sala}", expira);
    }
}
