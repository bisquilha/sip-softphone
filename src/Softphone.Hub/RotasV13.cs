using System.Security.Claims;
using System.Text;
using Softphone.Contratos;
using Microsoft.AspNetCore.SignalR;

namespace Softphone.Hub;

/// <summary>
/// 1.3 (Claude Code, 10/11/2026): auditoria, chamadas, resumo operacional do painel e automações do chat.
/// 1.3.3 (11/11/2026, pedido do Ana): artigos, anotações, legenda ao vivo e copiloto saíram do Softphone (vão para a ferramenta nova de vendas).
/// </summary>
public static class RotasV13
{
    public static void Mapear(WebApplication app)
    {
        var op = app.Services.GetRequiredService<OpcoesHub>();
        var banco = app.Services.GetRequiredService<Banco>();
        var pres = app.Services.GetRequiredService<Presencas>();
        var assistente = app.Services.GetRequiredService<Assistente>();
        var transcritor = app.Services.GetRequiredService<Transcritor>();
        var acessoPerfis = app.Services.GetRequiredService<AcessoPerfis>();
        var automacoes = app.Services.GetRequiredService<Automacoes>();
        var log = app.Logger;

        string Eu(ClaimsPrincipal u) => Diretorio.NormalizarLogin(u.Identity?.Name ?? "");
        bool Admin(ClaimsPrincipal u) => op.EhAdmin(Eu(u));
        IResult Proibido() => Results.Json(new { erro = "Só a TI (administradores do Softphone) acessa isto." }, statusCode: 403);

        // ---------------- chamadas e resumo operacional ----------------
        app.MapPost("/api/chamadas", (ChamadaRegistroDto c, ClaimsPrincipal u) =>
        {
            if (string.IsNullOrWhiteSpace(c.Id) || c.Id.Length > 64 || c.Numero.Length > 40 || c.DuracaoSegundos is < 0 or > 86400
                || c.Direcao is not ("entrada" or "saida") || c.Resultado.Length is 0 or > 30)
                return Results.BadRequest(new { erro = "Registro de chamada inválido." });
            var login = Eu(u);
            var p = banco.ObterPessoa(login);
            if (c.Resultado == "Perdida" && c.Direcao == "entrada") _ = automacoes.AoChamadaPerdidaAsync(login, c.Id, c.Numero);   // 1.3: regras "chamada perdida"
            banco.InserirChamada(login, p?.Unidade, p?.Setor, c with { Numero = new string(c.Numero.Where(ch => char.IsDigit(ch) || ch is '*' or '#' or '+').ToArray()), Motivo = c.Motivo is { Length: > 200 } m ? m[..200] : c.Motivo });
            return Results.Ok();
        }).RequireAuthorization();

        static DateTime Inicio(string? periodo) => periodo switch
        {
            "7d" => DateTime.Now.Date.AddDays(-6),
            "30d" => DateTime.Now.Date.AddDays(-29),
            _ => DateTime.Now.Date,
        };

        var g = app.MapGroup("/api/admin").RequireAuthorization();

        g.MapGet("/resumo", (string? periodo, ClaimsPrincipal u) =>
        {
            if (!Admin(u)) return Proibido();
            periodo = periodo is "7d" or "30d" ? periodo : "hoje";
            var desde = Inicio(periodo);
            var ch = banco.ContagemChamadas(desde);
            var at = banco.ContagemAtendimento(desde);
            int erros = banco.ListarAuditoria(DateTime.Now.AddHours(-24), null, null, null, null, 5000).Count(a => a.Status >= 400);
            return Results.Ok(new ResumoOperacionalDto(periodo, DateTime.UtcNow, ch.Rec, ch.Real, ch.Perd, ch.Recu, ch.Falh, ch.Transf, ch.Medio, ch.Total,
                pres.Online, banco.ListarPessoas().Count(), erros, at.Artigos, at.Consultas, at.Anotacoes, at.Perguntas, at.SemResposta, banco.HaChamadasRegistradas()));
        });

        g.MapGet("/chamadas", (string? periodo, string? resultado, string? direcao, string? unidade, string? setor, int? transferida, ClaimsPrincipal u) =>
            Admin(u) ? Results.Ok(banco.ListarChamadas(Inicio(periodo is "7d" or "30d" ? periodo : "hoje"), resultado, direcao, unidade, setor, transferida == 1 ? true : null, 500)) : Proibido());

        // ---------------- auditoria ----------------
        g.MapGet("/auditoria", (DateTime? de, DateTime? ate, string? usuario, string? acao, string? q, int? limite, string? gravidade, string? ramal, ClaimsPrincipal u) =>
            Admin(u) ? Results.Ok(banco.ListarAuditoria(de, ate, usuario, acao, q, limite ?? 500, gravidade, ramal)) : Proibido());

        g.MapGet("/auditoria.csv", (DateTime? de, DateTime? ate, string? usuario, string? acao, string? q, string? gravidade, string? ramal, ClaimsPrincipal u) =>
        {
            if (!Admin(u)) return Proibido();
            var sb = new StringBuilder("﻿data;usuario;acao;alvo;status;detalhe\r\n");
            static string Celula(string? s)
            {
                s ??= "";
                if (s.Length > 0 && s[0] is '=' or '+' or '-' or '@') s = "'" + s;   // evita fórmula ao abrir no Excel
                return "\"" + s.Replace("\"", "\"\"") + "\"";
            }
            foreach (var a in banco.ListarAuditoria(de, ate, usuario, acao, q, 5000, gravidade, ramal))
                sb.Append(Celula(a.Em.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"))).Append(';').Append(Celula(a.Usuario)).Append(';').Append(Celula(a.Acao)).Append(';')
                  .Append(Celula(a.Alvo)).Append(';').Append(a.Status).Append(';').Append(Celula(a.Detalhe)).Append("\r\n");
            banco.Auditar(Eu(u), "EXPORTAR", "/api/admin/auditoria.csv", 200);
            return Results.File(Encoding.UTF8.GetBytes(sb.ToString()), "text/csv; charset=utf-8", $"auditoria-{DateTime.Now:yyyyMMdd-HHmm}.csv");
        });

        // ---------------- automações do chat (regras de gatilho e ação) ----------------
        g.MapGet("/automacoes", (ClaimsPrincipal u) => Admin(u) ? Results.Ok(automacoes.Regras()) : Proibido());
        g.MapPut("/automacoes", (RegraAutomacao[] regras, ClaimsPrincipal u) =>
        {
            if (!Admin(u)) return Proibido();
            var erro = automacoes.Gravar(regras ?? []);
            return erro == null ? Results.Ok(automacoes.Regras()) : Results.BadRequest(new { erro });
        });
        g.MapGet("/automacoes/execucoes", (ClaimsPrincipal u) => Admin(u) ? Results.Ok(automacoes.Execucoes()) : Proibido());

    }
}

public static class RotasAcesso
{
    private static readonly string[] Niveis = { NivelComunicado.Info, NivelComunicado.Importante, NivelComunicado.Urgente };

    public static void Mapear(WebApplication app)
    {
        var op = app.Services.GetRequiredService<OpcoesHub>();
        var banco = app.Services.GetRequiredService<Banco>();
        var acesso = app.Services.GetRequiredService<AcessoPerfis>();
        var grupos = app.Services.GetRequiredService<GruposAdmin>();
        var hub = app.Services.GetRequiredService<Microsoft.AspNetCore.SignalR.IHubContext<ChatHub>>();
        var pres = app.Services.GetRequiredService<Presencas>();
        var log = app.Logger;

        string Eu(ClaimsPrincipal u) => Diretorio.NormalizarLogin(u.Identity?.Name ?? "");
        bool Admin(ClaimsPrincipal u) => op.EhAdmin(Eu(u));
        IResult Proibido(string? m = null) => Results.Json(new { erro = m ?? "Só a TI (administradores do Softphone) acessa isto." }, statusCode: 403);

        // matriz grupo do AD -> perfil -> setores
        var g = app.MapGroup("/api/admin/acesso").RequireAuthorization();
        g.MapGet("", (ClaimsPrincipal u) => Admin(u) ? Results.Ok(acesso.Regras()) : Proibido());
        g.MapPut("", (RegraAcessoDto[] regras, ClaimsPrincipal u) =>
        {
            if (!Admin(u)) return Proibido();
            var erro = acesso.Gravar(regras ?? []);
            return erro == null ? Results.Ok(acesso.Regras()) : Results.BadRequest(new { erro });
        });
        g.MapGet("/consulta", (string login, ClaimsPrincipal u) => Admin(u) ? Results.Ok(acesso.De(login)) : Proibido());
        app.MapGet("/api/eu/perfil", (ClaimsPrincipal u) => Results.Ok(acesso.De(Eu(u)))).RequireAuthorization();

        // comunicado por unidade, setores e/ou grupo: alcance antes de enviar, ciência opcional, quem pode enviar para quem
        (List<Banco.Pessoa>? Pessoas, string? Erro) Publico(NovoComunicadoV13Dto n, PerfilEfetivoDto perfil)
        {
            var setores = (n.Setores ?? []).Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();
            List<string>? logins = null;
            if (!string.IsNullOrWhiteSpace(n.Grupo))
            {
                var (l, erro) = grupos.Ler(n.Grupo.Trim());
                if (l == null) return (null, $"Grupo {n.Grupo}: {erro}.");
                logins = l.Select(Diretorio.NormalizarLogin).ToList();
            }
            if (perfil.Perfil == AcessoPerfis.Supervisor)
            {
                // supervisor só fala com o(s) próprio(s) setor(es): precisa escolher setor e não pode abrir para a empresa, unidade ou grupo
                if (setores.Length == 0) return (null, "Supervisor precisa escolher o setor (só o seu).");
                if (!setores.All(s => perfil.Setores.Contains(s, StringComparer.OrdinalIgnoreCase))) return (null, "Supervisor só envia para o próprio setor: " + string.Join(", ", perfil.Setores) + ".");
                if (!string.IsNullOrWhiteSpace(n.Unidade) || logins != null) return (null, "Supervisor não envia por unidade ou grupo.");
            }
            return (banco.PublicoDoComunicado(n.Unidade, setores, logins), null);
        }

        app.MapPost("/api/comunicados/alcance", (NovoComunicadoV13Dto n, ClaimsPrincipal u) =>
        {
            var perfil = acesso.De(Eu(u));
            if (perfil.Perfil == AcessoPerfis.Atendente) return Proibido("Seu perfil não envia comunicados.");
            var (pessoas, erro) = Publico(n, perfil);
            if (pessoas == null) return Results.BadRequest(new AlcanceComunicadoDto(0, [], false, erro));
            bool todos = string.IsNullOrWhiteSpace(n.Unidade) && (n.Setores?.Length ?? 0) == 0 && string.IsNullOrWhiteSpace(n.Grupo);
            return Results.Ok(new AlcanceComunicadoDto(pessoas.Count, pessoas.Take(8).Select(p => p.Nome).ToArray(), todos, null));
        }).RequireAuthorization();

        app.MapPost("/api/comunicados/enviar", async (NovoComunicadoV13Dto n, ClaimsPrincipal u) =>
        {
            var perfil = acesso.De(Eu(u));
            if (perfil.Perfil == AcessoPerfis.Atendente) return Proibido("Seu perfil não envia comunicados.");
            if (string.IsNullOrWhiteSpace(n.Titulo) || n.Titulo.Trim().Length > 100) return Results.BadRequest(new { erro = "Título de 1 a 100 caracteres." });
            if (string.IsNullOrWhiteSpace(n.Texto) || n.Texto.Trim().Length > 2000) return Results.BadRequest(new { erro = "Texto de 1 a 2000 caracteres." });
            if (!Niveis.Contains(n.Nivel)) return Results.BadRequest(new { erro = "Nível inválido." });
            var (pessoas, erro) = Publico(n, perfil);
            if (pessoas == null) return Results.BadRequest(new { erro });
            if (pessoas.Count == 0) return Results.BadRequest(new { erro = "Ninguém recebe com esses filtros." });
            bool todos = string.IsNullOrWhiteSpace(n.Unidade) && (n.Setores?.Length ?? 0) == 0 && string.IsNullOrWhiteSpace(n.Grupo);
            var rotulo = todos ? "Todos" : string.Join(" · ", new[] { n.Unidade, (n.Setores?.Length ?? 0) > 0 ? "setores: " + string.Join(", ", n.Setores!) : null, string.IsNullOrWhiteSpace(n.Grupo) ? null : "grupo " + n.Grupo }.Where(x => !string.IsNullOrWhiteSpace(x)));
            var c = banco.InserirComunicadoV13(n, Eu(u), todos ? null : pessoas.Select(p => p.Login), rotulo);
            foreach (var p in pessoas)
            {
                await hub.Clients.Group("u:" + p.Login.ToLowerInvariant()).SendAsync(HubNomes.EvComunicado, c);
                if (pres.De(p.Login) != EstadoPresenca.Offline) banco.MarcarEntregue(c.Id, p.Login);   // entregue = enviado a um app conectado
            }
            log.LogInformation("Comunicado {Id} ({Nivel}) de {Eu} ({Perfil}) para {Para} ({N} pessoas)", c.Id, c.Nivel, Eu(u), perfil.Perfil, rotulo, pessoas.Count);
            return Results.Ok(c);
        }).RequireAuthorization();
    }
}
