using System.Net;
using System.Net.Http.Json;
using Softphone.Contratos;
using Softphone.Core;

/// <summary>
/// Cliente do Hub com a rede de mentira (sem servidor): reconexão, token salvo, renovação do token e download
/// interrompido. Claude Code, 05/11/2026 — achados R02, R03, R08 e R16 da revisão do Codex (docs/REVISAO-CODIGO-CODEX-2026-11-05.md).
/// </summary>
static class TesteHubCliente
{
    sealed class RedeFalsa : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Responder = _ => throw new HttpRequestException("sem rede");
        public int Pedidos;
        public readonly List<string> Caminhos = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Interlocked.Increment(ref Pedidos);
            lock (Caminhos) Caminhos.Add($"{r.Method} {r.RequestUri!.AbsolutePath}");
            return Task.FromResult(Responder(r));
        }
    }

    /// <summary>Corpo que entrega uns bytes e cai no meio (conexão interrompida).</summary>
    sealed class CorpoQueCai : Stream
    {
        int _lidos;
        public override int Read(byte[] b, int o, int n) { if (_lidos >= 1000) throw new IOException("conexão caiu"); int k = Math.Min(n, 1000 - _lidos); _lidos += k; return k; }
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => _lidos; set => throw new NotSupportedException(); }
        public override void Flush() { } public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException(); public override void Write(byte[] b, int o, int n) => throw new NotSupportedException();
    }

    static EntrarResposta Resposta(string token) => new(token, DateTime.UtcNow.AddDays(30),
        new PessoaDto("ana.teste", "Ana Teste", "TI", "HQ", null, "4321", EstadoPresenca.Disponivel, null, null), "1.2.1", true);

    public static async Task RodarAsync(Action<bool, string> ok)
    {
        // R02: com o Hub fora do ar, continua tentando de tempos em tempos (antes parava depois da 2ª falha)
        {
            var rede = new RedeFalsa();
            var c = new HubCliente("http://hub.teste", rede) { IntervaloNovaTentativa = TimeSpan.FromMilliseconds(100) };
            await c.IniciarAsync();
            await Task.Delay(900);
            int durante = rede.Pedidos;
            ok(durante >= 4 && c.Estado == EstadoHub.Falha, $"Hub fora do ar: continua tentando ({durante} tentativas em 0,9 s, a cada 0,1 s)");
            await c.DisposeAsync();
            int aoFechar = rede.Pedidos;
            await Task.Delay(400);
            ok(rede.Pedidos == aoFechar, $"depois de fechar o cliente, para de tentar ({aoFechar} -> {rede.Pedidos})");
        }

        // R03: login do Windows recusado + token salvo válido -> troca o token e volta com quem sou eu, versão e admin
        {
            var rede = new RedeFalsa();
            rede.Responder = r => r.RequestUri!.AbsolutePath switch
            {
                "/api/entrar" => new HttpResponseMessage(HttpStatusCode.Unauthorized),
                "/api/renovar" when r.Headers.Authorization?.Parameter == "token-salvo" => new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(Resposta("token-novo")) },
                _ => throw new HttpRequestException("sem rede"),
            };
            await using var c = new HubCliente("http://hub.teste", rede) { IntervaloNovaTentativa = TimeSpan.FromHours(1) };
            string? renovado = null;
            c.TokenRenovado += (t, _) => renovado = t;
            c.UsarTokenSalvo("token-salvo", DateTime.UtcNow.AddDays(10));
            await c.IniciarAsync(); // o SignalR falha (não há servidor), mas o login já tem que ter vindo completo
            ok(c.Eu?.Login == "ana.teste" && c.VersaoServidor == "1.2.1" && c.Admin && renovado == "token-novo",
                $"token salvo: volta com quem sou eu, versão e admin (eu={c.Eu?.Login}, versão={c.VersaoServidor}, admin={c.Admin}, token novo={renovado})");
        }

        // R03 com Hub 1.2.0 (sem /api/renovar): usa /api/eu e /saude
        {
            var rede = new RedeFalsa();
            rede.Responder = r => r.RequestUri!.AbsolutePath switch
            {
                "/api/entrar" => new HttpResponseMessage(HttpStatusCode.Unauthorized),
                "/api/renovar" => new HttpResponseMessage(HttpStatusCode.NotFound),
                "/api/eu" => new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(Resposta("x").Eu) },
                "/saude" => new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { versao = "1.2.0" }) },
                _ => throw new HttpRequestException("sem rede"),
            };
            await using var c = new HubCliente("http://hub.teste", rede) { IntervaloNovaTentativa = TimeSpan.FromHours(1) };
            c.UsarTokenSalvo("token-salvo", DateTime.UtcNow.AddDays(10));
            await c.IniciarAsync();
            ok(c.Eu?.Login == "ana.teste" && c.VersaoServidor == "1.2.0", $"token salvo com Hub 1.2.0: quem sou eu e versão vêm de /api/eu e /saude (eu={c.Eu?.Login}, versão={c.VersaoServidor})");
        }

        // R08: token perto de vencer é trocado antes de usar (pedido HTTP ou reconexão do SignalR)
        {
            var rede = new RedeFalsa();
            rede.Responder = r => r.RequestUri!.AbsolutePath switch
            {
                "/api/entrar" => new HttpResponseMessage(HttpStatusCode.Unauthorized),
                "/api/renovar" => new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(Resposta("token-novo")) },
                "/api/ia/estado" => new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new EstadoIaDto(true, true, true, 90, 400)) },
                _ => throw new HttpRequestException("sem rede"),
            };
            await using var c = new HubCliente("http://hub.teste", rede);
            c.UsarTokenSalvo("token-velho", DateTime.UtcNow.AddHours(1).AddMinutes(1)); // UsarTokenSalvo só aceita com mais de 1 h
            c.ForcarExpiracaoParaTeste(DateTime.UtcNow.AddMinutes(3));
            await c.EstadoIaAsync();
            var token = await c.TokenParaConexaoAsync();
            ok(rede.Caminhos.Contains("POST /api/renovar") && token == "token-novo", $"token perto de vencer é renovado antes do pedido ({string.Join(", ", rede.Caminhos)})");
        }

        // R16: download que cai no meio não deixa arquivo pela metade (que depois tocaria como se estivesse inteiro)
        {
            var rede = new RedeFalsa();
            rede.Responder = r =>
            {
                var resp = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new CorpoQueCai()) };
                resp.Content.Headers.ContentLength = 5000;
                return resp;
            };
            await using var c = new HubCliente("http://hub.teste", rede);
            c.UsarTokenSalvo("token", DateTime.UtcNow.AddDays(10));
            var pasta = Path.Combine(Path.GetTempPath(), "softphone-teste-download-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(pasta);
            var destino = Path.Combine(pasta, "12.wav");
            bool falhou = false;
            try { await c.BaixarAudioGravacaoAsync(12, destino); } catch { falhou = true; }
            var sobras = Directory.GetFiles(pasta);
            ok(falhou && !File.Exists(destino) && sobras.Length == 0, $"download interrompido: dá erro e não deixa arquivo ({sobras.Length} arquivo(s) na pasta)");
            Directory.Delete(pasta, true);
        }
    }
}
