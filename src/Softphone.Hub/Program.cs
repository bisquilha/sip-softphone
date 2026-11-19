// Softphone Hub: presença, chat interno e diretório (AD) para o softphone Softphone.
// Login: Kerberos/Windows (GET /api/entrar, automático) ou usuário+senha do AD (POST /api/entrar-senha). Os dois devolvem
// um token (JWT) que o app usa no SignalR (/hub) e nos arquivos.
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Softphone.Contratos;
using Softphone.Hub;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.IdentityModel.Tokens;

const string Versao = "1.3.4";
var builder = WebApplication.CreateBuilder(args);

var op = new OpcoesHub();
builder.Configuration.GetSection("Hub").Bind(op);
op.Dados = Path.GetFullPath(op.Dados);
Directory.CreateDirectory(Path.Combine(op.Dados, "arquivos"));
builder.Services.AddSingleton(op);
builder.Services.AddSingleton<Banco>();
builder.Services.AddSingleton<Diretorio>();
builder.Services.AddSingleton<AcessoContas>();
builder.Services.AddSingleton<Presencas>();
builder.Services.AddSingleton<Reunioes>();
builder.Services.AddSingleton<ConvitesReuniao>();
builder.Services.AddSingleton<Transcritor>();
builder.Services.AddSingleton<ContasSip>();
builder.Services.AddSingleton<Assistente>();
builder.Services.AddSingleton<ProcessadorGravacoes>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ProcessadorGravacoes>());
builder.Services.AddSingleton<GruposAdmin>();
builder.Services.AddSingleton<AcessoPerfis>();
builder.Services.AddSingleton<Automacoes>();
builder.Services.AddSingleton<SincronizadorDiretorio>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<SincronizadorDiretorio>());
// 1.1: configurações do painel (chave da IA criptografada) e limpeza periódica
builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(op.Dados, "chaves-dp"))).SetApplicationName("Softphone.Hub");
builder.Services.AddSingleton<ConfigServidor>();
builder.Services.AddHostedService<LimpezaHub>();
var iniciadoEm = DateTime.UtcNow;

// chave do token: gerada uma vez e guardada na pasta de dados (trocar o arquivo = derruba todos os tokens)
var arqChave = Path.Combine(op.Dados, "jwt.key");
if (!File.Exists(arqChave)) File.WriteAllBytes(arqChave, RandomNumberGenerator.GetBytes(48));
var chave = new SymmetricSecurityKey(File.ReadAllBytes(arqChave));
const string Emissor = "softphone-hub";

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.MapInboundClaims = false;
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidIssuer = Emissor, ValidAudience = Emissor, IssuerSigningKey = chave,
            NameClaimType = "sub", ClockSkew = TimeSpan.FromMinutes(2),
        };
        // WebSocket não manda cabeçalho Authorization: o SignalR manda o token na URL
        o.Events = new JwtBearerEvents
        {
            OnMessageReceived = ctx =>
            {
                var t = ctx.Request.Query["access_token"];
                if (!string.IsNullOrEmpty(t) && ctx.HttpContext.Request.Path.StartsWithSegments(HubNomes.Rota)) ctx.Token = t;
                return Task.CompletedTask;
            },
            // token emitido antes de a conta ser desativada no AD deixa de valer (R07)
            OnTokenValidated = ctx =>
            {
                var login = ctx.Principal?.FindFirst("sub")?.Value;
                if (login != null && !ctx.HttpContext.RequestServices.GetRequiredService<AcessoContas>().Permitido(login))
                    ctx.Fail("Conta desativada no AD.");
                return Task.CompletedTask;
            },
        };
    })
    .AddNegotiate();
builder.Services.AddAuthorization();
builder.Services.AddSignalR(o => { o.MaximumReceiveMessageSize = 64 * 1024; o.EnableDetailedErrors = op.Teste; });
builder.Services.AddSingleton<IUserIdProvider, LoginComoId>();
// limite geral = o maior (gravação); cada endpoint confere o seu
var maiorMb = Math.Max(op.ArquivoMaxMb, op.Ia.GravacaoMaxMb);
builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = maiorMb * 1024L * 1024L);
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = (maiorMb + 1) * 1024L * 1024L);

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();

// 1.3: auditoria de tudo que a TI muda (POST/PUT/DELETE em /api/admin e /api/artigos): quem, o quê, resultado. O corpo da
// requisição NÃO é gravado (pode ter chave de IA ou texto de artigo); só método, caminho e status.
app.Use(async (ctx, next) =>
{
    await next();
    var caminho = ctx.Request.Path.Value ?? "";
    if (ctx.Request.Method is "GET" or "HEAD" or "OPTIONS") return;
    if (!(caminho.StartsWith("/api/admin/", StringComparison.OrdinalIgnoreCase) || caminho.StartsWith("/api/artigos", StringComparison.OrdinalIgnoreCase))) return;
    try
    {
        var quem = Diretorio.NormalizarLogin(ctx.User.Identity?.Name ?? "(anônimo)");
        var acao = ctx.Request.Method switch { "POST" => "EXECUTAR", "PUT" => "ALTERAR", "DELETE" => "EXCLUIR", var m => m };
        app.Services.GetRequiredService<Banco>().Auditar(quem, acao, caminho, ctx.Response.StatusCode, ctx.Response.StatusCode == 403 ? "acesso negado" : null);
    }
    catch (Exception ex) { app.Logger.LogWarning("Auditoria não gravada: {Msg}", ex.Message); }
});

var banco = app.Services.GetRequiredService<Banco>();
var dir = app.Services.GetRequiredService<Diretorio>();
var pres = app.Services.GetRequiredService<Presencas>();
// o que a TI mudou pelo painel (chave/modelo da IA, retenção, admins) vale por cima do hub.env
var cfgServidor = app.Services.GetRequiredService<ConfigServidor>();
cfgServidor.Aplicar();
// administradores por grupo do AD (1.2.3): lidos já na partida, sem segurar o Hub se o AD demorar
_ = Task.Run(() => { try { app.Services.GetRequiredService<GruposAdmin>().Atualizar(); } catch (Exception ex) { app.Logger.LogWarning(ex, "Grupos de administradores não lidos na partida"); } });
app.Services.GetRequiredService<Assistente>().Recriar();

string EmitirToken(string login, string nome, out DateTime expira)
{
    expira = DateTime.UtcNow.AddDays(Math.Max(1, op.TokenDias));
    var tk = new JwtSecurityToken(Emissor, Emissor,
        new[] { new Claim("sub", login), new Claim("name", nome) },
        expires: expira, signingCredentials: new SigningCredentials(chave, SecurityAlgorithms.HmacSha256));
    return new JwtSecurityTokenHandler().WriteToken(tk);
}

PessoaDto Dto(Banco.Pessoa p) => new(p.Login, p.Nome, p.Setor, p.Unidade, p.Email, p.Ramal, pres.De(p.Login), p.VistoEm, p.Recado);

var acesso = app.Services.GetRequiredService<AcessoContas>();

IResult Entrar(string login)
{
    if (!acesso.Permitido(login)) return Results.Json(new { erro = "Conta desativada no AD." }, statusCode: 403);
    var p = banco.ObterPessoa(login);
    if (p is null)
    {
        // ainda não veio na sincronização (usuário novo): entra com o login como nome até a próxima sincronização
        banco.GarantirPessoa(login, login);
        p = banco.ObterPessoa(login)!;
    }
    var token = EmitirToken(p.Login, p.Nome, out var exp);
    return Results.Ok(new EntrarResposta(token, exp, Dto(p), Versao, op.EhAdmin(p.Login)));
}

app.MapGet("/saude", (SincronizadorDiretorio s) => Results.Ok(new
{
    versao = Versao, online = pres.Online, ldap = dir.Configurado, diretorio = s.UltimoTotal, reunioes = app.Services.GetRequiredService<Reunioes>().Configurado,
    transcricao = app.Services.GetRequiredService<Transcritor>().Configurado, resumo = app.Services.GetRequiredService<Assistente>().Configurado,
    sincronizadoEm = s.UltimaSincronizacao, erroDiretorio = s.UltimoErro,
}));

// Login automático (Kerberos no servidor Linux; Kerberos/NTLM no Windows de desenvolvimento)
app.MapGet("/api/entrar", (HttpContext ctx) =>
{
    var nome = ctx.User.Identity?.Name;
    return string.IsNullOrWhiteSpace(nome) ? Results.Unauthorized() : Entrar(Diretorio.NormalizarLogin(nome));
}).RequireAuthorization(new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder(NegotiateDefaults.AuthenticationScheme).RequireAuthenticatedUser().Build());

// Link de reunião para Outlook/e-mail (1.2.2): abre no navegador; o Windows diz quem é (Kerberos), o Hub confere se a
// pessoa pode entrar na sala, dá o passe do Jitsi e manda para lá. Fora da rede da empresa não funciona (de propósito).
app.MapGet("/reuniao/{sala}", (string sala, HttpContext ctx, Reunioes reunioes, ConvitesReuniao convites) =>
{
    sala = sala.Trim().ToLowerInvariant();
    if (!Reunioes.SalaValida(sala)) return Results.NotFound();
    if (!reunioes.Configurado) return Results.Text("Reuniões ainda não estão configuradas no servidor.", statusCode: 503);
    var nome = ctx.User.Identity?.Name;
    if (string.IsNullOrWhiteSpace(nome)) return Results.Unauthorized();
    var login = Diretorio.NormalizarLogin(nome);
    if (!acesso.Permitido(login)) return Results.Text("Conta desativada no AD.", statusCode: 403);
    if (ConvitesReuniao.ConversaDaSala(sala) is long conv && !banco.EhMembro(conv, login) && !convites.Convidado(sala, login))
        return Results.Text("Esta reunião é de uma conversa da qual você não participa.", statusCode: 403);
    var p = banco.ObterPessoa(login);
    var passe = reunioes.Passe(sala, "Reunião", login, p?.Nome ?? login, p?.Email);
    ctx.Response.Headers.CacheControl = "no-store";
    return Results.Redirect(passe.Url);
}).RequireAuthorization(new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder(NegotiateDefaults.AuthenticationScheme).RequireAuthenticatedUser().Build());

// Login com usuário e senha do AD (reserva). A senha só é usada para o bind no AD e não é guardada.
// 1.3: proteção contra tentativa de senha em sequência: 5 erros em 10 min bloqueiam aquele login por 10 min (resposta 429), venha de onde vier.
// Fica na memória do Hub (reiniciar zera). O erro e o bloqueio entram na auditoria; a senha nunca é gravada.
var tentativas = new Dictionary<string, (int Erros, DateTime Inicio, DateTime? BloqueadoAte)>(StringComparer.OrdinalIgnoreCase);
app.MapPost("/api/entrar-senha", (EntrarComSenha req) =>
{
    var chave = Diretorio.NormalizarLogin(req.Login ?? "");
    if (chave.Length is 0 or > 128) return Results.Json(new { erro = "Usuário ou senha inválidos." }, statusCode: 401);
    lock (tentativas)
    {
        if (tentativas.TryGetValue(chave, out var t) && t.BloqueadoAte is { } ate && ate > DateTime.UtcNow)
            return Results.Json(new { erro = $"Muitas tentativas. Tente de novo em {Math.Ceiling((ate - DateTime.UtcNow).TotalMinutes):0} min." }, statusCode: 429);
    }
    var login = dir.ValidarSenha(req.Login, req.Senha);
    if (login is null)
    {
        lock (tentativas)
        {
            tentativas.TryGetValue(chave, out var t);
            if (t.Inicio == default || DateTime.UtcNow - t.Inicio > TimeSpan.FromMinutes(10)) t = (0, DateTime.UtcNow, null);
            t.Erros++;
            if (t.Erros >= 5) { t.BloqueadoAte = DateTime.UtcNow.AddMinutes(10); try { banco.Auditar(chave, "ENTRAR", "/api/entrar-senha", 429, "login bloqueado por 10 min (5 senhas erradas)"); } catch { } }
            else { try { banco.Auditar(chave, "ENTRAR", "/api/entrar-senha", 401, "senha inválida"); } catch { } }
            tentativas[chave] = t;
            if (tentativas.Count > 5000) foreach (var k in tentativas.Where(x => DateTime.UtcNow - x.Value.Inicio > TimeSpan.FromHours(1)).Select(x => x.Key).ToList()) tentativas.Remove(k);
        }
        return Results.Json(new { erro = "Usuário ou senha inválidos." }, statusCode: 401);
    }
    lock (tentativas) tentativas.Remove(chave);
    return Entrar(login);
});

if (op.Teste)
{
    // SÓ em teste: entra como qualquer login, sem senha. Nunca ligar em produção.
    app.MapPost("/api/entrar-teste", (string login, string? nome, string? setor, string? ramalAd) =>
    {
        login = Diretorio.NormalizarLogin(login);
        banco.GarantirPessoa(login, nome ?? login, setor);
        if (setor != null) banco.DefinirSetorParaTeste(login, setor);
        if (ramalAd != null) banco.DefinirRamalAd(login, Diretorio.LimparRamal(ramalAd));
        return Entrar(login);
    });
    // SÓ em teste: finge os membros de um grupo do AD (membros = logins separados por vírgula)
    app.MapPost("/api/teste/grupo", (string nome, string? membros) =>
    {
        app.Services.GetRequiredService<GruposAdmin>().SimularParaTeste(nome, (membros ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries));
        return Results.Ok();
    });
    // SÓ em teste: finge o estado da conta no AD (ativa | desativada | naoexiste)
    app.MapPost("/api/teste/conta", (string login, string estado) =>
    {
        var e = estado.ToLowerInvariant() switch { "desativada" => EstadoContaAd.Desativada, "naoexiste" => EstadoContaAd.NaoExiste, _ => EstadoContaAd.Ativa };
        acesso.SimularParaTeste(login, e);
        return Results.Ok();
    });
    // SÓ em teste: finge o ramal e a senha do SIP que o AD teria para uma pessoa (sem ramal e sem senha = tira)
    app.MapPost("/api/teste/conta-sip", (string login, string? ramal, string? loginSip, string? senha) =>
    {
        app.Services.GetRequiredService<ContasSip>().SimularParaTeste(login, ramal, loginSip, senha);
        return Results.Ok();
    });
    app.Logger.LogWarning("MODO DE TESTE LIGADO: /api/entrar-teste aceita qualquer login sem senha.");
}

// 1.2.8: conta SIP (ramal + senha) que a TI cadastrou no AD para QUEM PEDE (login do token; ninguém lê a de outra pessoa).
// A senha vai só na resposta, nunca em log, banco ou cache; o AD é lido na hora. AD fora do ar = 503 e o app mantém a conta que tem.
app.MapGet("/api/conta-sip", (ClaimsPrincipal u, HttpContext ctx, ContasSip contas) =>
{
    ctx.Response.Headers.CacheControl = "no-store";
    var (conta, falhou) = contas.Da(u.Identity!.Name!);
    if (falhou) return Results.Json(new { erro = "Não consegui ler o AD agora." }, statusCode: 503);
    app.Logger.LogInformation("Conta SIP do AD pedida por {Eu}: {Resultado}", Diretorio.NormalizarLogin(u.Identity.Name!), conta.Login is null ? "sem dados no AD" : $"ramal {conta.Ramal ?? "-"} login {conta.Login} entregues (senha não registrada)");
    return Results.Ok(conta);
}).RequireAuthorization();

// Troca um token ainda válido por um novo e devolve quem sou eu, a versão e se sou da TI (o app usa quando o login do
// Windows não funciona e há token salvo; sem isso o chat conectava sem saber quem era o usuário: R03/R08).
app.MapPost("/api/renovar", (ClaimsPrincipal u) => Entrar(u.Identity!.Name!)).RequireAuthorization();

app.MapGet("/api/eu", (ClaimsPrincipal u) =>
{
    var p = banco.ObterPessoa(u.Identity!.Name!);
    return p is null ? Results.NotFound() : Results.Ok(Dto(p));
}).RequireAuthorization();

// Arquivos do chat: envia (multipart) e baixa. O arquivo fica em Dados/arquivos/<id>.
app.MapPost("/api/arquivos", async (HttpRequest req, ClaimsPrincipal u) =>
{
    if (!req.HasFormContentType) return Results.BadRequest(new { erro = "Envie como multipart/form-data." });
    var form = await req.ReadFormAsync();
    var f = form.Files.FirstOrDefault();
    if (f is null || f.Length == 0) return Results.BadRequest(new { erro = "Arquivo vazio." });
    if (f.Length > op.ArquivoMaxMb * 1024L * 1024L) return Results.BadRequest(new { erro = $"Máximo {op.ArquivoMaxMb} MB." });
    var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
    await using (var s = File.Create(Path.Combine(op.Dados, "arquivos", id))) await f.CopyToAsync(s);
    var nome = Path.GetFileName(f.FileName);
    if (nome.Length > 200) nome = nome[..200];
    return Results.Ok(banco.RegistrarArquivo(id, nome, f.Length, string.IsNullOrWhiteSpace(f.ContentType) ? "application/octet-stream" : f.ContentType, u.Identity!.Name!));
}).RequireAuthorization().DisableAntiforgery();

app.MapGet("/api/arquivos/{id}", (string id, ClaimsPrincipal u) =>
{
    if (id.Length != 32 || !id.All(Uri.IsHexDigit)) return Results.NotFound();
    var a = banco.Arquivo(id);
    if (a is null || !banco.PodeBaixar(id, u.Identity!.Name!)) return Results.NotFound();
    var caminho = Path.Combine(op.Dados, "arquivos", id);
    return File.Exists(caminho) ? Results.File(caminho, a.Tipo, a.Nome) : Results.NotFound();
}).RequireAuthorization();

// TI: ramais que o Softphone aprendeu x ipPhone do AD (o script Gravar-Ramais-AD.ps1 lê isto com Kerberos e grava no AD).
app.MapGet("/api/admin/ramais", (HttpContext ctx) =>
{
    var login = Diretorio.NormalizarLogin(ctx.User.Identity?.Name ?? "");
    return op.EhAdmin(login) ? Results.Ok(banco.ListarRamais()) : Results.StatusCode(403);
}).RequireAuthorization(new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder(NegotiateDefaults.AuthenticationScheme).RequireAuthenticatedUser().Build());

// ======================= 1.1: painel de administração (no app) e qualidade das ligações =======================
RotasAdmin.Mapear(app, Versao, iniciadoEm);
RotasV13.Mapear(app);
RotasAcesso.Mapear(app);

// O app manda ao fim de cada ligação atendida; o painel da TI mostra por unidade e as piores.
app.MapPost("/api/qualidade", (QualidadeLigacaoDto q, ClaimsPrincipal u) =>
{
    var login = u.Identity!.Name!;
    if (q.Numero.Length is 0 or > 40 || q.DuracaoSegundos is < 0 or > 86400 || q.Mos is < 1 or > 5 || q.PerdaPct is < 0 or > 100 || q.JitterMs is < 0 or > 10000)
        return Results.BadRequest(new { erro = "Medição fora da faixa." });
    var p = banco.ObterPessoa(login);
    banco.InserirQualidade(login, p?.Unidade, p?.Setor, q with { Numero = new string(q.Numero.Where(ch => char.IsDigit(ch) || ch is '*' or '#' or '+').ToArray()) });
    return Results.Ok();
}).RequireAuthorization();

// ======================= IA: gravações, transcrição, resumo, sugestões =======================
var proc = app.Services.GetRequiredService<ProcessadorGravacoes>();
var transcritor = app.Services.GetRequiredService<Transcritor>();
var assistente = app.Services.GetRequiredService<Assistente>();

app.MapGet("/api/ia/estado", () =>
    Results.Ok(new EstadoIaDto(true, transcritor.Configurado, assistente.Configurado, op.Ia.RetencaoDias, op.Ia.GravacaoMaxMb)))
    .RequireAuthorization();

// O app manda o WAV estéreo (esquerdo = quem gravou, direito = o outro lado) quando a gravação termina.
app.MapPost("/api/gravacoes", async (HttpRequest req, ClaimsPrincipal u) =>
{
    if (!req.HasFormContentType) return Results.BadRequest(new { erro = "Envie como multipart/form-data." });
    var form = await req.ReadFormAsync();
    var f = form.Files.GetFile("audio");
    if (f is null || f.Length < 44) return Results.BadRequest(new { erro = "Áudio vazio." });
    if (f.Length > op.Ia.GravacaoMaxMb * 1024L * 1024L) return Results.BadRequest(new { erro = $"Gravação maior que {op.Ia.GravacaoMaxMb} MB." });
    var numero = form["numero"].ToString().Trim();
    if (numero.Length is 0 or > 40) return Results.BadRequest(new { erro = "Número inválido." });
    var nome = form["nome"].ToString().Trim();
    var direcao = form["direcao"].ToString() == "entrada" ? "entrada" : "saida";
    if (!DateTime.TryParse(form["inicio"], null, System.Globalization.DateTimeStyles.RoundtripKind, out var inicio)) inicio = DateTime.UtcNow;
    _ = int.TryParse(form["duracao"], out var duracao);

    var temp = Path.Combine(proc.PastaGravacoes, $"tmp-{Guid.NewGuid():N}.wav");
    await using (var s = File.Create(temp)) await f.CopyToAsync(s);
    var cab = new byte[4];
    await using (var s = File.OpenRead(temp)) await s.ReadExactlyAsync(cab);
    if (cab is not [(byte)'R', (byte)'I', (byte)'F', (byte)'F']) { File.Delete(temp); return Results.BadRequest(new { erro = "O áudio precisa ser WAV." }); }

    // 1.2.4: o app 1.1.5+ diz se quer transcrever; sem o campo (apps antigos) transcreve como antes
    var transcrever = form["transcrever"].ToString().Trim().ToLowerInvariant() is not ("false" or "0");
    var id = banco.InserirGravacao(u.Identity!.Name!, numero, nome.Length == 0 ? null : (nome.Length > 120 ? nome[..120] : nome), direcao, inicio, Math.Max(0, duracao),
        transcrever ? StatusGravacao.Fila : StatusGravacao.Guardada);
    File.Move(temp, proc.ArquivoDe(id));
    if (transcrever) proc.Enfileirar(id);
    return Results.Ok(proc.Dto(banco.ObterGravacao(id)!));
}).RequireAuthorization().DisableAntiforgery();

app.MapGet("/api/gravacoes", (ClaimsPrincipal u) => Results.Ok(banco.GravacoesDe(u.Identity!.Name!).Select(proc.Dto)))
    .RequireAuthorization();

// Só o dono vê a própria gravação (nem a TI pelo app).
Banco.Gravacao? MinhaGravacao(long id, ClaimsPrincipal u)
{
    var g = banco.ObterGravacao(id);
    return g != null && string.Equals(g.Dono, u.Identity!.Name, StringComparison.OrdinalIgnoreCase) ? g : null;
}

app.MapGet("/api/gravacoes/{id:long}", (long id, ClaimsPrincipal u) =>
{
    var g = MinhaGravacao(id, u);
    return g is null ? Results.NotFound()
        : Results.Ok(new GravacaoDetalheDto(proc.Dto(g), ProcessadorGravacoes.LerSegmentos(g.Segmentos), ProcessadorGravacoes.LerResumo(g.Resumo)));
}).RequireAuthorization();

app.MapGet("/api/gravacoes/{id:long}/audio", (long id, ClaimsPrincipal u) =>
{
    var g = MinhaGravacao(id, u);
    return g is null || !File.Exists(proc.ArquivoDe(id)) ? Results.NotFound()
        : Results.File(proc.ArquivoDe(id), "audio/wav", $"ligacao-{g.Inicio.ToLocalTime():yyyyMMdd-HHmm}-{new string(g.Numero.Where(char.IsDigit).ToArray())}.wav", enableRangeProcessing: true);
}).RequireAuthorization();

app.MapDelete("/api/gravacoes/{id:long}", (long id, ClaimsPrincipal u) =>
{
    if (MinhaGravacao(id, u) is null) return Results.NotFound();
    try { proc.Excluir(id); }
    catch (IOException ex) { return Results.Json(new { erro = ex.Message }, statusCode: 500); }
    return Results.Ok();
}).RequireAuthorization();

app.MapPost("/api/gravacoes/{id:long}/reprocessar", (long id, ClaimsPrincipal u) =>
{
    var g = MinhaGravacao(id, u);
    if (g is null) return Results.NotFound();
    banco.StatusDaGravacao(id, StatusGravacao.Fila);
    proc.Enfileirar(id);
    return Results.Ok(proc.Dto(banco.ObterGravacao(id)!));
}).RequireAuthorization();

// Antes de atender: o que eu já conversei com esse número (só as MINHAS gravações).
app.MapGet("/api/contexto", (string numero, ClaimsPrincipal u) =>
{
    var minhas = banco.GravacoesDe(u.Identity!.Name!, 1000).Where(g => ProcessadorGravacoes.MesmoNumero(g.Numero, numero)).ToList();
    var anteriores = minhas.Where(g => g.Status == StatusGravacao.Pronta).Take(3)
        .Select(g => { var r = ProcessadorGravacoes.LerResumo(g.Resumo); return new ContextoLigacaoDto(g.Id, g.Inicio, r?.Assunto, r?.Resumo); })
        .ToArray();
    var quem = minhas.Select(g => ProcessadorGravacoes.LerResumo(g.Resumo)?.QuemLigou).FirstOrDefault(q => !string.IsNullOrWhiteSpace(q))
               ?? minhas.Select(g => g.Nome).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));
    return Results.Ok(new ContextoNumeroDto(numero, quem, anteriores));
}).RequireAuthorization();

// painel web de administração (/admin/), só arquivos estáticos; a página usa a API acima
PainelWeb.Mapear(app);

app.MapHub<ChatHub>(HubNomes.Rota);

app.Logger.LogInformation("Softphone Hub {Versao} | dados em {Dados} | LDAP: {Ldap} | transcrição: {T} | resumo IA: {R} ({Modelo})", Versao, op.Dados,
    dir.Configurado ? op.Ldap.Servidor : "desligado", transcritor.Configurado ? op.Ia.WhisperUrl : "desligada", assistente.Configurado ? "ligado" : "desligado", op.Ia.Modelo);
app.Run();

/// <summary>No SignalR, o "usuário" é o login (claim sub), para mandar mensagem para uma pessoa em todas as conexões dela.</summary>
sealed class LoginComoId : IUserIdProvider
{
    public string? GetUserId(HubConnectionContext connection) => connection.User?.FindFirst("sub")?.Value;
}
