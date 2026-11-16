using System.Net;
using System.Text;
using System.Text.Json;

/// <summary>
/// Imita, só para testes, o whisper-asr-webservice (/asr) e a API da Anthropic (/v1/messages), para testar a fila de IA
/// do Hub de ponta a ponta sem gastar nada. O Hub de teste sobe com Hub__Ia__WhisperUrl e Hub__Ia__AnthropicUrl apontando aqui.
/// "Transcrição": acha os trechos com som (janelas de 0,25 s) e devolve "fala de teste X,X" com o início do trecho,
/// mais uma frase inventada típica ("Legendas pela comunidade Amara.org") num trecho mudo, que o Hub tem que descartar.
/// </summary>
static class ServidorFalsoIa
{
    public const string Url = "http://localhost:5299";
    public static volatile string? UltimoPedido;
    public static volatile string? UltimoBeta;
    public static volatile string? UltimaChave;
    public static volatile string? Cabecalhos;
    public static int Transcricoes;
    public static volatile string? UltimoPedidoOpenAi;
    public static volatile string? UltimaChaveOpenAi;
    /// <summary>Pedidos recebidos por "IA" no formato OpenAI (primeira parte do caminho: oa1, oa2, oafalha, oasemformato).</summary>
    public static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> ChamadasOpenAi = new();
    public static int Chamadas(string nome) => ChamadasOpenAi.TryGetValue(nome, out var n) ? n : 0;
    private static HttpListener? _l;

    public static void Iniciar()
    {
        if (_l != null) return;
        _l = new HttpListener();
        _l.Prefixes.Add(Url + "/");
        _l.Start();
        _ = Task.Run(async () =>
        {
            while (_l.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await _l.GetContextAsync(); } catch { break; }
                _ = Task.Run(() => Atender(ctx));
            }
        });
    }

    private static void Atender(HttpListenerContext ctx)
    {
        try
        {
            using var ms = new MemoryStream();
            ctx.Request.InputStream.CopyTo(ms);
            var corpo = ms.ToArray();
            string resposta;
            var caminho = ctx.Request.Url!.AbsolutePath;
            if (caminho == "/asr") { Interlocked.Increment(ref Transcricoes); resposta = Transcrever(corpo); }
            else if (caminho == "/v1/models")
                resposta = JsonSerializer.Serialize(new { data = new[] { new { id = "claude-teste-1", type = "model" } } });
            else if (caminho.EndsWith("/models"))
            {
                // 1.2.5: "Buscar modelos" dos provedores compatíveis com OpenAI
                if (ctx.Request.Headers["Authorization"] is not { Length: > 8 }) { Responder(ctx, 401, "{\"error\":{\"message\":\"sem chave\"}}"); return; }
                resposta = JsonSerializer.Serialize(new { @object = "list", data = new[] { new { id = "modelo-a" }, new { id = "modelo-b" } } });
            }
            else if (caminho.EndsWith("/chat/completions"))
            {
                // 1.2.5: IAs no formato OpenAI. /oafalha sempre dá 500; /oasemformato recusa response_format; as outras respondem.
                var pedido = Encoding.UTF8.GetString(corpo);
                var nome = caminho.Split('/', StringSplitOptions.RemoveEmptyEntries)[0];
                ChamadasOpenAi.AddOrUpdate(nome, 1, (_, n) => n + 1);
                if (nome == "oafalha") { Responder(ctx, 500, "{\"error\":{\"message\":\"falha simulada com a chave sk-falha-123456789\"}}"); return; }
                if (nome == "oasemformato" && pedido.Contains("response_format")) { Responder(ctx, 400, "{\"error\":{\"message\":\"response_format is not supported\"}}"); return; }
                UltimoPedidoOpenAi = pedido;
                UltimaChaveOpenAi = ctx.Request.Headers["Authorization"]?.Replace("Bearer ", "");
                resposta = JsonSerializer.Serialize(new
                {
                    id = "chatcmpl-teste", @object = "chat.completion", model = "modelo-a",
                    choices = new[] { new { index = 0, finish_reason = "stop", message = new { role = "assistant", content = RespostaJson(pedido) } } },
                });
            }
            else if (ctx.Request.Url.AbsolutePath.StartsWith("/v1/messages"))
            {
                var pedido = Encoding.UTF8.GetString(corpo);
                UltimoPedido = pedido;
                UltimoBeta = ctx.Request.Headers["anthropic-beta"];
                UltimaChave = ctx.Request.Headers["x-api-key"] ?? ctx.Request.Headers["Authorization"]?.Replace("Bearer ", "");
                Cabecalhos = string.Join("; ", ctx.Request.Headers.AllKeys.Select(k => k + "=" + (k!.Contains("key", StringComparison.OrdinalIgnoreCase) || k.Contains("auth", StringComparison.OrdinalIgnoreCase) ? ctx.Request.Headers[k]?.Length.ToString() + " chars" : ctx.Request.Headers[k])));
                string json = RespostaJson(pedido);
                resposta = JsonSerializer.Serialize(new
                {
                    id = "msg_teste", type = "message", role = "assistant", model = "claude-opus-5-5",
                    content = new[] { new { type = "text", text = json } },
                    stop_reason = "end_turn", stop_sequence = (string?)null,
                    usage = new { input_tokens = 100, output_tokens = 50, cache_creation_input_tokens = 0, cache_read_input_tokens = 0 },
                });
            }
            else { ctx.Response.StatusCode = 404; ctx.Response.Close(); return; }
            var bytes = Encoding.UTF8.GetBytes(resposta);
            ctx.Response.ContentType = "application/json";
            ctx.Response.OutputStream.Write(bytes);
            ctx.Response.Close();
        }
        catch (Exception ex)
        {
            Console.WriteLine("    servidor falso: " + ex.Message);
            try { ctx.Response.StatusCode = 500; ctx.Response.Close(); } catch { }
        }
    }

    private static string RespostaJson(string pedido) => pedido.Contains("proximos_passos")
        ? JsonSerializer.Serialize(new
        {
            assunto = "Assunto de teste",
            resumo = "Cliente pediu orçamento e ficou de mandar o desenho.",
            proximos_passos = new[] { "Cliente envia o desenho até sexta", "Ana manda o orçamento" },
            quem_ligou = "Cliente Teste Ltda",
            sentimento = "positivo",
            sugestao_resposta = "Olá! Conforme combinado, aguardo o desenho para enviar o orçamento.",
        })
        : pedido.Contains("Teste de conex")
        ? JsonSerializer.Serialize(new { ok = true, frase = "Conexão ok." })
        : JsonSerializer.Serialize(new { texto = "Sugestão de teste: pode deixar, eu verifico." });

    private static void Responder(HttpListenerContext ctx, int status, string json)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json";
        var b = Encoding.UTF8.GetBytes(json);
        ctx.Response.OutputStream.Write(b);
        ctx.Response.Close();
    }

    private static string Transcrever(byte[] multipart)
    {
        // acha o WAV dentro do multipart e lê o PCM mono 16 bits
        int riff = IndexOf(multipart, "RIFF"u8.ToArray());
        int data = IndexOf(multipart, "data"u8.ToArray(), riff);
        int taxa = BitConverter.ToInt32(multipart, riff + 24);
        int ini = data + 8;
        int n = (multipart.Length - ini) / 2;
        int janela = taxa / 4;
        var segmentos = new List<object>();
        double? comeco = null;
        double dur = (double)n / taxa;
        for (int w = 0; w * janela < n; w++)
        {
            double soma = 0; int k = 0;
            for (int i = w * janela; i < Math.Min(n, (w + 1) * janela); i++, k++) { double v = BitConverter.ToInt16(multipart, ini + i * 2); soma += v * v; }
            bool som = k > 0 && Math.Sqrt(soma / k) > 150;
            double t = w * 0.25;
            if (som && comeco == null) comeco = t;
            if (!som && comeco != null) { segmentos.Add(Seg(comeco.Value, t)); comeco = null; }
        }
        if (comeco != null) segmentos.Add(Seg(comeco.Value, dur));
        segmentos.Add(new { id = 99, start = Math.Max(0, dur - 0.4), end = dur, text = " Legendas pela comunidade Amara.org" });
        return JsonSerializer.Serialize(new { text = "teste", segments = segmentos, language = "pt" });

        static object Seg(double a, double b) => new { id = 0, start = a, end = b, text = $" fala de teste {a:0.0}" };
    }

    private static int IndexOf(byte[] h, byte[] agulha, int desde = 0)
    {
        for (int i = Math.Max(0, desde); i <= h.Length - agulha.Length; i++)
        {
            int j = 0;
            while (j < agulha.Length && h[i + j] == agulha[j]) j++;
            if (j == agulha.Length) return i;
        }
        return -1;
    }

    /// <summary>WAV estéreo 8 kHz de 3 s: eu falo em 0–1 s; o outro lado fala em 1,5–2,5 s e tem um eco baixo da minha fala em 0–1 s.</summary>
    public static string GerarWavLigacao(string caminho)
    {
        const int Taxa = 8000; int n = Taxa * 3;
        using var fs = File.Create(caminho);
        using var w = new BinaryWriter(fs);
        w.Write("RIFF"u8); w.Write(36 + n * 4); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)2); w.Write(Taxa); w.Write(Taxa * 4); w.Write((short)4); w.Write((short)16);
        w.Write("data"u8); w.Write(n * 4);
        for (int i = 0; i < n; i++)
        {
            double t = (double)i / Taxa;
            double tom = Math.Sin(2 * Math.PI * 300 * t);
            short eu = (short)(t < 1.0 ? tom * 8000 : 0);
            short outro = (short)(t >= 1.5 && t < 2.5 ? tom * 8000 : t < 1.0 ? tom * 300 : 0);
            w.Write(eu); w.Write(outro);
        }
        return caminho;
    }
}
