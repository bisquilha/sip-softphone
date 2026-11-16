using System.Security.Claims;
using System.Text.RegularExpressions;
using Softphone.Contratos;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.SignalR;

namespace Softphone.Hub;

/// <summary>
/// Configurações que a TI muda pelo painel do app (sem SSH): chave e modelo da IA, retenção, administradores extras.
/// Ficam na tabela config do hub.db; a chave da API vai criptografada (Data Protection, chaves em Dados/chaves-dp).
/// O que vem do hub.env continua valendo quando o painel não definiu nada.
/// </summary>
public sealed class ConfigServidor
{
    private readonly Banco _banco;
    private readonly OpcoesHub _op;
    private readonly IDataProtector _protetor;
    private readonly ILogger<ConfigServidor> _log;
    private readonly string _chaveDoEnv;
    private readonly string _modeloDoEnv;
    private readonly int _retencaoDoEnv;

    public ConfigServidor(Banco banco, OpcoesHub op, IDataProtectionProvider dp, ILogger<ConfigServidor> log)
    {
        _banco = banco; _op = op; _log = log;
        _protetor = dp.CreateProtector("Softphone.Hub.ConfigIa");
        _chaveDoEnv = op.Ia.AnthropicChave ?? "";
        _modeloDoEnv = op.Ia.Modelo;
        _retencaoDoEnv = op.Ia.RetencaoDias;
    }

    /// <summary>De onde veio a chave em uso: "painel", "hub.env" ou "" (sem chave).</summary>
    public string OrigemChave { get; private set; } = "";

    /// <summary>Aplica nas opções o que o painel gravou (chamado na inicialização e depois de cada mudança).</summary>
    public void Aplicar()
    {
        string? chave = null;
        var protegida = _banco.LerConfig("ia.chave");
        if (protegida != null)
        {
            try { chave = _protetor.Unprotect(protegida); }
            catch (Exception ex) { _log.LogWarning("Chave da IA gravada pelo painel não abriu ({Msg}); usando a do hub.env.", ex.Message); }
        }
        _op.Ia.AnthropicChave = chave ?? _chaveDoEnv;
        OrigemChave = chave != null ? "painel" : string.IsNullOrWhiteSpace(_chaveDoEnv) ? "" : "hub.env";
        _op.Ia.Modelo = _banco.LerConfig("ia.modelo") ?? _modeloDoEnv;
        _op.Ia.RetencaoDias = int.TryParse(_banco.LerConfig("ia.retencao"), out var r) ? r : _retencaoDoEnv;
        AplicarProvedores();
        _op.AdminsExtras = (_banco.LerConfig("admins") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        _op.GruposAdmin = (_banco.LerConfig("admins.grupos") ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    // ---------- 1.2.5: provedores de IA (lista no banco, em JSON; cada chave cifrada) ----------

    public sealed record ProvedorGravado(string Id, string Nome, string Tipo, string Url, string Modelo, string ChaveProtegida, bool Ligado, string Uso = "tudo");
    private static readonly System.Text.Json.JsonSerializerOptions Json = new(System.Text.Json.JsonSerializerDefaults.Web);

    public List<ProvedorGravado> LerProvedores()
    {
        var j = _banco.LerConfig("ia.provedores");
        return string.IsNullOrEmpty(j) ? new() : System.Text.Json.JsonSerializer.Deserialize<List<ProvedorGravado>>(j, Json) ?? new();
    }

    public string ModoProvedores => _banco.LerConfig("ia.modo") == "revezar" ? "revezar" : "ordem";

    public void GravarProvedores(IEnumerable<ProvedorGravado> lista, string modo)
    {
        _banco.GravarConfig("ia.provedores", System.Text.Json.JsonSerializer.Serialize(lista.ToList(), Json));
        _banco.GravarConfig("ia.modo", modo == "revezar" ? "revezar" : "ordem");
    }

    public string Proteger(string chave) => _protetor.Protect(chave.Trim());

    /// <summary>
    /// Monta a lista em uso. Na primeira partida do 1.2.5 (nada gravado ainda), a chave Anthropic em uso (painel ou hub.env)
    /// vira o 1º provedor, com o modelo atual: ninguém precisa recadastrar nada.
    /// </summary>
    private void AplicarProvedores()
    {
        if (_banco.LerConfig("ia.provedores") == null)
        {
            var lista = new List<ProvedorGravado>();
            if (!string.IsNullOrWhiteSpace(_op.Ia.AnthropicChave))
            {
                var url = string.IsNullOrWhiteSpace(_op.Ia.AnthropicUrl) ? ProvedorAtivo.UrlAnthropic : _op.Ia.AnthropicUrl.TrimEnd('/');
                lista.Add(new ProvedorGravado(Guid.NewGuid().ToString("N")[..12], "Anthropic", TipoProvedorIa.Anthropic, url, _op.Ia.Modelo,
                    Proteger(_op.Ia.AnthropicChave), true));
                _log.LogInformation("IA: a chave Anthropic ({Origem}) virou o 1º provedor da lista", OrigemChave);
            }
            GravarProvedores(lista, "ordem");
        }
        var ativos = new List<ProvedorAtivo>();
        foreach (var p in LerProvedores())
        {
            string chave;
            try { chave = _protetor.Unprotect(p.ChaveProtegida); }
            catch (Exception ex) { _log.LogWarning("IA {Nome}: a chave gravada não abriu ({Msg}); provedor ignorado.", p.Nome, ex.Message); continue; }
            ativos.Add(new ProvedorAtivo(p.Id, p.Nome, p.Tipo, p.Url, p.Modelo, chave, p.Ligado, p.Uso ?? "tudo"));
        }
        _op.Ia.Provedores = ativos;
        _op.Ia.ModoProvedores = ModoProvedores;
    }

    /// <summary>Resumo para a Visão geral: as IAs ligadas, na ordem, só com o fim da chave.</summary>
    public string DescricaoProvedores()
    {
        var ligados = _op.Ia.Provedores.Where(p => p.Ligado).ToList();
        if (ligados.Count == 0) return _op.Ia.Provedores.Count == 0 ? "nenhuma IA cadastrada" : "nenhuma IA ligada";
        return (ligados.Count > 1 ? (_op.Ia.ModoProvedores == "revezar" ? "revezando: " : "em ordem: ") : "")
            + string.Join(", ", ligados.Select(p => $"{p.Nome} (…{p.ChaveFim})"));
    }

    /// <summary>1.2.3: grupos do AD cujos membros são administradores (um por linha; nome de grupo pode ter vírgula).</summary>
    public void GravarGruposAdmin(IEnumerable<string> grupos) =>
        _banco.GravarConfig("admins.grupos", string.Join("\n", grupos.Select(g => g.Trim()).Where(g => g.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)));

    public string DescricaoChave()
    {
        var k = _op.Ia.AnthropicChave ?? "";
        if (string.IsNullOrWhiteSpace(k)) return "não configurada";
        var fim = k.Length > 4 ? k[^4..] : "";
        return $"configurada ({(OrigemChave == "painel" ? "pelo painel" : "no hub.env")}, termina em …{fim})";
    }

    public void GravarChave(string? chave)
    {
        if (chave == null) _banco.GravarConfig("ia.chave", null);
        else _banco.GravarConfig("ia.chave", _protetor.Protect(chave.Trim()));
    }

    public void GravarModelo(string? modelo) => _banco.GravarConfig("ia.modelo", string.IsNullOrWhiteSpace(modelo) ? null : modelo.Trim());
    public void GravarRetencao(int? dias) => _banco.GravarConfig("ia.retencao", dias?.ToString());
    public void GravarAdmins(IEnumerable<string> logins) =>
        _banco.GravarConfig("admins", string.Join(",", logins.Select(Diretorio.NormalizarLogin).Where(l => l.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)));
}

/// <summary>Limpeza periódica: medições de qualidade velhas e comunicados vencidos.</summary>
public sealed class LimpezaHub : BackgroundService
{
    private readonly Banco _banco;
    private readonly OpcoesHub _op;
    private readonly ILogger<LimpezaHub> _log;
    public LimpezaHub(Banco banco, OpcoesHub op, ILogger<LimpezaHub> log) { _banco = banco; _op = op; _log = log; }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var (q, c) = _banco.Limpar(Math.Max(7, _op.Ia.RetencaoDias));
                if (q + c > 0) _log.LogInformation("Limpeza: {Q} medições de qualidade e {C} comunicados antigos apagados", q, c);
            }
            catch (Exception ex) { _log.LogWarning(ex, "Limpeza falhou"); }
            try { await Task.Delay(TimeSpan.FromHours(6), ct); } catch (OperationCanceledException) { }
        }
    }
}

/// <summary>Rotas do painel de administração (no próprio app, só para quem está em Hub:Admins ou foi adicionado pelo painel).</summary>
public static class RotasAdmin
{
    private static readonly Regex NomeArquivo = new(@"^[\w\-. ()]{1,80}\.(md|txt)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    /// <summary>Nome de modelo de qualquer IA (OpenRouter usa "empresa/modelo", Bedrock usa ":").</summary>
    private static readonly Regex ModeloIa = new(@"^[A-Za-z0-9][A-Za-z0-9._\-:/@]{0,119}$", RegexOptions.CultureInvariant);

    public static void Mapear(WebApplication app, string versao, DateTime iniciadoEm)
    {
        var op = app.Services.GetRequiredService<OpcoesHub>();
        var banco = app.Services.GetRequiredService<Banco>();
        var cfg = app.Services.GetRequiredService<ConfigServidor>();
        var pres = app.Services.GetRequiredService<Presencas>();
        var assistente = app.Services.GetRequiredService<Assistente>();
        var transcritor = app.Services.GetRequiredService<Transcritor>();
        var proc = app.Services.GetRequiredService<ProcessadorGravacoes>();
        var sinc = app.Services.GetRequiredService<SincronizadorDiretorio>();
        var grupos = app.Services.GetRequiredService<GruposAdmin>();
        var dir = app.Services.GetRequiredService<Diretorio>();
        var reunioes = app.Services.GetRequiredService<Reunioes>();
        var hub = app.Services.GetRequiredService<IHubContext<ChatHub>>();
        var log = app.Logger;
        var pastaConhecimento = Path.Combine(op.Dados, "conhecimento");

        string Eu(ClaimsPrincipal u) => Diretorio.NormalizarLogin(u.Identity?.Name ?? "");
        bool Admin(ClaimsPrincipal u) => op.EhAdmin(Eu(u));
        IResult Proibido() => Results.Json(new { erro = "Só a TI (administradores do Softphone) acessa o painel." }, statusCode: 403);
        IResult Erro(string msg) => Results.BadRequest(new { erro = msg });
        EstadoIaDto EstadoIa() => new(true, transcritor.Configurado, assistente.Configurado, op.Ia.RetencaoDias, op.Ia.GravacaoMaxMb);

        var g = app.MapGroup("/api/admin").RequireAuthorization();

        g.MapGet("/painel", (ClaimsPrincipal u) =>
        {
            if (!Admin(u)) return Proibido();
            var pessoas = banco.ListarPessoas().ToDictionary(p => p.Login, StringComparer.OrdinalIgnoreCase);
            var clientes = banco.Clientes().Select(c =>
            {
                pessoas.TryGetValue(c.Login, out var p);
                return new ClienteDto(c.Login, p?.Nome ?? c.Login, p?.Unidade, c.Versao, c.Pc, c.VistoEm, pres.De(c.Login) != EstadoPresenca.Offline);
            }).ToArray();
            var versoes = clientes.Where(c => c.VistoEm > DateTime.UtcNow.AddDays(-30))
                .GroupBy(c => c.Versao ?? "?").OrderByDescending(x => x.Key).ToDictionary(x => x.Key, x => x.Count());
            var wavs = Directory.Exists(proc.PastaGravacoes) ? new DirectoryInfo(proc.PastaGravacoes).GetFiles("*.wav") : Array.Empty<FileInfo>();
            double livre = 0;
            try { livre = new DriveInfo(Path.GetPathRoot(op.Dados)!).AvailableFreeSpace / 1024d / 1024 / 1024; } catch { }
            return Results.Ok(new PainelAdminDto(versao, iniciadoEm, pres.Online, sinc.UltimoTotal, dir.Configurado, sinc.UltimaSincronizacao, sinc.UltimoErro,
                reunioes.Configurado, transcritor.Configurado, assistente.Configurado, cfg.DescricaoProvedores(),
                op.Ia.Provedores.FirstOrDefault(p => p.Ligado)?.Modelo ?? "", op.Ia.RetencaoDias,
                wavs.Length, Math.Round(wavs.Sum(f => f.Length) / 1024d / 1024, 1), Math.Round(livre, 1), versoes, clientes,
                op.Admins.Concat(op.AdminsExtras).Select(Diretorio.NormalizarLogin).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                grupos.Situacao));
        });

        // ----- 1.2.3: "Sincronizar agora" (sem esperar os 30 minutos) -----
        g.MapPost("/diretorio/sincronizar", async (ClaimsPrincipal u) =>
        {
            if (!Admin(u)) return Proibido();
            var erro = await sinc.SincronizarAgoraAsync();
            log.LogInformation("Painel: sincronização do AD pedida por {Eu} ({Resultado})", Eu(u), erro ?? $"{sinc.UltimoTotal} pessoas");
            return erro != null
                ? Results.Json(new { erro }, statusCode: 409)
                : Results.Ok(new SincronizacaoDto(sinc.UltimoTotal, sinc.UltimaSincronizacao));
        });

        // ----- 1.2.4: pesquisa de grupos do AD (o painel mostra a lista para escolher) -----
        g.MapGet("/diretorio/grupos", (string? busca, ClaimsPrincipal u) =>
        {
            if (!Admin(u)) return Proibido();
            if (busca is { Length: > 100 }) return Erro("Busca grande demais.");
            var (lista, erro) = grupos.Buscar(busca);
            return lista is null ? Results.Json(new { erro }, statusCode: 409) : Results.Ok(lista);
        });

        // ----- 1.2.3: grupos do AD cujos membros são administradores -----
        g.MapPut("/grupos-admin", (string[] lista, ClaimsPrincipal u) =>
        {
            if (!Admin(u)) return Proibido();
            var nomes = (lista ?? Array.Empty<string>()).Select(x => x.Trim()).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (nomes.Any(n => n.Length > 256)) return Erro("Nome de grupo grande demais.");
            // só grava se todos os grupos existem no AD (um nome errado não pode passar despercebido)
            foreach (var n in nomes)
            {
                var (logins, erro) = grupos.Ler(n);
                if (logins == null) return Erro($"Grupo {n}: {erro}.");
            }
            // 1.3: proteção contra perder o último administrador efetivo (sem grupos, sem extras e sem Hub__Admins no hub.env)
            if (nomes.Length == 0 && op.Admins.Length == 0 && op.AdminsExtras.Length == 0)
                return Erro("Isso deixaria o painel sem nenhum administrador. Mantenha pelo menos um grupo ou uma pessoa.");
            cfg.GravarGruposAdmin(nomes);
            cfg.Aplicar();
            grupos.Atualizar();
            log.LogInformation("Painel: grupos de administradores = {Lista} (por {Eu})", string.Join("; ", nomes), Eu(u));
            return Results.Ok(grupos.Situacao);
        });

        // ----- IA: chave, modelo, retenção -----
        g.MapPut("/ia", async (ConfigIaDto d, ClaimsPrincipal u) =>
        {
            if (!Admin(u)) return Proibido();
            // 1.2.5: chave e modelo ficam nos provedores; aqui sobrou a retenção
            if (!string.IsNullOrWhiteSpace(d.Chave) || d.ApagarChave || !string.IsNullOrWhiteSpace(d.Modelo))
                return Erro("A chave e o modelo agora ficam em IA > Provedores: cadastre ou edite um provedor.");
            if (d.RetencaoDias is int r && (r < 1 || r > 365)) return Erro("Retenção: de 1 a 365 dias.");
            if (d.RetencaoDias != null) cfg.GravarRetencao(d.RetencaoDias);
            cfg.Aplicar();
            log.LogInformation("Painel: retenção das gravações = {Ret} dias (por {Eu})", op.Ia.RetencaoDias, Eu(u));
            await hub.Clients.All.SendAsync(HubNomes.EvEstadoIa, EstadoIa());
            return Results.Ok(EstadoIa());
        });

        g.MapPost("/ia/testar", async (ClaimsPrincipal u, CancellationToken ct) =>
        {
            if (!Admin(u)) return Proibido();
            if (!assistente.Configurado) return Results.Ok(new TesteIaDto(false, "Nenhuma IA ligada (IA > Provedores)."));
            try { return Results.Ok(new TesteIaDto(true, await assistente.TestarAsync(ct))); }
            catch (Exception ex) when (ex is not OperationCanceledException) { return Results.Ok(new TesteIaDto(false, ex.Message)); }
        });

        // ----- 1.2.5: provedores de IA (várias IAs, em ordem ou revezando) -----
        ProvedoresIaDto Provedores() => new(cfg.ModoProvedores, cfg.LerProvedores().Select(p =>
        {
            var ativo = op.Ia.Provedores.FirstOrDefault(x => x.Id == p.Id);
            var s = assistente.SituacaoDe(p.Id);
            return new ProvedorIaDto(p.Id, p.Nome, p.Tipo, p.Url, p.Modelo, p.Ligado, ativo?.ChaveFim ?? "?", s.UltimoOk, s.UltimaFalha,
                ativo is null ? "a chave gravada não abriu neste servidor: cadastre a chave de novo" : s.UltimoErro, p.Uso ?? "tudo");
        }).ToArray());

        async Task<IResult> Mudou(ClaimsPrincipal u, string oque)
        {
            cfg.Aplicar();
            assistente.Recriar();
            log.LogInformation("Painel: provedores de IA: {Oque} (por {Eu}); em uso: {Lista}", oque, Eu(u), cfg.DescricaoProvedores());
            await hub.Clients.All.SendAsync(HubNomes.EvEstadoIa, EstadoIa());
            return Results.Ok(Provedores());
        }

        // endereço: https; http só para a própria máquina (testes) — a chave não pode viajar aberta pela rede
        string? ConferirUrl(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "https" && uri.Scheme != "http")) return "Endereço inválido (ex.: https://api.exemplo.com/v1).";
            if (uri.Scheme == "http" && !uri.IsLoopback) return "Use https: a chave da IA não pode ir aberta pela rede.";
            if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)) return "O endereço não pode ter ? nem #.";
            return null;
        }

        g.MapGet("/ia/provedores", (ClaimsPrincipal u) => Admin(u) ? Results.Ok(Provedores()) : Proibido());

        g.MapPost("/ia/provedores", async (SalvarProvedorIaDto d, ClaimsPrincipal u) =>
        {
            if (!Admin(u)) return Proibido();
            var lista = cfg.LerProvedores();
            var atual = string.IsNullOrEmpty(d.Id) ? null : lista.FirstOrDefault(p => p.Id == d.Id);
            if (!string.IsNullOrEmpty(d.Id) && atual is null) return Results.NotFound(new { erro = "Provedor não encontrado (foi apagado?)." });
            var nome = (d.Nome ?? "").Trim();
            if (nome.Length is 0 or > 40) return Erro("Nome de 1 a 40 caracteres.");
            if (lista.Any(p => p.Id != atual?.Id && p.Nome.Equals(nome, StringComparison.OrdinalIgnoreCase))) return Erro($"Já existe uma IA chamada {nome}.");
            if (!TipoProvedorIa.Todos.Contains(d.Tipo)) return Erro("Tipo de IA desconhecido.");
            var url = string.IsNullOrWhiteSpace(d.Url) ? ProvedorAtivo.UrlPadrao(d.Tipo) : d.Url.Trim().TrimEnd('/');
            if (url is null) return Erro("Para \"Outra\", informe o endereço da API (compatível com OpenAI, ex.: https://api.exemplo.com/v1).");
            if (ConferirUrl(url) is string eu) return Erro(eu);
            var modelo = (d.Modelo ?? "").Trim();
            if (!ModeloIa.IsMatch(modelo)) return Erro("Informe o modelo (use \"Buscar modelos\" para ver os disponíveis).");
            var chave = d.Chave?.Trim();
            if (string.IsNullOrEmpty(chave) && atual is null) return Erro("Informe a chave da API.");
            if (!string.IsNullOrEmpty(chave) && (chave.Length is < 8 or > 500 || chave.Any(char.IsWhiteSpace))) return Erro("Chave inválida (cole a chave inteira, sem espaços).");
            var protegida = string.IsNullOrEmpty(chave) ? atual!.ChaveProtegida : cfg.Proteger(chave);
            var uso = string.IsNullOrWhiteSpace(d.Uso) ? (atual?.Uso ?? "tudo") : d.Uso.Trim().ToLowerInvariant();
            if (uso is not ("tudo" or "chat" or "ligacoes")) return Erro("Uso inválido: tudo, chat ou ligacoes.");
            var novo = new ConfigServidor.ProvedorGravado(atual?.Id ?? Guid.NewGuid().ToString("N")[..12], nome, d.Tipo, url, modelo, protegida, d.Ligado, uso);
            var i = atual is null ? -1 : lista.IndexOf(atual);
            if (i >= 0) lista[i] = novo; else lista.Add(novo);
            cfg.GravarProvedores(lista, cfg.ModoProvedores);
            return await Mudou(u, $"{(atual is null ? "nova" : "alterada")} {nome} ({d.Tipo}, {modelo}{(d.Ligado ? "" : ", desligada")}{(string.IsNullOrEmpty(chave) ? "" : ", chave nova")})");
        });

        g.MapDelete("/ia/provedores/{id}", async (string id, ClaimsPrincipal u) =>
        {
            if (!Admin(u)) return Proibido();
            var lista = cfg.LerProvedores();
            var p = lista.FirstOrDefault(x => x.Id == id);
            if (p is null) return Results.NotFound(new { erro = "Provedor não encontrado." });
            lista.Remove(p);
            cfg.GravarProvedores(lista, cfg.ModoProvedores);
            return await Mudou(u, "excluída " + p.Nome);
        });

        g.MapPut("/ia/provedores/ordem", async (OrdemProvedoresIaDto d, ClaimsPrincipal u) =>
        {
            if (!Admin(u)) return Proibido();
            if (d.Modo is not ("ordem" or "revezar")) return Erro("Modo: ordem ou revezar.");
            var lista = cfg.LerProvedores();
            var ids = (d.Ids ?? []).Distinct().ToList();
            // a lista mudou em outra aba/por outro admin: não grava uma ordem que perde ou inventa provedores
            if (ids.Count != lista.Count || ids.Any(x => lista.All(p => p.Id != x))) return Results.Json(new { erro = "A lista mudou enquanto você mexia. Atualize e tente de novo." }, statusCode: 409);
            cfg.GravarProvedores(ids.Select(x => lista.First(p => p.Id == x)), d.Modo);
            return await Mudou(u, $"ordem {string.Join(" > ", ids.Select(x => lista.First(p => p.Id == x).Nome))}, modo {d.Modo}");
        });

        g.MapPost("/ia/provedores/{id}/testar", async (string id, ClaimsPrincipal u, CancellationToken ct) =>
        {
            if (!Admin(u)) return Proibido();
            var p = op.Ia.Provedores.FirstOrDefault(x => x.Id == id);
            if (p is null) return Results.NotFound(new { erro = "Provedor não encontrado (ou a chave dele não abriu)." });
            try { return Results.Ok(new TesteIaDto(true, await assistente.TestarProvedorAsync(p, ct))); }
            catch (Exception ex) when (ex is not OperationCanceledException) { return Results.Ok(new TesteIaDto(false, ex.Message)); }
        });

        g.MapPost("/ia/modelos", async (BuscarModelosIaDto d, ClaimsPrincipal u, CancellationToken ct) =>
        {
            if (!Admin(u)) return Proibido();
            if (!TipoProvedorIa.Todos.Contains(d.Tipo)) return Erro("Tipo de IA desconhecido.");
            var gravado = string.IsNullOrEmpty(d.Id) ? null : op.Ia.Provedores.FirstOrDefault(x => x.Id == d.Id);
            var url = string.IsNullOrWhiteSpace(d.Url) ? ProvedorAtivo.UrlPadrao(d.Tipo) : d.Url.Trim().TrimEnd('/');
            if (url is null) return Erro("Informe o endereço da API primeiro.");
            if (ConferirUrl(url) is string eu) return Erro(eu);
            var chave = string.IsNullOrWhiteSpace(d.Chave) ? gravado?.Chave : d.Chave.Trim();
            if (string.IsNullOrEmpty(chave)) return Erro("Informe a chave da API primeiro.");
            try { return Results.Ok(await Assistente.BuscarModelosAsync(d.Tipo, url, chave, ct)); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { return Erro("Não deu para buscar os modelos: " + Assistente.Mascarar(ex.Message, chave) + " Dá para digitar o nome do modelo."); }
        });

        // ----- qualidade das ligações -----
        g.MapGet("/qualidade", (int? dias, ClaimsPrincipal u) =>
        {
            if (!Admin(u)) return Proibido();
            return Results.Ok(banco.PainelQualidade(Math.Clamp(dias ?? 7, 1, 90), 3.6));
        });

        // ----- comunicados -----
        g.MapGet("/comunicados", (ClaimsPrincipal u) => Admin(u) ? Results.Ok(banco.ComunicadosAdmin()) : Proibido());
        g.MapGet("/unidades", (ClaimsPrincipal u) => Admin(u) ? Results.Ok(banco.Unidades()) : Proibido());

        g.MapPost("/comunicados", async (NovoComunicadoDto n, ClaimsPrincipal u) =>
        {
            if (!Admin(u)) return Proibido();
            if (string.IsNullOrWhiteSpace(n.Titulo) || n.Titulo.Trim().Length > 100) return Erro("Título de 1 a 100 caracteres.");
            if (string.IsNullOrWhiteSpace(n.Texto) || n.Texto.Trim().Length > 2000) return Erro("Texto de 1 a 2000 caracteres.");
            if (n.Nivel is not (NivelComunicado.Info or NivelComunicado.Importante or NivelComunicado.Urgente)) return Erro("Nível inválido.");
            var c = banco.InserirComunicado(n, Eu(u));
            var destinatarios = banco.LoginsDaUnidade(c.Unidade);
            foreach (var l in destinatarios) await hub.Clients.Group("u:" + l.ToLowerInvariant()).SendAsync(HubNomes.EvComunicado, c);
            log.LogInformation("Comunicado {Id} ({Nivel}) de {Eu} para {Para}: {Titulo}", c.Id, c.Nivel, Eu(u), c.Unidade ?? "todos", c.Titulo);
            return Results.Ok(c);
        });

        g.MapDelete("/comunicados/{id:long}", async (long id, ClaimsPrincipal u) =>
        {
            if (!Admin(u)) return Proibido();
            if (!banco.CancelarComunicado(id)) return Results.NotFound();
            await hub.Clients.All.SendAsync(HubNomes.EvComunicadoCancelado, id);
            return Results.Ok();
        });

        // ----- administradores (os do hub.env ficam fixos; o painel acrescenta) -----
        g.MapPut("/admins", (string[] extras, ClaimsPrincipal u) =>
        {
            if (!Admin(u)) return Proibido();
            // os do hub.env não vão para o banco: senão tirar alguém do Hub__Admins deixaria de revogar o acesso
            var fixos = new HashSet<string>(op.Admins.Select(Diretorio.NormalizarLogin), StringComparer.OrdinalIgnoreCase);
            var validos = extras.Select(Diretorio.NormalizarLogin)
                .Where(l => l.Length > 0 && !fixos.Contains(l) && banco.ObterPessoa(l) != null)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            // 1.3: nunca deixa o painel sem administrador efetivo (os do hub.env são a contingência se o AD sair do ar)
            if (fixos.Count == 0 && validos.Count == 0 && op.AdminsDeGrupos.Length == 0)
                return Erro("Isso deixaria o painel sem nenhum administrador. Mantenha pelo menos uma pessoa ou um grupo com membros.");
            cfg.GravarAdmins(validos);
            cfg.Aplicar();
            log.LogInformation("Painel: administradores extras = {Lista} (por {Eu})", string.Join(", ", validos), Eu(u));
            return Results.Ok(op.Admins.Concat(op.AdminsExtras).Distinct(StringComparer.OrdinalIgnoreCase));
        });

        // ----- ramais: o mesmo do script do AD, para ver no painel -----
        g.MapGet("/ramais-app", (ClaimsPrincipal u) =>
            Admin(u) ? Results.Ok(banco.ListarRamais().Select(r => new RamalAdminDto(r.Login, r.Nome, r.RamalAprendido, r.RamalAd, r.Ativo))) : Proibido());
    }
}
