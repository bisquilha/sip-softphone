using System.Reflection;

namespace Softphone.Hub;

/// <summary>
/// Painel web de administração em /admin/: três arquivos estáticos embutidos no Hub (HTML, CSS, JS sem framework).
/// A página só usa a API que já existe (/api/entrar, /api/entrar-senha, /api/admin/*) com o token no cabeçalho
/// Authorization; nada de cookie. Toda resposta daqui leva cabeçalhos que proíbem script de fora, iframe e cache.
/// </summary>
public static class PainelWeb
{
    private const string Csp = "default-src 'self'; img-src 'self' data:; style-src 'self'; script-src 'self'; connect-src 'self'; " +
                               "frame-ancestors 'none'; base-uri 'none'; form-action 'self'";

    public static void Mapear(WebApplication app)
    {
        var html = Ler("Painel/index.html");
        var css = Ler("Painel/painel.css");
        var js = Ler("Painel/painel.js");
        // "/admin" e "/admin/" são a MESMA rota para o roteador (barra final ignorada): uma regra só, que olha o caminho
        app.MapGet("/admin", (HttpContext ctx) =>
        {
            Cabecalhos(ctx.Response);
            return ctx.Request.Path.Value!.EndsWith('/') ? Results.Bytes(html, "text/html; charset=utf-8") : Results.Redirect("/admin/");
        });
        app.MapGet("/admin/painel.css", (HttpContext ctx) => { Cabecalhos(ctx.Response); return Results.Bytes(css, "text/css; charset=utf-8"); });
        app.MapGet("/admin/painel.js", (HttpContext ctx) => { Cabecalhos(ctx.Response); return Results.Bytes(js, "text/javascript; charset=utf-8"); });
        // 1.3: Visão geral por módulos, Artigos, Auditoria e copiloto; logo oficial da empresa
        var js13 = Ler("Painel/painel-v13.js");
        var logo = Ler("Painel/logo.png");
        app.MapGet("/admin/painel-v13.js", (HttpContext ctx) => { Cabecalhos(ctx.Response); return Results.Bytes(js13, "text/javascript; charset=utf-8"); });
        app.MapGet("/admin/logo.png", (HttpContext ctx) => { Cabecalhos(ctx.Response); ctx.Response.Headers["Cache-Control"] = "public, max-age=86400"; return Results.Bytes(logo, "image/png"); });
    }

    private static byte[] Ler(string recurso)
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(recurso)
                      ?? throw new InvalidOperationException("Arquivo do painel não embutido: " + recurso);
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    private static void Cabecalhos(HttpResponse r)
    {
        r.Headers["Content-Security-Policy"] = Csp;
        r.Headers["X-Frame-Options"] = "DENY";
        r.Headers["X-Content-Type-Options"] = "nosniff";
        r.Headers["Referrer-Policy"] = "no-referrer";
        r.Headers["Cache-Control"] = "no-store";
    }
}
