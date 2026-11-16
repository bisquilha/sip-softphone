using System.Collections.Concurrent;
using Softphone.Contratos;

namespace Softphone.Hub;

/// <summary>
/// Conta SIP vinda do AD (Hub 1.2.8/1.2.9, Claude Code, 10/11/2026). A TI grava no AD, por pessoa, o ramal de uso (ipPhone), o
/// ramal de login (msDS-cloudExtensionAttribute2) e a senha do SIP (msDS-cloudExtensionAttribute1); o app da própria pessoa pede ao Hub ao conectar e se configura sozinho. O Hub lê o AD
/// na hora de cada pedido: a senha NÃO vai para o hub.db, nem para cache, nem para log, nem para a lista de pessoas.
/// </summary>
public sealed class ContasSip
{
    private readonly Diretorio _dir;
    private readonly OpcoesHub _op;
    private readonly ConcurrentDictionary<string, (string? Ramal, string? Login, string? Senha)> _simuladas = new(StringComparer.OrdinalIgnoreCase);

    public ContasSip(Diretorio dir, OpcoesHub op) { _dir = dir; _op = op; }

    /// <summary>Ramal de uso, login e senha da pessoa (sempre a do próprio login do token). Falhou = o AD não respondeu (o app mantém o que tem).</summary>
    public (ContaSipAdDto Conta, bool Falhou) Da(string login)
    {
        login = Diretorio.NormalizarLogin(login);
        if (_op.Teste && _simuladas.TryGetValue(login, out var s))
        {
            var (r, l, p, _) = Diretorio.JuntarContaSip(s.Ramal, s.Login, s.Senha);
            return (new ContaSipAdDto(r, p, l), false);
        }
        var (ramal, loginSip, senha, falhou) = _dir.LerContaSip(login);
        return (new ContaSipAdDto(ramal, senha, loginSip), falhou);
    }

    /// <summary>Só com Hub:Teste=true: finge o que o AD tem para esta pessoa (sem nada = tira).</summary>
    public void SimularParaTeste(string login, string? ramal, string? loginSip, string? senha)
    {
        login = Diretorio.NormalizarLogin(login);
        if (string.IsNullOrEmpty(ramal) && string.IsNullOrEmpty(loginSip) && string.IsNullOrEmpty(senha)) _simuladas.TryRemove(login, out _);
        else _simuladas[login] = (ramal, loginSip, senha);
    }
}
