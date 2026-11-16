using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Anthropic;
using Anthropic.Models.Beta.Messages;
using Softphone.Contratos;
using Microsoft.AspNetCore.SignalR;

namespace Softphone.Hub;

/// <summary>
/// IA do Softphone (decisão de 04/11: "misto"): transcrição LOCAL no softphone-server (contêiner whisper, o áudio não sai da rede)
/// e resumo/sugestões pela API da Anthropic (só o TEXTO vai para fora). Gravação só quando o usuário clica,
/// com aviso falado para o outro lado; tudo apagado depois de RetencaoDias (90).
/// </summary>
public sealed class OpcoesIa
{
    /// <summary>Serviço de transcrição (onerahmet/openai-whisper-asr-webservice, faster_whisper). Vazio = sem transcrição.</summary>
    public string WhisperUrl { get; set; } = "";
    public string Idioma { get; set; } = "pt";
    /// <summary>Chave da API da Anthropic (vai no hub.env, quem coloca é a TI). Vazio = sem resumo/sugestões.</summary>
    public string AnthropicChave { get; set; } = "";
    /// <summary>Endereço da API (vazio = padrão). Só os testes trocam.</summary>
    public string AnthropicUrl { get; set; } = "";
    public string Modelo { get; set; } = "claude-opus-5-5";
    public string Empresa { get; set; } = "Minha Empresa";
    public int RetencaoDias { get; set; } = 90;
    /// <summary>3 h de ligação em 8 kHz estéreo ≈ 345 MB.</summary>
    public int GravacaoMaxMb { get; set; } = 400;
    /// <summary>1.2.5: provedores de IA em uso (montados pelo ConfigServidor a partir do painel; chave já aberta).</summary>
    public IReadOnlyList<ProvedorAtivo> Provedores { get; set; } = [];
    /// <summary>"ordem" ou "revezar".</summary>
    public string ModoProvedores { get; set; } = "ordem";
}

/// <summary>1.2.5: um provedor de IA pronto para uso (só em memória: a chave vem aberta).</summary>
public sealed record ProvedorAtivo(string Id, string Nome, string Tipo, string Url, string Modelo, string Chave, bool Ligado, string Uso = "tudo")
{
    public const string UrlAnthropic = "https://api.anthropic.com";

    /// <summary>Endereço padrão de cada tipo (o painel preenche; "outra" exige o endereço).</summary>
    public static string? UrlPadrao(string tipo) => tipo switch
    {
        TipoProvedorIa.Anthropic => UrlAnthropic,
        TipoProvedorIa.OpenAi => "https://api.openai.com/v1",
        TipoProvedorIa.Gemini => "https://generativelanguage.googleapis.com/v1beta/openai",
        TipoProvedorIa.Xai => "https://api.x.ai/v1",
        TipoProvedorIa.DeepSeek => "https://api.deepseek.com/v1",
        _ => null,
    };

    public string ChaveFim => Chave.Length > 4 ? Chave[^4..] : "";
}

// ============================================================================================
// WAV: separa os canais (esquerdo = quem gravou, direito = o outro lado) e mede o volume de um trecho
// ============================================================================================

public sealed class WavCanais : IDisposable
{
    public int Taxa { get; private init; }
    public int Canais { get; private init; }
    public double Segundos { get; private init; }
    /// <summary>Um WAV mono por canal (arquivos temporários).</summary>
    public string[] Arquivos { get; private init; } = [];

    public static WavCanais Separar(string wav, string pastaTemp)
    {
        Directory.CreateDirectory(pastaTemp);
        using var fs = File.OpenRead(wav);
        using var r = new BinaryReader(fs);
        if (Encoding.ASCII.GetString(r.ReadBytes(4)) != "RIFF") throw new InvalidDataException("Arquivo não é WAV.");
        r.ReadInt32();
        if (Encoding.ASCII.GetString(r.ReadBytes(4)) != "WAVE") throw new InvalidDataException("Arquivo não é WAV.");
        int taxa = 0, canais = 0, bits = 0; long dados = -1;
        while (fs.Position + 8 <= fs.Length)
        {
            var id = Encoding.ASCII.GetString(r.ReadBytes(4)); long tam = r.ReadUInt32();
            if (id == "fmt ")
            {
                r.ReadInt16(); canais = r.ReadInt16(); taxa = r.ReadInt32(); r.ReadInt32(); r.ReadInt16(); bits = r.ReadInt16();
                if (tam > 16) fs.Seek(tam - 16, SeekOrigin.Current);
            }
            else if (id == "data") { dados = Math.Min(tam, fs.Length - fs.Position); break; }
            else fs.Seek(tam + (tam & 1), SeekOrigin.Current);
        }
        if (dados < 0 || bits != 16 || canais is < 1 or > 2 || taxa <= 0) throw new InvalidDataException("WAV precisa ser PCM 16 bits mono ou estéreo.");

        long quadros = dados / (2 * canais);
        var nomes = Enumerable.Range(0, canais).Select(i => Path.Combine(pastaTemp, $"{Guid.NewGuid():N}.c{i}.wav")).ToArray();
        var saidas = nomes.Select(n => new FileStream(n, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024)).ToArray();
        try
        {
            foreach (var s in saidas) Cabecalho(s, quadros, taxa);
            var buf = new byte[4096 * 2 * canais];
            var sep = saidas.Select(_ => new byte[4096 * 2]).ToArray();
            long restante = quadros * 2 * canais;
            while (restante > 0)
            {
                int n = fs.Read(buf, 0, (int)Math.Min(buf.Length, restante));
                if (n <= 0) break;
                n -= n % (2 * canais);
                int q = n / (2 * canais);
                for (int i = 0; i < q; i++)
                    for (int c = 0; c < canais; c++) { sep[c][i * 2] = buf[(i * canais + c) * 2]; sep[c][i * 2 + 1] = buf[(i * canais + c) * 2 + 1]; }
                for (int c = 0; c < canais; c++) saidas[c].Write(sep[c], 0, q * 2);
                restante -= n;
            }
        }
        finally { foreach (var s in saidas) s.Dispose(); }
        return new WavCanais { Taxa = taxa, Canais = canais, Segundos = (double)quadros / taxa, Arquivos = nomes };
    }

    private static void Cabecalho(Stream s, long quadros, int taxa)
    {
        using var w = new BinaryWriter(s, Encoding.ASCII, leaveOpen: true);
        long dados = quadros * 2;
        w.Write("RIFF"u8); w.Write((uint)Math.Min(uint.MaxValue, 36 + dados)); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(taxa); w.Write(taxa * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write((uint)Math.Min(uint.MaxValue, dados));
    }

    /// <summary>Volume (RMS) do canal entre <paramref name="ini"/> e <paramref name="fim"/> segundos.</summary>
    public double Volume(int canal, double ini, double fim)
    {
        using var fs = File.OpenRead(Arquivos[canal]);
        long a = Math.Clamp((long)(ini * Taxa), 0, (fs.Length - 44) / 2);
        long b = Math.Clamp((long)(fim * Taxa), a, (fs.Length - 44) / 2);
        if (b <= a) return 0;
        fs.Seek(44 + a * 2, SeekOrigin.Begin);
        var buf = new byte[8192]; double soma = 0; long n = 0, falta = (b - a) * 2;
        while (falta > 0)
        {
            int lidos = fs.Read(buf, 0, (int)Math.Min(buf.Length, falta));
            if (lidos <= 1) break;
            for (int i = 0; i + 1 < lidos; i += 2) { double v = BitConverter.ToInt16(buf, i); soma += v * v; n++; }
            falta -= lidos;
        }
        return n == 0 ? 0 : Math.Sqrt(soma / n);
    }

    public void Dispose() { foreach (var a in Arquivos) try { File.Delete(a); } catch { } }
}

// ============================================================================================
// Transcrição local (whisper-asr-webservice no próprio softphone-server)
// ============================================================================================

public sealed class Transcritor
{
    private readonly OpcoesIa _op;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromHours(2) };

    public Transcritor(OpcoesHub op) => _op = op.Ia;
    public bool Configurado => !string.IsNullOrWhiteSpace(_op.WhisperUrl);

    public async Task<List<(double Inicio, double Fim, string Texto)>> TranscreverAsync(string wavMono, CancellationToken ct)
    {
        using var form = new MultipartFormDataContent();
        await using var fs = File.OpenRead(wavMono);
        var parte = new StreamContent(fs);
        parte.Headers.ContentType = new("audio/wav");
        form.Add(parte, "audio_file", "audio.wav");
        // vad_filter: o faster-whisper pula o silêncio (menos "fala inventada" em trecho mudo)
        var url = $"{_op.WhisperUrl.TrimEnd('/')}/asr?output=json&task=transcribe&encode=true&vad_filter=true&language={Uri.EscapeDataString(_op.Idioma)}";
        using var resp = await _http.PostAsync(url, form, ct);
        var corpo = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"Transcrição falhou ({(int)resp.StatusCode}): {Curto(corpo)}");
        using var doc = JsonDocument.Parse(corpo);
        var lista = new List<(double, double, string)>();
        if (doc.RootElement.TryGetProperty("segments", out var segs) && segs.ValueKind == JsonValueKind.Array)
            foreach (var s in segs.EnumerateArray())
            {
                var texto = s.TryGetProperty("text", out var t) ? t.GetString()?.Trim() : null;
                if (string.IsNullOrWhiteSpace(texto)) continue;
                lista.Add((s.GetProperty("start").GetDouble(), s.GetProperty("end").GetDouble(), texto));
            }
        return lista;
    }

    internal static string Curto(string s) => s.Length > 300 ? s[..300] + "..." : s;
}

// ============================================================================================
// Resumo e sugestões (API da Anthropic, SDK oficial)
// ============================================================================================

public sealed class Assistente
{
    private readonly OpcoesIa _op;
    private readonly ILogger<Assistente> _log;

    public Assistente(OpcoesHub op, ILogger<Assistente> log)
    {
        _op = op.Ia;
        _log = log;
        Recriar();
    }

    // ---------- 1.2.5: vários provedores (Anthropic e compatíveis com OpenAI), em ordem ou revezando ----------

    /// <summary>Situação de cada provedor para o painel (só em memória; zera quando o Hub reinicia).</summary>
    public sealed class Situacao { public DateTime? UltimoOk; public DateTime? UltimaFalha; public string? UltimoErro; }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Situacao> _situacao = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, IAnthropicClient> _clientesAnthropic = new();
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private static readonly TimeSpan TempoPorIa = TimeSpan.FromMinutes(3);
    private int _vez = -1;

    /// <summary>Monta os clientes da lista atual das opções. O painel chama depois de qualquer mudança nos provedores.</summary>
    public void Recriar()
    {
        _clientesAnthropic.Clear();
        foreach (var p in _op.Provedores.Where(p => p.Tipo == TipoProvedorIa.Anthropic))
            _clientesAnthropic[p.Id] = ClienteAnthropic(p);
        foreach (var velho in _situacao.Keys.Where(k => _op.Provedores.All(p => p.Id != k)).ToList()) _situacao.TryRemove(velho, out _);
    }

    private static IAnthropicClient ClienteAnthropic(ProvedorAtivo p)
    {
        var url = p.Url.TrimEnd('/');
        return string.IsNullOrWhiteSpace(url) || url.Equals(ProvedorAtivo.UrlAnthropic, StringComparison.OrdinalIgnoreCase)
            ? new AnthropicClient { ApiKey = p.Chave, Timeout = TempoPorIa, MaxRetries = 1 }
            : new AnthropicClient { ApiKey = p.Chave, Timeout = TempoPorIa, MaxRetries = 1, BaseUrl = url };
    }

    private IReadOnlyList<ProvedorAtivo> Ligados => _op.Provedores.Where(p => p.Ligado).ToList();
    /// <summary>1.3: IAs ligadas e aprovadas para este tipo de dado ("chat" ou "ligacoes"; as de uso "tudo" servem para os dois).</summary>
    private IReadOnlyList<ProvedorAtivo> LigadosPara(string uso) => uso == "tudo" ? Ligados : _op.Provedores.Where(p => p.Ligado && (p.Uso == "tudo" || p.Uso == uso)).ToList();
    public bool Configurado => _op.Provedores.Any(p => p.Ligado);
    public Situacao SituacaoDe(string id) => _situacao.GetOrAdd(id, _ => new Situacao());

    /// <summary>Teste do painel pela cadeia inteira (como o resumo usaria).</summary>
    public async Task<string> TestarAsync(CancellationToken ct)
    {
        var (doc, p) = await PerguntarAsync(PedidoTeste, EsquemaTeste, 1000, ct);
        using (doc) return $"Respondeu: {p.Nome} ({p.Modelo}): {Frase(doc)}";
    }

    /// <summary>Teste de um provedor só (mesmo desligado), sem passar para o próximo.</summary>
    public async Task<string> TestarProvedorAsync(ProvedorAtivo p, CancellationToken ct)
    {
        using var doc = await PerguntarEmAsync(p, PedidoTeste, EsquemaTeste, 1000, ct);
        return $"{p.Nome} ({p.Modelo}) respondeu: {Frase(doc)}";
    }

    private const string PedidoTeste = "Teste de conexão do painel do Softphone: responda ok=true e uma frase curta de confirmação.";
    private static readonly Dictionary<string, JsonElement> EsquemaTeste =
        Esquema(new { ok = new { type = "boolean" }, frase = new { type = "string" } }, ["ok", "frase"]);
    private static string Frase(JsonDocument d) => d.RootElement.TryGetProperty("frase", out var f) ? f.GetString() ?? "ok" : "ok";

    /// <summary>Lista de modelos que a própria IA informa (o painel mostra para escolher). Só leitura.</summary>
    public static async Task<List<string>> BuscarModelosAsync(string tipo, string url, string chave, CancellationToken ct)
    {
        url = url.TrimEnd('/');
        using var req = new HttpRequestMessage(HttpMethod.Get, tipo == TipoProvedorIa.Anthropic ? $"{url}/v1/models?limit=100" : $"{url}/models");
        if (tipo == TipoProvedorIa.Anthropic) { req.Headers.Add("x-api-key", chave); req.Headers.Add("anthropic-version", "2023-06-01"); }
        else req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", chave);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(30));
        using var resp = await Http.SendAsync(req, cts.Token);
        var corpo = await resp.Content.ReadAsStringAsync(cts.Token);
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"A IA recusou ({(int)resp.StatusCode}): {Mascarar(Transcritor.Curto(corpo), chave)}");
        using var doc = JsonDocument.Parse(corpo);
        var lista = new List<string>();
        if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            foreach (var m in data.EnumerateArray())
                if (m.TryGetProperty("id", out var id) && id.GetString() is { Length: > 0 } s)
                    lista.Add(s.StartsWith("models/") ? s["models/".Length..] : s); // Gemini devolve "models/xxx"
        return lista.Distinct().OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Tira a chave (e coisas com cara de chave) de uma mensagem de erro antes de guardar/mostrar.</summary>
    public static string Mascarar(string texto, string? chave)
    {
        if (!string.IsNullOrEmpty(chave) && chave.Length >= 6) texto = texto.Replace(chave, "…" + chave[^4..]);
        return System.Text.RegularExpressions.Regex.Replace(texto, @"(sk-[A-Za-z0-9_\-]{6}|AIza[0-9A-Za-z_\-]{4}|xai-[A-Za-z0-9]{4})[A-Za-z0-9_\-]{4,}", "$1…");
    }

    /// <summary>
    /// Pergunta à primeira IA ligada (ou à da vez, no modo revezar); se ela falhar (erro, tempo esgotado, resposta sem o
    /// JSON pedido), passa para a próxima. Todas falharam: erro com o motivo de cada uma.
    /// </summary>
    private async Task<(JsonDocument Doc, ProvedorAtivo Provedor)> PerguntarAsync(string pedido, Dictionary<string, JsonElement> esquema, int maxTokens, CancellationToken ct, PessoaIa? quem = null, string uso = "tudo")
    {
        var ligados = LigadosPara(uso);
        if (ligados.Count == 0) throw new InvalidOperationException(uso == "tudo" || Ligados.Count == 0
            ? "IA desligada no servidor (nenhum provedor ligado no painel)."
            : $"Nenhuma IA ligada está aprovada para {(uso == "chat" ? "o chat" : "as ligações")} (veja o campo Uso em cada IA no painel).");
        int inicio = _op.ModoProvedores == "revezar" ? (int)((uint)Interlocked.Increment(ref _vez) % (uint)ligados.Count) : 0;
        var erros = new List<string>();
        for (int i = 0; i < ligados.Count; i++)
        {
            var p = ligados[(inicio + i) % ligados.Count];
            try { return (await PerguntarEmAsync(p, pedido, esquema, maxTokens, ct, quem), p); }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                erros.Add($"{p.Nome}: {ex.Message}");
                if (i < ligados.Count - 1) _log.LogWarning("IA {Nome} falhou ({Erro}); tentando a próxima", p.Nome, ex.Message);
            }
        }
        _log.LogWarning("Nenhuma IA respondeu: {Erros}", string.Join(" | ", erros));
        throw new InvalidOperationException(ligados.Count == 1 ? erros[0] : "Nenhuma IA respondeu. " + string.Join(" | ", erros));
    }

    /// <summary>Um provedor só. Registra sucesso/falha para o painel.</summary>
    private async Task<JsonDocument> PerguntarEmAsync(ProvedorAtivo p, string pedido, Dictionary<string, JsonElement> esquema, int maxTokens, CancellationToken ct, PessoaIa? quem = null)
    {
        var sit = SituacaoDe(p.Id);
        try
        {
            var doc = p.Tipo == TipoProvedorIa.Anthropic
                ? await PerguntarAnthropicAsync(p, pedido, esquema, maxTokens, ct, quem)
                : await PerguntarOpenAiAsync(p, pedido, esquema, ct, quem);
            try { ConferirCampos(doc, esquema); } catch { doc.Dispose(); throw; }
            sit.UltimoOk = DateTime.UtcNow;
            return doc;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            var msg = Mascarar(ex is TaskCanceledException or OperationCanceledException ? $"não respondeu em {TempoPorIa.TotalMinutes:0} min" : ex.Message, p.Chave);
            sit.UltimaFalha = DateTime.UtcNow;
            sit.UltimoErro = msg.Length > 300 ? msg[..300] + "..." : msg;
            throw new InvalidOperationException(sit.UltimoErro, ex);
        }
    }

    private static void ConferirCampos(JsonDocument doc, Dictionary<string, JsonElement> esquema)
    {
        if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("a resposta não veio no formato pedido (JSON).");
        if (!esquema.TryGetValue("required", out var req)) return;
        var faltam = req.EnumerateArray().Select(x => x.GetString()!).Where(c => !doc.RootElement.TryGetProperty(c, out _)).ToList();
        if (faltam.Count > 0) throw new InvalidOperationException("a resposta veio sem: " + string.Join(", ", faltam) + ".");
    }

    private async Task<JsonDocument> PerguntarAnthropicAsync(ProvedorAtivo p, string pedido, Dictionary<string, JsonElement> esquema, int maxTokens, CancellationToken ct, PessoaIa? quem)
    {
        var cli = _clientesAnthropic.GetOrAdd(p.Id, _ => ClienteAnthropic(p));
        var mp = new MessageCreateParams
        {
            Model = p.Modelo,
            MaxTokens = maxTokens,
            // se o filtro de segurança do modelo recusar, a própria API tenta de novo no modelo reserva recomendado
            Betas = ["server-side-fallback-2026-07-01"],
            Fallbacks = new Default(),
            // instruções gerais (iguais para todos: ficam no cache)
            System = new List<BetaTextBlockParam> { new() { Text = Sistema(), CacheControl = new BetaCacheControlEphemeral() } },
            OutputConfig = new BetaOutputConfig
            {
                Effort = Effort.Medium,
                Format = new BetaJsonOutputFormat { Schema = esquema },
            },
            Messages = [new BetaUserMessageParam { Content = pedido }],
        };
        var resp = await cli.Beta.Messages.Create(mp, ct);
        if (resp.StopReason == "refusal") throw new InvalidOperationException("O modelo recusou este conteúdo.");
        if (resp.StopReason == "max_tokens") throw new InvalidOperationException("Resposta da IA veio cortada.");
        var texto = string.Concat(resp.Content.Select(b => b.Value).OfType<BetaTextBlock>().Select(t => t.Text));
        return JsonDocument.Parse(texto);
    }

    /// <summary>
    /// Formato compatível com OpenAI (OpenAI, Gemini, xAI, DeepSeek, OpenRouter...): pede JSON (response_format json_object,
    /// o mais aceito) e descreve os campos no texto. Sem max_tokens/temperature: modelos de raciocínio recusam esses campos.
    /// Se a IA não aceitar response_format, tenta de novo sem ele.
    /// </summary>
    private async Task<JsonDocument> PerguntarOpenAiAsync(ProvedorAtivo p, string pedido, Dictionary<string, JsonElement> esquema, CancellationToken ct, PessoaIa? quem)
    {
        var formato = JsonSerializer.Serialize(esquema["properties"]);
        var usuario = $"{pedido}\n\nResponda SOMENTE com um objeto JSON (sem texto antes ou depois) com exatamente estes campos: {formato}";
        var mensagens = new object[] { new { role = "system", content = Sistema() }, new { role = "user", content = usuario } };
        for (int tentativa = 0; ; tentativa++)
        {
            bool comFormato = tentativa == 0;
            object corpo = comFormato
                ? new { model = p.Modelo, messages = mensagens, response_format = new { type = "json_object" } }
                : new { model = p.Modelo, messages = mensagens };
            using var req = new HttpRequestMessage(HttpMethod.Post, p.Url.TrimEnd('/') + "/chat/completions")
            {
                Content = new StringContent(JsonSerializer.Serialize(corpo), Encoding.UTF8, "application/json"),
            };
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", p.Chave);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TempoPorIa);
            using var resp = await Http.SendAsync(req, cts.Token);
            var texto = await resp.Content.ReadAsStringAsync(cts.Token);
            if (!resp.IsSuccessStatusCode)
            {
                if (comFormato && (int)resp.StatusCode is 400 or 422 && texto.Contains("response_format", StringComparison.OrdinalIgnoreCase)) continue;
                throw new InvalidOperationException($"erro {(int)resp.StatusCode}: {Transcritor.Curto(MensagemDeErro(texto))}");
            }
            using var doc = JsonDocument.Parse(texto);
            var conteudo = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
            return JsonDocument.Parse(SoOJson(conteudo));
        }
    }

    private static string MensagemDeErro(string corpo)
    {
        try
        {
            using var d = JsonDocument.Parse(corpo);
            var r = d.RootElement.ValueKind == JsonValueKind.Array && d.RootElement.GetArrayLength() > 0 ? d.RootElement[0] : d.RootElement;
            if (r.TryGetProperty("error", out var e))
                return e.ValueKind == JsonValueKind.String ? e.GetString() ?? corpo : e.TryGetProperty("message", out var m) ? m.GetString() ?? corpo : corpo;
        }
        catch (JsonException) { }
        return corpo;
    }

    /// <summary>Algumas IAs embrulham o JSON em ```json ... ``` ou põem texto em volta: fica do primeiro { ao último }.</summary>
    internal static string SoOJson(string s)
    {
        int a = s.IndexOf('{'), b = s.LastIndexOf('}');
        if (a < 0 || b <= a) throw new InvalidOperationException("a resposta não veio no formato pedido (JSON).");
        return s[a..(b + 1)];
    }

    private string Sistema() => $"""
        Você é o assistente do Softphone, o telefone e chat interno da {_op.Empresa}. Escreve sempre em português do Brasil,
        de forma clara e objetiva, como um colega de trabalho experiente. Nunca invente fatos, nomes, valores ou prazos:
        use só o que está no texto recebido. Quando algo não foi dito, deixe o campo vazio.
        Transcrições vêm de reconhecimento de voz e podem ter palavras trocadas; corrija só quando o sentido for óbvio.
        """;

    private static Dictionary<string, JsonElement> Esquema(object propriedades, string[] obrigatorios) => new()
    {
        ["type"] = JsonSerializer.SerializeToElement("object"),
        ["properties"] = JsonSerializer.SerializeToElement(propriedades),
        ["required"] = JsonSerializer.SerializeToElement(obrigatorios),
        ["additionalProperties"] = JsonSerializer.SerializeToElement(false),
    };

    public async Task<ResumoDto> ResumirAsync(string transcricao, PessoaIa eu, string outro, string direcao, CancellationToken ct)
    {
        var nomeEu = eu.Nome;
        var pedido = $"""
            Abaixo está a transcrição de uma ligação telefônica {(direcao == "entrada" ? "RECEBIDA" : "FEITA")} por {nomeEu}
            (funcionário da {_op.Empresa}, aparece como "{nomeEu}") com "{outro}" (o outro lado).

            Devolva:
            - assunto: o tema da ligação em até 8 palavras;
            - resumo: 2 a 5 frases com o que foi tratado e decidido;
            - proximos_passos: ações combinadas, uma por item, com quem faz e prazo quando foram ditos (lista vazia se nenhuma);
            - quem_ligou: nome e/ou empresa da outra pessoa, SÓ se ela disse na ligação (senão vazio);
            - sentimento: positivo, neutro ou negativo (como a outra pessoa terminou a ligação);
            - sugestao_resposta: rascunho curto de mensagem/e-mail de retorno de {nomeEu} para a outra pessoa confirmando o combinado
              (vazio se a ligação não pede retorno).

            <transcricao>
            {transcricao}
            </transcricao>
            """;
        var esquema = Esquema(new
        {
            assunto = new { type = "string" },
            resumo = new { type = "string" },
            proximos_passos = new { type = "array", items = new { type = "string" } },
            quem_ligou = new { type = "string" },
            sentimento = new { type = "string", @enum = new[] { "positivo", "neutro", "negativo" } },
            sugestao_resposta = new { type = "string" },
        }, ["assunto", "resumo", "proximos_passos", "quem_ligou", "sentimento", "sugestao_resposta"]);
        var (doc, quem) = await PerguntarAsync(pedido, esquema, 8000, ct, eu, "ligacoes");
        using var _doc = doc;
        var r = doc.RootElement;
        string? Vazio(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        return new ResumoDto(
            r.GetProperty("assunto").GetString()?.Trim() ?? "",
            r.GetProperty("resumo").GetString()?.Trim() ?? "",
            r.GetProperty("proximos_passos").EnumerateArray().Select(x => x.GetString()?.Trim() ?? "").Where(x => x.Length > 0).ToArray(),
            Vazio(r.GetProperty("quem_ligou").GetString()),
            r.GetProperty("sentimento").GetString() ?? "neutro",
            Vazio(r.GetProperty("sugestao_resposta").GetString()),
            quem.Nome);
    }

}

// ============================================================================================
// Fila de processamento: separa canais → transcreve → junta → resume → avisa o dono. Também apaga o que venceu.
// ============================================================================================

public sealed class ProcessadorGravacoes : BackgroundService
{
    private readonly Channel<long> _fila = Channel.CreateUnbounded<long>();
    private readonly Banco _banco;
    private readonly Transcritor _transcritor;
    private readonly Assistente _assistente;
    private readonly OpcoesHub _op;
    private readonly IHubContext<ChatHub> _hub;
    private readonly ILogger<ProcessadorGravacoes> _log;

    public ProcessadorGravacoes(Banco banco, Transcritor transcritor, Assistente assistente, OpcoesHub op, IHubContext<ChatHub> hub, ILogger<ProcessadorGravacoes> log)
    {
        _banco = banco; _transcritor = transcritor; _assistente = assistente; _op = op; _hub = hub; _log = log;
        Directory.CreateDirectory(PastaGravacoes);
    }

    public string PastaGravacoes => Path.Combine(_op.Dados, "gravacoes");
    public string ArquivoDe(long id) => Path.Combine(PastaGravacoes, $"{id}.wav");
    public void Enfileirar(long id) => _fila.Writer.TryWrite(id);

    public GravacaoDto Dto(Banco.Gravacao g) => new(g.Id, g.Numero, g.Nome, g.Direcao, g.Inicio, g.Duracao, g.Status, g.Erro, g.Assunto,
        g.CriadaEm.AddDays(_op.Ia.RetencaoDias));

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static SegmentoDto[] LerSegmentos(string? json) => string.IsNullOrEmpty(json) ? [] : JsonSerializer.Deserialize<SegmentoDto[]>(json, Json) ?? [];
    public static ResumoDto? LerResumo(string? json) => string.IsNullOrEmpty(json) ? null : JsonSerializer.Deserialize<ResumoDto>(json, Json);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        foreach (var id in _banco.GravacoesPendentes()) Enfileirar(id);
        _ = Task.Run(() => LimparVencidasAsync(ct), ct);
        await foreach (var id in _fila.Reader.ReadAllAsync(ct))
        {
            try { await ProcessarAsync(id, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _log.LogError(ex, "Gravação {Id}: falha", id);
                await MudarAsync(id, StatusGravacao.Erro, ex.Message);
            }
        }
    }

    private async Task MudarAsync(long id, string status, string? erro = null)
    {
        _banco.StatusDaGravacao(id, status, erro);
        var g = _banco.ObterGravacao(id);
        if (g != null) await _hub.Clients.User(g.Dono).SendAsync(HubNomes.EvGravacao, Dto(g));
    }

    private async Task ProcessarAsync(long id, CancellationToken ct)
    {
        var g = _banco.ObterGravacao(id);
        if (g == null) return;
        if (!File.Exists(ArquivoDe(id))) { await MudarAsync(id, StatusGravacao.Erro, "Áudio não encontrado."); return; }
        if (!_transcritor.Configurado) { await MudarAsync(id, StatusGravacao.SoAudio); return; }

        await MudarAsync(id, StatusGravacao.Transcrevendo);
        var inicio = DateTime.UtcNow;
        SegmentoDto[] segmentos;
        using (var wav = WavCanais.Separar(ArquivoDe(id), Path.Combine(_op.Dados, "tmp")))
        {
            var brutos = new List<(string Lado, double Ini, double Fim, string Texto, double Volume)>();
            for (int c = 0; c < wav.Canais; c++)
            {
                var lado = c == 0 ? "eu" : "outro";
                foreach (var (ini, fim, texto) in await _transcritor.TranscreverAsync(wav.Arquivos[c], ct))
                    brutos.Add((lado, ini, fim, texto, wav.Volume(c, ini, fim)));
            }
            segmentos = Juntar(brutos);
        }
        _banco.SalvarTranscricao(id, JsonSerializer.Serialize(segmentos, Json));
        _log.LogInformation("Gravação {Id}: {Seg} trechos transcritos em {Tempo:N0} s", id, segmentos.Length, (DateTime.UtcNow - inicio).TotalSeconds);

        string? aviso = null;
        var falado = string.Join(" ", segmentos.Select(s => s.Texto));
        if (_assistente.Configurado && falado.Length >= 20)
        {
            await MudarAsync(id, StatusGravacao.Resumindo);
            try
            {
                var dono = _banco.ObterPessoa(g.Dono);
                var nomeEu = dono?.Nome ?? g.Dono;
                var outro = string.IsNullOrWhiteSpace(g.Nome) ? g.Numero : $"{g.Nome} ({g.Numero})";
                var transcrito = Texto(segmentos, nomeEu, outro);
                var resumo = await _assistente.ResumirAsync(transcrito, new PessoaIa(g.Dono, nomeEu, dono?.Setor) { Assunto = transcrito }, outro, g.Direcao, ct);
                _banco.SalvarResumo(id, JsonSerializer.Serialize(resumo, Json), resumo.Assunto);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "Gravação {Id}: resumo falhou", id);
                aviso = "Transcrição pronta, mas o resumo falhou: " + ex.Message;
            }
        }
        await MudarAsync(id, StatusGravacao.Pronta, aviso);
    }

    /// <summary>Transcrição em texto corrido para a IA: "[mm:ss] Nome: fala".</summary>
    public static string Texto(IEnumerable<SegmentoDto> segmentos, string nomeEu, string outro) => string.Join("\n",
        segmentos.Select(s => $"[{TimeSpan.FromSeconds(s.Inicio):mm\\:ss}] {(s.Lado == "eu" ? nomeEu : outro)}: {s.Texto}"));

    // frases que o reconhecimento de voz "inventa" em silêncio/ruído (vêm de legendas de vídeo do treino)
    private static readonly string[] Alucinacoes =
    [
        "amara.org", "legendas pela comunidade", "legenda por", "legendado por", "obrigado por assistir", "obrigada por assistir",
        "inscreva-se no canal", "se inscreva no canal", "ative o sininho", "transcrição por", "tradução por",
    ];

    /// <summary>
    /// Junta os dois lados em ordem de tempo, tirando: fala inventada em silêncio (volume baixo ou frase típica de legenda)
    /// e eco (o microfone de quem usa caixa de som pega a voz do outro lado: mesma frase nos dois canais → fica a mais alta).
    /// </summary>
    internal static SegmentoDto[] Juntar(List<(string Lado, double Ini, double Fim, string Texto, double Volume)> brutos)
    {
        const double VolumeMinimo = 120;
        var bons = brutos
            .Where(s => s.Volume >= VolumeMinimo)
            .Where(s => !Alucinacoes.Any(a => s.Texto.Contains(a, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        var descartar = new HashSet<int>();
        for (int i = 0; i < bons.Count; i++)
            for (int j = i + 1; j < bons.Count; j++)
            {
                var (a, b) = (bons[i], bons[j]);
                if (a.Lado == b.Lado) continue;
                double sobre = Math.Min(a.Fim, b.Fim) - Math.Max(a.Ini, b.Ini);
                if (sobre <= 0 || sobre < 0.5 * Math.Min(a.Fim - a.Ini, b.Fim - b.Ini)) continue;
                if (Parecidas(a.Texto, b.Texto) >= 0.6) descartar.Add(a.Volume >= b.Volume ? j : i);
            }
        return bons.Where((_, i) => !descartar.Contains(i))
            .OrderBy(s => s.Ini).ThenBy(s => s.Lado)
            .Select(s => new SegmentoDto(s.Lado, Math.Round(s.Ini, 2), Math.Round(s.Fim, 2), s.Texto))
            .ToArray();
    }

    private static double Parecidas(string a, string b)
    {
        static HashSet<string> Palavras(string s) => s.ToLower(CultureInfo.GetCultureInfo("pt-BR"))
            .Split(s.Where(ch => !char.IsLetterOrDigit(ch)).Distinct().Append(' ').ToArray(), StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet();
        var pa = Palavras(a); var pb = Palavras(b);
        if (pa.Count == 0 || pb.Count == 0) return 0;
        return (double)pa.Intersect(pb).Count() / pa.Union(pb).Count();
    }

    /// <summary>Apaga áudio, transcrição e resumo com mais de RetencaoDias (checa de hora em hora).</summary>
    private async Task LimparVencidasAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var vencidas = _banco.GravacoesCriadasAntesDe(DateTime.UtcNow.AddDays(-Math.Max(1, _op.Ia.RetencaoDias)));
                int apagadas = 0;
                foreach (var id in vencidas)
                {
                    try { Excluir(id); apagadas++; }
                    catch (IOException) { } // já registrado; o registro fica e a próxima rodada tenta de novo
                }
                if (vencidas.Count > 0) _log.LogInformation("Retenção: {N} de {Total} gravações apagadas (mais de {Dias} dias)", apagadas, vencidas.Count, _op.Ia.RetencaoDias);
            }
            catch (Exception ex) { _log.LogError(ex, "Falha ao apagar gravações vencidas"); }
            try { await Task.Delay(TimeSpan.FromHours(1), ct); } catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>Apaga o áudio e só depois o registro (R12): se o arquivo não sair do disco, o registro fica, a pessoa
    /// recebe o erro e a retenção tenta de novo na próxima hora (antes o áudio ficava órfão e ninguém mais via).</summary>
    public void Excluir(long id)
    {
        var arq = ArquivoDe(id);
        try { if (File.Exists(arq)) File.Delete(arq); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogError(ex, "Não deu para apagar o áudio da gravação {Id}; o registro fica para tentar de novo", id);
            throw new IOException("Não deu para apagar o áudio no servidor. Tente de novo daqui a pouco; se continuar, avise a TI.", ex);
        }
        _banco.ExcluirGravacao(id);
    }

    /// <summary>Compara números de telefone pelos dígitos (com ou sem DDD/0/+55: bate pelos 8 últimos).</summary>
    public static bool MesmoNumero(string a, string b)
    {
        string da = new(a.Where(char.IsDigit).ToArray()), db = new(b.Where(char.IsDigit).ToArray());
        if (da.Length == 0 || db.Length == 0) return false;
        if (da == db) return true;
        return da.Length >= 8 && db.Length >= 8 && da[^8..] == db[^8..];
    }
}
