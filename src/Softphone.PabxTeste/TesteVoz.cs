using System.Diagnostics;
using Softphone.Core;

/// <summary>
/// Testes do tratamento de voz (WebRTC APM) sem placa de som: um "viva-voz" simulado devolve o que tocou no alto-falante
/// para o microfone (com atraso e reflexões), e o teste mede quanto do eco sobrou, se a voz de quem está no PC passa e se
/// o ruído de fundo cai.
/// </summary>
static class TesteVoz
{
    public static void Rodar(Action<bool, string> ok)
    {
        ok(ProcessadorVoz.Disponivel, $"webrtc-apm.dll carregou ({ProcessadorVoz.ErroCarga ?? "ok"})");
        if (!ProcessadorVoz.Disponivel) return;
        ok(ProcessadorVoz.Criar(8000, new OpcoesVoz { CancelarEco = false, Ruido = NivelRuido.Desligado }) == null, "tudo desligado = sem processador");

        foreach (int taxa in new[] { 8000, 16000 })
        {
            var voz = Voz(taxa);
            int seg = 14, n = taxa * seg, q = taxa / 50; // quadros de 20 ms, como o microfone do app
            var longe = Repetir(voz, n, 1.0);
            // fala de quem está no PC só nos últimos 4 s (conversa simultânea), voz diferente: invertida e mais aguda
            var perto = new short[n];
            var vozPerto = Inverter(voz);
            for (int i = taxa * 10; i < n; i++) perto[i] = (short)(vozPerto[(i * 5 / 4) % vozPerto.Length] * 0.6);

            var eco = Eco(longe, taxa, atrasoMs: 120);
            var rnd = new Random(7);
            var mic = new short[n];
            for (int i = 0; i < n; i++) mic[i] = Sat(eco[i] + perto[i] + (rnd.NextDouble() - 0.5) * 60);

            using var p = ProcessadorVoz.Criar(taxa, new OpcoesVoz { CancelarEco = true, Ruido = NivelRuido.Moderado })!;
            var saida = (short[])mic.Clone();
            var rel = Stopwatch.StartNew();
            for (int ini = 0; ini + q <= n; ini += q)
            {
                p.Referencia(longe.AsSpan(ini, q), taxa);   // o que tocou agora
                var quadro = saida.AsSpan(ini, q).ToArray();
                p.Processar(quadro);                         // microfone deste instante
                quadro.CopyTo(saida, ini);
            }
            rel.Stop();
            ok(p.Erros == 0 && p.Quadros == n / (taxa / 100), $"{taxa / 1000} kHz: {p.Quadros} quadros de 10 ms sem erro");
            ok(rel.Elapsed.TotalSeconds < seg / 5.0, $"{taxa / 1000} kHz: leve ({rel.Elapsed.TotalMilliseconds:N0} ms para {seg} s de áudio)");

            // eco sozinho: 6 s a 10 s (depois de aprender)
            double erle = Db(Energia(mic, taxa * 6, taxa * 10) / Math.Max(1, Energia(saida, taxa * 6, taxa * 10)));
            ok(erle > 18, $"{taxa / 1000} kHz: eco reduzido em {erle:N1} dB (mínimo 18)");
            // conversa simultânea: a voz de perto tem de passar (não pode virar silêncio)
            double ePerto = Energia(perto, taxa * 11, n), eSaida = Energia(saida, taxa * 11, n);
            double retido = Db(eSaida / Math.Max(1, ePerto));
            ok(retido > -10, $"{taxa / 1000} kHz: voz de quem fala no PC passa durante o eco ({retido:N1} dB da original)");
        }

        // ruído de fundo constante (ventilador): cai com a redução de ruído
        {
            int taxa = 8000, n = taxa * 6, q = taxa / 50;
            var rnd = new Random(3);
            var mic = new short[n];
            double fase = 0;
            for (int i = 0; i < n; i++) { fase += 2 * Math.PI * 120 / taxa; mic[i] = Sat((rnd.NextDouble() - 0.5) * 1200 + Math.Sin(fase) * 200); }
            using var p = ProcessadorVoz.Criar(taxa, new OpcoesVoz { CancelarEco = false, Ruido = NivelRuido.Alto })!;
            var saida = (short[])mic.Clone();
            for (int ini = 0; ini + q <= n; ini += q) { var f = saida.AsSpan(ini, q).ToArray(); p.Processar(f); f.CopyTo(saida, ini); }
            double red = Db(Energia(mic, taxa * 3, n) / Math.Max(1, Energia(saida, taxa * 3, n)));
            ok(red > 8, $"ruído de fundo reduzido em {red:N1} dB (mínimo 8)");
        }
    }

    // ---------- sinais ----------

    /// <summary>Voz de verdade (o aviso de gravação embutido no app) ou, se não achar, uma "fala" sintética.</summary>
    private static short[] Voz(int taxa)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            var wav = Path.Combine(d.FullName, "src", "Softphone.App", "Assets", "aviso-gravacao.wav");
            if (!File.Exists(wav)) continue;
            using var fs = File.OpenRead(wav);
            var pcm16 = AvisoGravacao.CarregarWav(fs);
            if (taxa == AvisoGravacao.Taxa) return pcm16;
            var r = new short[pcm16.Length * taxa / AvisoGravacao.Taxa];
            for (int i = 0; i < r.Length; i++) r[i] = pcm16[(long)i * AvisoGravacao.Taxa / taxa];
            return r;
        }
        var s = new short[taxa * 3];
        double f = 0;
        for (int i = 0; i < s.Length; i++)
        {
            double t = (double)i / taxa, env = Math.Max(0, Math.Sin(2 * Math.PI * 3 * t));
            f += 2 * Math.PI * (140 + 40 * Math.Sin(2 * Math.PI * 0.7 * t)) / taxa;
            s[i] = (short)(env * (Math.Sin(f) + 0.5 * Math.Sin(2 * f) + 0.3 * Math.Sin(3 * f) + 0.2 * Math.Sin(5 * f)) * 7000);
        }
        return s;
    }

    private static short[] Repetir(short[] s, int n, double ganho)
    {
        var r = new short[n];
        for (int i = 0; i < n; i++) r[i] = (short)(s[i % s.Length] * ganho);
        return r;
    }

    private static short[] Inverter(short[] s) { var r = (short[])s.Clone(); Array.Reverse(r); return r; }

    /// <summary>Caminho alto-falante -> microfone: atraso + som direto + três reflexões da sala.</summary>
    private static double[] Eco(short[] longe, int taxa, int atrasoMs)
    {
        var taps = new (double ms, double g)[] { (0, 0.45), (4, 0.22), (11, -0.14), (27, 0.08), (55, -0.04) };
        var r = new double[longe.Length];
        foreach (var (ms, g) in taps)
        {
            int d = (atrasoMs + (int)ms) * taxa / 1000;
            for (int i = d; i < longe.Length; i++) r[i] += longe[i - d] * g;
        }
        return r;
    }

    private static short Sat(double v) => (short)Math.Clamp(v, short.MinValue, short.MaxValue);
    private static double Energia(short[] s, int de, int ate) { double e = 0; for (int i = de; i < ate && i < s.Length; i++) e += (double)s[i] * s[i]; return e; }
    private static double Db(double razao) => 10 * Math.Log10(Math.Max(razao, 1e-9));
}
