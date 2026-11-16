using System.DirectoryServices.Protocols;
using System.Net;
using Softphone.Contratos;

namespace Softphone.Hub;

public sealed class OpcoesHub
{
    /// <summary>Pasta de dados (banco, arquivos, chave do token). No Docker: /dados.</summary>
    public string Dados { get; set; } = "dados";
    public OpcoesLdap Ldap { get; set; } = new();
    /// <summary>SÓ para desenvolvimento/testes automáticos: libera POST /api/entrar-teste (login sem senha).</summary>
    public bool Teste { get; set; }
    /// <summary>Tamanho máximo de arquivo no chat, em MB.</summary>
    public int ArquivoMaxMb { get; set; } = 25;
    /// <summary>Validade do token do app, em dias (o app renova sozinho ao conectar).</summary>
    public int TokenDias { get; set; } = 30;
    public OpcoesJitsi Jitsi { get; set; } = new();
    public OpcoesIa Ia { get; set; } = new();
    /// <summary>Logins da TI que podem usar /api/admin/* (ex.: exportar os ramais aprendidos para gravar no AD).</summary>
    public string[] Admins { get; set; } = [];
    /// <summary>Administradores acrescentados pelo painel do app (tabela config; os de Admins continuam fixos).</summary>
    public string[] AdminsExtras { get; set; } = [];
    /// <summary>1.2.3: grupos do AD cujos membros são administradores (gravados pelo painel web).</summary>
    public string[] GruposAdmin { get; set; } = [];
    /// <summary>1.2.3: membros desses grupos (inclusive por grupo dentro de grupo), lidos do AD a cada sincronização.</summary>
    public string[] AdminsDeGrupos { get; set; } = [];
    public bool EhAdmin(string? login) => login != null &&
        Admins.Concat(AdminsExtras).Concat(AdminsDeGrupos).Any(a => string.Equals(a.Trim(), login, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Pessoa lida do AD.</summary>
public sealed record PessoaAd(string Login, string Nome, string? Setor, string? Unidade, string? Email, string? Ramal);

/// <summary>Reuniões por vídeo (Jitsi Meet com autenticação JWT: só entra quem o Hub autorizar).</summary>
public sealed class OpcoesJitsi
{
    /// <summary>Endereço público do Jitsi (vazio = reuniões desligadas).</summary>
    public string Url { get; set; } = "";
    /// <summary>JWT_APP_ID do Jitsi (emissor do token).</summary>
    public string AppId { get; set; } = "softphone";
    /// <summary>JWT_APP_SECRET do Jitsi (o mesmo valor nos dois lados).</summary>
    public string Segredo { get; set; } = "";
    /// <summary>XMPP_DOMAIN do Jitsi (claim "sub").</summary>
    public string Dominio { get; set; } = "meet.jitsi";
    public int HorasValidade { get; set; } = 12;
}

public sealed class OpcoesLdap
{
    /// <summary>Controlador de domínio (vazio = sem AD; só para testes).</summary>
    public string Servidor { get; set; } = "";
    public int Porta { get; set; } = 636;
    public bool Ssl { get; set; } = true;
    public string BaseDn { get; set; } = "DC=Homelab,DC=local";
    /// <summary>NetBIOS do domínio, para montar DOMINIO\usuario no login com senha.</summary>
    public string Dominio { get; set; } = "CORP";
    /// <summary>Conta de serviço só de leitura (ex.: svc-softphone@corp.example.com). Vazio = credenciais do processo (Windows).</summary>
    public string Usuario { get; set; } = "";
    public string Senha { get; set; } = "";
    /// <summary>Quem aparece no diretório: pessoas ativas com nome e sobrenome (tira contas de serviço/admin).</summary>
    public string Filtro { get; set; } =
        "(&(objectCategory=person)(objectClass=user)(!(userAccountControl:1.2.840.113556.1.4.803:=2))(displayName=*)(sn=*))";
    public int SincronizarMinutos { get; set; } = 30;
    /// <summary>Atributo do AD com o ramal (ipPhone é o padrão do Windows para isso). Vazio = não lê ramal do AD.</summary>
    public string AtributoRamal { get; set; } = "ipPhone";
    /// <summary>1.2.8: atributo do AD com a senha do SIP da pessoa (ex.: msDS-cloudExtensionAttribute1). Vazio (padrão) = o Hub não
    /// entrega conta SIP aos apps. ATENÇÃO antes de ligar: atributos comuns do AD são legíveis por qualquer usuário autenticado;
    /// marque o atributo como confidencial (searchFlags, bit 0x80) e restrinja a leitura à conta de serviço do Hub.</summary>
    public string AtributoSenhaSip { get; set; } = "";
    /// <summary>1.2.9: atributo do AD com o ramal de LOGIN do SIP (o que registra no PABX), quando é diferente do ramal de uso (ipPhone).
    /// Vazio no AD de uma pessoa = o login é o próprio ramal. Vazio aqui = o Hub só lê o ipPhone.</summary>
    public string AtributoLoginSip { get; set; } = "msDS-cloudExtensionAttribute2";
}

public enum EstadoContaAd { Ativa, Desativada, NaoExiste, Desconhecido }

/// <summary>
/// Quem pode usar o Hub com um token já emitido (Claude Code, 05/11/2026, achado R07 da revisão do Codex): o token vale
/// 30 dias, então a conta é conferida no AD (com cache) a cada pedido/conexão. Barra só o que o AD confirma: conta
/// desativada, ou conta que não existe mais no AD e que a sincronização também não viu. Se o AD não responder, deixa
/// passar (uma falha de consulta nunca tranca todo mundo para fora).
/// </summary>
public sealed class AcessoContas
{
    private readonly Diretorio _dir;
    private readonly Banco _banco;
    private readonly OpcoesHub _op;
    private readonly ILogger<AcessoContas> _log;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (bool Permitido, DateTime Ate)> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, EstadoContaAd> _simuladas = new(StringComparer.OrdinalIgnoreCase);

    public AcessoContas(Diretorio dir, Banco banco, OpcoesHub op, ILogger<AcessoContas> log) { _dir = dir; _banco = banco; _op = op; _log = log; }

    public bool Permitido(string login)
    {
        login = Diretorio.NormalizarLogin(login);
        if (_cache.TryGetValue(login, out var c) && c.Ate > DateTime.UtcNow) return c.Permitido;
        var estado = _op.Teste && _simuladas.TryGetValue(login, out var s) ? s : _dir.EstadoConta(login);
        bool ok = estado switch
        {
            EstadoContaAd.Desativada => false,
            EstadoContaAd.NaoExiste => _banco.PessoaAtiva(login),
            _ => true,
        };
        if (!ok) _log.LogWarning("Acesso recusado para {Login}: conta {Estado} no AD", login, estado);
        _cache[login] = (ok, DateTime.UtcNow.Add(estado == EstadoContaAd.Desconhecido ? TimeSpan.FromMinutes(1) : TimeSpan.FromMinutes(10)));
        return ok;
    }

    /// <summary>Só com Hub:Teste=true: finge o estado da conta no AD.</summary>
    public void SimularParaTeste(string login, EstadoContaAd estado)
    {
        login = Diretorio.NormalizarLogin(login);
        _simuladas[login] = estado;
        _cache.TryRemove(login, out _);
    }
}

/// <summary>Leitura do AD por LDAP(S): sincroniza o diretório e valida login com senha (reserva quando o Kerberos não estiver disponível).</summary>
public sealed class Diretorio
{
    private readonly OpcoesLdap _op;
    private readonly ILogger<Diretorio> _log;

    public Diretorio(OpcoesHub op, ILogger<Diretorio> log) { _op = op.Ldap; _log = log; }

    public bool Configurado => !string.IsNullOrWhiteSpace(_op.Servidor);

    private LdapConnection Conectar(NetworkCredential? cred)
    {
        var id = new LdapDirectoryIdentifier(_op.Servidor, _op.Porta);
        var c = cred is null ? new LdapConnection(id) : new LdapConnection(id, cred);
        c.SessionOptions.ProtocolVersion = 3;
        c.SessionOptions.ReferralChasing = ReferralChasingOptions.None;
        if (_op.Ssl) c.SessionOptions.SecureSocketLayer = true;
        c.AuthType = cred is null ? AuthType.Negotiate : AuthType.Basic; // Basic só dentro do LDAPS (cifrado)
        c.Timeout = TimeSpan.FromSeconds(30);
        return c;
    }

    private NetworkCredential? CredencialServico() =>
        string.IsNullOrWhiteSpace(_op.Usuario) ? null : new NetworkCredential(_op.Usuario, _op.Senha);

    /// <summary>Só aceita como ramal algo curto, de dígitos (o AD às vezes tem telefone completo, "R: 258" etc.).</summary>
    public static string? LimparRamal(string? v)
    {
        if (string.IsNullOrWhiteSpace(v)) return null;
        v = v.Trim();
        return v.Length is >= 2 and <= 8 && v.All(char.IsAsciiDigit) ? v : null;
    }

    public List<PessoaAd> LerPessoas()
    {
        using var c = Conectar(CredencialServico());
        c.Bind();
        var lista = new List<PessoaAd>();
        var atributos = new List<string> { "sAMAccountName", "displayName", "department", "physicalDeliveryOfficeName", "mail" };
        var attrRamal = _op.AtributoRamal?.Trim();
        if (!string.IsNullOrEmpty(attrRamal)) atributos.Add(attrRamal);
        var req = new SearchRequest(_op.BaseDn, _op.Filtro, SearchScope.Subtree, atributos.ToArray());
        var pag = new PageResultRequestControl(500);
        req.Controls.Add(pag);
        while (true)
        {
            var resp = (SearchResponse)c.SendRequest(req);
            foreach (SearchResultEntry e in resp.Entries)
            {
                string? A(string n) => e.Attributes.Contains(n) && e.Attributes[n].Count > 0 ? e.Attributes[n][0]?.ToString()?.Trim() : null;
                var login = A("sAMAccountName");
                var nome = A("displayName");
                if (string.IsNullOrWhiteSpace(login) || string.IsNullOrWhiteSpace(nome)) continue;
                lista.Add(new(login.ToLowerInvariant(), nome, A("department"), A("physicalDeliveryOfficeName"), A("mail"),
                    string.IsNullOrEmpty(attrRamal) ? null : LimparRamal(A(attrRamal))));
            }
            var cookie = resp.Controls.OfType<PageResultResponseControl>().FirstOrDefault()?.Cookie;
            if (cookie is null || cookie.Length == 0) break;
            pag.Cookie = cookie;
        }
        return lista;
    }

    /// <summary>Confere usuário/senha no AD (bind como o próprio usuário, por LDAPS). Retorna o login normalizado ou null.</summary>
    public string? ValidarSenha(string login, string senha)
    {
        if (!Configurado || string.IsNullOrWhiteSpace(login) || string.IsNullOrEmpty(senha)) return null;
        login = NormalizarLogin(login);
        try
        {
            using var c = Conectar(new NetworkCredential($"{_op.Dominio}\\{login}", senha));
            c.Bind();
            return login;
        }
        catch (LdapException ex)
        {
            _log.LogInformation("Login com senha recusado para {Login}: {Msg}", login, ex.Message);
            return null;
        }
    }

    /// <summary>Estado desta conta no AD agora, buscando só por ela (sem o filtro da sincronização, que também deixa de
    /// fora contas sem nome/sobrenome). Desconhecido = LDAP não configurado ou consulta com erro.</summary>
    public EstadoContaAd EstadoConta(string login)
    {
        if (!Configurado || string.IsNullOrWhiteSpace(login)) return EstadoContaAd.Desconhecido;
        try
        {
            using var c = Conectar(CredencialServico());
            c.Bind();
            var filtro = $"(&(objectCategory=person)(objectClass=user)(sAMAccountName={EscaparFiltro(NormalizarLogin(login))}))";
            var resp = (SearchResponse)c.SendRequest(new SearchRequest(_op.BaseDn, filtro, SearchScope.Subtree, "userAccountControl"));
            if (resp.Entries.Count == 0) return EstadoContaAd.NaoExiste;
            var e = resp.Entries[0];
            if (!e.Attributes.Contains("userAccountControl") || !int.TryParse(e.Attributes["userAccountControl"][0]?.ToString(), out var uac))
                return EstadoContaAd.Desconhecido;
            return (uac & 2) != 0 ? EstadoContaAd.Desativada : EstadoContaAd.Ativa; // bit 2 = ACCOUNTDISABLE
        }
        catch (Exception ex)
        {
            _log.LogWarning("Não deu para consultar a conta {Login} no AD: {Msg}", login, ex.Message);
            return EstadoContaAd.Desconhecido;
        }
    }

    /// <summary>
    /// Membros de um grupo do AD pelo nome (sAMAccountName ou cn), inclusive de grupos dentro do grupo
    /// (LDAP_MATCHING_RULE_IN_CHAIN). Logins em minúsculas. Erro: grupo não encontrado ou falha na consulta.
    /// </summary>
    public (List<string>? Logins, string? Erro) MembrosDoGrupo(string grupo)
    {
        if (!Configurado) return (null, "o AD não está configurado no servidor");
        try
        {
            using var c = Conectar(CredencialServico());
            c.Bind();
            var nome = EscaparFiltro(grupo.Trim());
            var achou = (SearchResponse)c.SendRequest(new SearchRequest(_op.BaseDn,
                $"(&(objectCategory=group)(|(sAMAccountName={nome})(cn={nome})))", SearchScope.Subtree, "distinguishedName"));
            if (achou.Entries.Count == 0) return (null, "grupo não encontrado no AD");
            var dn = achou.Entries[0].DistinguishedName;
            var req = new SearchRequest(_op.BaseDn,
                $"(&(objectCategory=person)(objectClass=user)(memberOf:1.2.840.113556.1.4.1941:={EscaparFiltro(dn)}))", SearchScope.Subtree, "sAMAccountName");
            var pag = new PageResultRequestControl(500);
            req.Controls.Add(pag);
            var logins = new List<string>();
            while (true)
            {
                var resp = (SearchResponse)c.SendRequest(req);
                foreach (SearchResultEntry e in resp.Entries)
                    if (e.Attributes.Contains("sAMAccountName") && e.Attributes["sAMAccountName"][0]?.ToString() is { Length: > 0 } l)
                        logins.Add(l.ToLowerInvariant());
                var cookie = resp.Controls.OfType<PageResultResponseControl>().FirstOrDefault()?.Cookie;
                if (cookie is null || cookie.Length == 0) break;
                pag.Cookie = cookie;
            }
            return (logins, null);
        }
        catch (Exception ex)
        {
            _log.LogWarning("Não deu para ler o grupo {Grupo} no AD: {Msg}", grupo, ex.Message);
            return (null, "erro ao consultar o AD: " + ex.Message);
        }
    }

    /// <summary>
    /// 1.2.8/1.2.9: ramal de uso (ipPhone), ramal de login e senha do SIP de UMA pessoa, lidos do AD na hora do pedido. Nada é guardado.
    /// Regras: ramal e login precisam ser número curto (LimparRamal); sem login no AD, o login é o ramal; sem ramal, vale só o login;
    /// senha vazia ou com mais de 128 caracteres, ou nem ramal nem login válidos, contam como "sem dados". Falha = <c>Falhou</c>.
    /// </summary>
    public (string? Ramal, string? Login, string? Senha, bool Falhou) LerContaSip(string login)
    {
        var attrRamal = _op.AtributoRamal?.Trim();
        var attrLogin = _op.AtributoLoginSip?.Trim();
        var attrSenha = _op.AtributoSenhaSip?.Trim();
        if (!Configurado || string.IsNullOrEmpty(attrRamal) || string.IsNullOrEmpty(attrSenha) || string.IsNullOrWhiteSpace(login)) return (null, null, null, false);
        try
        {
            using var c = Conectar(CredencialServico());
            c.Bind();
            var filtro = $"(&(objectCategory=person)(objectClass=user)(sAMAccountName={EscaparFiltro(NormalizarLogin(login))}))";
            var atributos = new List<string> { attrRamal, attrSenha };
            if (!string.IsNullOrEmpty(attrLogin)) atributos.Add(attrLogin);
            var resp = (SearchResponse)c.SendRequest(new SearchRequest(_op.BaseDn, filtro, SearchScope.Subtree, atributos.ToArray()));
            if (resp.Entries.Count == 0) return (null, null, null, false);
            var e = resp.Entries[0];
            string? A(string? n) => !string.IsNullOrEmpty(n) && e.Attributes.Contains(n) && e.Attributes[n].Count > 0 ? e.Attributes[n][0]?.ToString() : null;
            return JuntarContaSip(A(attrRamal), A(attrLogin), A(attrSenha));
        }
        catch (Exception ex)
        {
            _log.LogWarning("Não deu para ler a conta SIP de {Login} no AD: {Msg}", login, ex.Message); // sem o valor lido
            return (null, null, null, true);
        }
    }

    /// <summary>As regras acima, sem AD (o modo de teste do Hub usa a mesma função).</summary>
    public static (string? Ramal, string? Login, string? Senha, bool Falhou) JuntarContaSip(string? ramalBruto, string? loginBruto, string? senhaBruta)
    {
        var ramal = LimparRamal(ramalBruto);
        var loginSip = LimparRamal(loginBruto) ?? ramal;
        var senha = senhaBruta?.Trim();
        if (string.IsNullOrEmpty(senha) || senha.Length > 128 || loginSip is null) return (null, null, null, false);
        return (ramal, loginSip, senha, false);
    }
    /// <summary>
    /// 1.2.4: grupos do AD cujo nome (sAMAccountName/cn) ou descrição contém <paramref name="busca"/>; vazio = todos.
    /// Volta no máximo <paramref name="max"/>, em ordem de nome. Só leitura.
    /// </summary>
    public (List<GrupoAdDto>? Grupos, string? Erro) BuscarGrupos(string? busca, int max = 50)
    {
        if (!Configurado) return (null, "o AD não está configurado no servidor");
        try
        {
            using var c = Conectar(CredencialServico());
            c.Bind();
            var t = EscaparFiltro((busca ?? "").Trim());
            var filtro = t.Length == 0 ? "(objectCategory=group)"
                : $"(&(objectCategory=group)(|(sAMAccountName=*{t}*)(cn=*{t}*)(description=*{t}*)))";
            var req = new SearchRequest(_op.BaseDn, filtro, SearchScope.Subtree, "sAMAccountName", "description") { SizeLimit = 1000 };
            var lista = new List<GrupoAdDto>();
            try
            {
                var resp = (SearchResponse)c.SendRequest(req);
                Juntar(resp.Entries);
            }
            catch (DirectoryOperationException ex) when (ex.Response is SearchResponse parcial) { Juntar(parcial.Entries); } // passou do limite: fica o que veio
            void Juntar(SearchResultEntryCollection entradas)
            {
                foreach (SearchResultEntry e in entradas)
                {
                    string? A(string n) => e.Attributes.Contains(n) && e.Attributes[n].Count > 0 ? e.Attributes[n][0]?.ToString()?.Trim() : null;
                    if (A("sAMAccountName") is { Length: > 0 } nome) lista.Add(new GrupoAdDto(nome, A("description")));
                }
            }
            return (lista.OrderBy(g => g.Nome, StringComparer.OrdinalIgnoreCase).Take(max).ToList(), null);
        }
        catch (Exception ex)
        {
            _log.LogWarning("Não deu para pesquisar grupos no AD ({Busca}): {Msg}", busca, ex.Message);
            return (null, "erro ao consultar o AD: " + ex.Message);
        }
    }

    /// <summary>RFC 4515: escapa \ * ( ) e NUL num valor de filtro LDAP.</summary>
    public static string EscaparFiltro(string v) =>
        v.Replace("\\", "\\5c").Replace("*", "\\2a").Replace("(", "\\28").Replace(")", "\\29").Replace("\0", "\\00");

    /// <summary>"CORP\fulano", "fulano@corp.example.com" ou "Fulano" → "fulano".</summary>
    public static string NormalizarLogin(string login)
    {
        login = login.Trim();
        var i = login.LastIndexOf('\\');
        if (i >= 0) login = login[(i + 1)..];
        var a = login.IndexOf('@');
        if (a >= 0) login = login[..a];
        return login.ToLowerInvariant();
    }
}

/// <summary>Sincroniza o diretório do AD de tempos em tempos.</summary>
public sealed class SincronizadorDiretorio : BackgroundService
{
    private readonly Diretorio _dir;
    private readonly Banco _banco;
    private readonly OpcoesHub _op;
    private readonly GruposAdmin _grupos;
    private readonly ILogger<SincronizadorDiretorio> _log;
    private readonly SemaphoreSlim _umaPorVez = new(1, 1);

    public SincronizadorDiretorio(Diretorio dir, Banco banco, OpcoesHub op, GruposAdmin grupos, ILogger<SincronizadorDiretorio> log)
    { _dir = dir; _banco = banco; _op = op; _grupos = grupos; _log = log; }

    public DateTime? UltimaSincronizacao { get; private set; }
    public int UltimoTotal { get; private set; }
    public string? UltimoErro { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!_dir.Configurado) { _log.LogWarning("LDAP não configurado: diretório só com quem entrar no Hub."); return; }
        while (!ct.IsCancellationRequested)
        {
            await SincronizarAgoraAsync(ct);
            await Task.Delay(TimeSpan.FromMinutes(Math.Max(5, _op.Ldap.SincronizarMinutos)), ct);
        }
    }

    /// <summary>
    /// Lê o AD agora (a cada 30 min, e pelo botão "Sincronizar agora" do painel web, 1.2.3): pessoas, ramal (ipPhone) e
    /// os membros dos grupos de administradores. Uma de cada vez. Devolve o erro (ou null) para o painel mostrar.
    /// </summary>
    public async Task<string?> SincronizarAgoraAsync(CancellationToken ct = default)
    {
        if (!_dir.Configurado) return "O diretório (AD) não está configurado no servidor.";
        await _umaPorVez.WaitAsync(ct);
        try
        {
            var pessoas = await Task.Run(_dir.LerPessoas, ct);
            // proteção: se o AD devolver quase nada (erro de filtro/permissão), não desativa todo mundo
            if (pessoas.Count >= 5)
            {
                UltimoTotal = _banco.SincronizarPessoas(pessoas);
                UltimaSincronizacao = DateTime.Now;
                UltimoErro = null;
                _log.LogInformation("Diretório sincronizado com o AD: {Total} pessoas", UltimoTotal);
            }
            else
            {
                UltimoErro = $"O AD devolveu só {pessoas.Count} pessoas; a sincronização foi ignorada.";
                _log.LogWarning("AD devolveu só {Total} pessoas: sincronização ignorada", pessoas.Count);
            }
            await Task.Run(_grupos.Atualizar, ct);
            return UltimoErro;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            UltimoErro = ex.Message;
            _log.LogError(ex, "Falha ao sincronizar o diretório com o AD");
            return "Falha ao ler o AD: " + ex.Message;
        }
        finally { _umaPorVez.Release(); }
    }
}
