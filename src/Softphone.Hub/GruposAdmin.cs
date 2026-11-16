using System.Collections.Concurrent;
using Softphone.Contratos;

namespace Softphone.Hub;

/// <summary>
/// Administradores por grupo do AD (Hub 1.2.3, Claude Code, 05/11/2026). A TI coloca no painel web os grupos que quiser;
/// quem é membro (inclusive por grupo dentro de grupo) vira administrador: vê o escudo no app e entra no painel web.
/// Os membros são lidos do AD a cada sincronização do diretório e quando a lista muda. Se a leitura de um grupo falhar,
/// ficam valendo os membros da última leitura boa (uma falha do AD nunca tira o acesso de todo mundo).
/// </summary>
public sealed class GruposAdmin
{
    private readonly Diretorio _dir;
    private readonly OpcoesHub _op;
    private readonly ILogger<GruposAdmin> _log;
    private readonly object _trava = new();
    private readonly Dictionary<string, HashSet<string>> _ultimosBons = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string[]> _simulados = new(StringComparer.OrdinalIgnoreCase);
    private GrupoAdminDto[] _situacao = [];

    public GruposAdmin(Diretorio dir, OpcoesHub op, ILogger<GruposAdmin> log) { _dir = dir; _op = op; _log = log; }

    /// <summary>Situação de cada grupo da lista (membros lidos ou erro), para o painel.</summary>
    public GrupoAdminDto[] Situacao { get { lock (_trava) return _situacao; } }

    /// <summary>Lê os membros de um grupo (no modo de teste, os simulados).</summary>
    public (List<string>? Logins, string? Erro) Ler(string grupo)
    {
        if (_op.Teste && _simulados.TryGetValue(grupo, out var s)) return (s.ToList(), null);
        if (_op.Teste && !_dir.Configurado) return (null, "grupo não encontrado no AD");
        return _dir.MembrosDoGrupo(grupo);
    }

    /// <summary>1.2.4: pesquisa de grupos para o painel (no modo de teste sem AD, os simulados).</summary>
    public (List<GrupoAdDto>? Grupos, string? Erro) Buscar(string? busca)
    {
        if (_op.Teste && !_dir.Configurado)
        {
            var t = (busca ?? "").Trim();
            return (_simulados.Keys.Where(k => t.Length == 0 || k.Contains(t, StringComparison.OrdinalIgnoreCase))
                .OrderBy(k => k, StringComparer.OrdinalIgnoreCase).Select(k => new GrupoAdDto(k, "grupo simulado (teste)")).ToList(), null);
        }
        return _dir.BuscarGrupos(busca);
    }

    /// <summary>Relê todos os grupos da lista e atualiza quem é administrador por grupo.</summary>
    public void Atualizar()
    {
        var grupos = _op.GruposAdmin;
        var situacao = new List<GrupoAdminDto>();
        var membros = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in grupos)
        {
            var (logins, erro) = Ler(g);
            int qtd = 0;
            lock (_trava)
            {
                if (logins != null) _ultimosBons[g] = new HashSet<string>(logins, StringComparer.OrdinalIgnoreCase);
                if (_ultimosBons.TryGetValue(g, out var bons)) { membros.UnionWith(bons); qtd = bons.Count; }
            }
            situacao.Add(new GrupoAdminDto(g, qtd, erro));
        }
        lock (_trava)
        {
            foreach (var velho in _ultimosBons.Keys.Where(k => !grupos.Contains(k, StringComparer.OrdinalIgnoreCase)).ToList()) _ultimosBons.Remove(velho);
            _situacao = situacao.ToArray();
        }
        _op.AdminsDeGrupos = membros.ToArray();
        if (grupos.Length > 0) _log.LogInformation("Administradores por grupo do AD: {Membros} pessoa(s) em {Grupos} grupo(s)", membros.Count, grupos.Length);
    }

    /// <summary>Só com Hub:Teste=true: finge os membros de um grupo.</summary>
    public void SimularParaTeste(string grupo, string[] logins) =>
        _simulados[grupo] = logins.Select(Diretorio.NormalizarLogin).Where(l => l.Length > 0).ToArray();
}
