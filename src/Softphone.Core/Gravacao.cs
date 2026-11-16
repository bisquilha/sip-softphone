using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Softphone.Core;

/// <summary>
/// Grava os dois lados de UMA chamada em WAV estéreo 16 bits, na taxa do codec (8 kHz no G.711, 16 kHz no G.722): canal esquerdo = eu (microfone, já com o aviso),
/// canal direito = o outro lado. Separar os lados por canal deixa a transcrição saber quem falou o quê sem adivinhar.
/// Enquanto grava, cada lado vai para um arquivo bruto temporário (não segura a chamada inteira na memória);
/// no fim os dois viram um WAV só. Os lados são alinhados pelo relógio: se um lado ficar sem áudio (espera,
/// supressão de silêncio do PABX), entra silêncio no lugar, para as falas não "andarem" uma em relação à outra.
/// </summary>
public sealed class GravadorChamada : IDisposable
{
    /// <summary>Taxa do WAV (a do codec da chamada).</summary>
    public int Taxa { get; }
    /// <summary>Gravação mais longa que isso para de crescer (proteção de disco).</summary>
    public static readonly TimeSpan Maximo = TimeSpan.FromHours(3);

    private readonly Lado _eu, _outro;
    private readonly Stopwatch _relogio = Stopwatch.StartNew();
    private volatile bool _parado;
    private TimeSpan _duracaoFinal;

    public DateTime Inicio { get; } = DateTime.Now;
    public TimeSpan Duracao => _parado ? _duracaoFinal : _relogio.Elapsed;
    public string Pasta { get; }

    /// <summary>Limite do Hub para receber uma gravação (EstadoIaDto.GravacaoMaxMb; 400 MB por padrão).</summary>
    public const long LimitePadraoBytes = 400L * 1024 * 1024;

    /// <summary>Até quando grava: 3 h, ou menos se o WAV passaria do limite do Hub (R06: em G.722, 16 kHz estéreo,
    /// 3 h dariam ~659 MB, o Hub recusava e a gravação travava a fila de envio).</summary>
    public TimeSpan DuracaoMaxima { get; }

    public GravadorChamada(string pasta, int taxa = 8000, long limiteBytes = LimitePadraoBytes)
    {
        Pasta = pasta;
        Taxa = taxa is 8000 or 16000 ? taxa : 8000;
        var porTamanho = TimeSpan.FromSeconds(Math.Max(60, (limiteBytes - 44) / (Taxa * 4.0)) * 0.99); // 1% de folga
        DuracaoMaxima = porTamanho < Maximo ? porTamanho : Maximo;
        Directory.CreateDirectory(pasta);
        var id = DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6];
        _eu = new Lado(Path.Combine(pasta, id + ".eu.raw"));
        _outro = new Lado(Path.Combine(pasta, id + ".outro.raw"));
        ArquivoFinal = Path.Combine(pasta, id + ".wav");
    }

    /// <summary>Onde o WAV fica depois de <see cref="Concluir"/>.</summary>
    public string ArquivoFinal { get; }

    /// <summary>Áudio do meu lado (o que vai para o outro).</summary>
    public void Eu(short[] pcm, int taxa) => Escrever(_eu, pcm, taxa);
    /// <summary>Áudio do outro lado (o que chega).</summary>
    public void Outro(short[] pcm, int taxa) => Escrever(_outro, pcm, taxa);

    private void Escrever(Lado lado, short[] pcm, int taxa)
    {
        if (_parado || pcm.Length == 0 || taxa <= 0) return;
        var agora = _relogio.Elapsed;
        if (agora > DuracaoMaxima) return;
        var conv = Reamostrar(pcm, taxa, Taxa, ref lado.UltimaAmostra);
        lock (lado)
        {
            if (_parado) return;
            // onde este pedaço deveria começar, pelo relógio; se o lado ficou mais de 200 ms para trás, completa com silêncio
            long inicioEsperado = (long)(agora.TotalSeconds * Taxa) - conv.Length;
            long atraso = inicioEsperado - lado.Amostras;
            if (atraso > Taxa / 5) lado.Silencio(atraso);
            lado.Gravar(conv);
        }
    }

    /// <summary>Muda a taxa por interpolação linear (mesma taxa passa direto).</summary>
    internal static short[] Reamostrar(short[] pcm, int taxa, int taxaSaida, ref short anterior)
    {
        if (taxa == taxaSaida) { anterior = pcm[^1]; return pcm; }
        double passo = (double)taxa / taxaSaida;     // quanto anda na entrada a cada amostra de saída
        int n = (int)Math.Round(pcm.Length / passo);
        var saida = new short[n];
        for (int i = 0; i < n; i++)
        {
            double pos = i * passo - 1;               // -1: a primeira amostra interpola com a última do pedaço anterior
            int a = (int)Math.Floor(pos);
            double f = pos - a;
            short va = a < 0 ? anterior : pcm[Math.Min(a, pcm.Length - 1)];
            short vb = pcm[Math.Min(Math.Max(a + 1, 0), pcm.Length - 1)];
            saida[i] = (short)Math.Round(va + (vb - va) * f);
        }
        anterior = pcm[^1];
        return saida;
    }

    /// <summary>Para a gravação e monta o WAV estéreo. Retorna o caminho do arquivo.</summary>
    public string Concluir()
    {
        _parado = true;
        _duracaoFinal = _relogio.Elapsed;
        lock (_eu) lock (_outro)
        {
            long total = Math.Max(_eu.Amostras, _outro.Amostras);
            _eu.Fechar(); _outro.Fechar();
            using var wav = new FileStream(ArquivoFinal, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024);
            EscreverCabecalho(wav, total, Taxa);
            using var a = File.OpenRead(_eu.Caminho);
            using var b = File.OpenRead(_outro.Caminho);
            const int Bloco = 8192;
            var ba = new byte[Bloco * 2]; var bb = new byte[Bloco * 2]; var sai = new byte[Bloco * 4];
            for (long feito = 0; feito < total; feito += Bloco)
            {
                int n = (int)Math.Min(Bloco, total - feito);
                LerOuSilencio(a, ba, n * 2); LerOuSilencio(b, bb, n * 2);
                for (int i = 0; i < n; i++)
                {
                    sai[i * 4] = ba[i * 2]; sai[i * 4 + 1] = ba[i * 2 + 1];
                    sai[i * 4 + 2] = bb[i * 2]; sai[i * 4 + 3] = bb[i * 2 + 1];
                }
                wav.Write(sai, 0, n * 4);
            }
        }
        _eu.Apagar(); _outro.Apagar();
        return ArquivoFinal;
    }

    private static void LerOuSilencio(Stream s, byte[] buf, int bytes)
    {
        int lidos = 0;
        while (lidos < bytes) { int r = s.Read(buf, lidos, bytes - lidos); if (r <= 0) break; lidos += r; }
        if (lidos < bytes) Array.Clear(buf, lidos, bytes - lidos);
    }

    private static void EscreverCabecalho(Stream s, long amostrasPorCanal, int Taxa)
    {
        const short Canais = 2, Bits = 16;
        long dados = amostrasPorCanal * Canais * (Bits / 8);
        using var w = new BinaryWriter(s, System.Text.Encoding.ASCII, leaveOpen: true);
        w.Write("RIFF"u8); w.Write((uint)Math.Min(uint.MaxValue, 36 + dados)); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write(Canais); w.Write(Taxa);
        w.Write(Taxa * Canais * (Bits / 8)); w.Write((short)(Canais * (Bits / 8))); w.Write(Bits);
        w.Write("data"u8); w.Write((uint)Math.Min(uint.MaxValue, dados));
    }

    public void Dispose()
    {
        _parado = true;
        lock (_eu) lock (_outro) { _eu.Fechar(); _outro.Fechar(); }
        _eu.Apagar(); _outro.Apagar();
    }

    private sealed class Lado
    {
        private FileStream? _fs;
        public readonly string Caminho;
        public long Amostras;
        public short UltimaAmostra;
        private static readonly byte[] Zeros = new byte[32000];

        public Lado(string caminho)
        {
            Caminho = caminho;
            _fs = new FileStream(caminho, FileMode.Create, FileAccess.Write, FileShare.Read, 64 * 1024);
        }

        public void Gravar(short[] pcm)
        {
            if (_fs == null) return;
            _fs.Write(MemoryMarshal.AsBytes(pcm.AsSpan()));
            Amostras += pcm.Length;
        }

        public void Silencio(long amostras)
        {
            if (_fs == null) return;
            long bytes = amostras * 2;
            while (bytes > 0) { int n = (int)Math.Min(bytes, Zeros.Length); _fs.Write(Zeros, 0, n); bytes -= n; }
            Amostras += amostras;
        }

        public void Fechar() { try { _fs?.Dispose(); } catch { } _fs = null; }
        public void Apagar() { try { File.Delete(Caminho); } catch { } }
    }
}

/// <summary>Aviso falado ("esta ligação está sendo gravada"): PCM 16 kHz misturado no áudio que sai e no que toca aqui.</summary>
public static class AvisoGravacao
{
    /// <summary>O aviso fica guardado em 16 kHz e é convertido na hora para a taxa de cada lado.</summary>
    public const int Taxa = 16000;

    /// <summary>Lê um WAV PCM 16 bits mono (qualquer taxa até 48 kHz), tira o silêncio das pontas e devolve em 16 kHz.</summary>
    public static short[] CarregarWav(Stream wav)
    {
        using var r = new BinaryReader(wav);
        if (new string(r.ReadChars(4)) != "RIFF") throw new InvalidDataException("não é WAV");
        r.ReadInt32();
        if (new string(r.ReadChars(4)) != "WAVE") throw new InvalidDataException("não é WAV");
        int taxa = 0, canais = 0, bits = 0;
        byte[]? dados = null;
        while (wav.Position + 8 <= wav.Length)
        {
            var id = new string(r.ReadChars(4)); int tam = r.ReadInt32();
            if (id == "fmt ")
            {
                r.ReadInt16(); canais = r.ReadInt16(); taxa = r.ReadInt32(); r.ReadInt32(); r.ReadInt16(); bits = r.ReadInt16();
                if (tam > 16) r.ReadBytes(tam - 16);
            }
            else if (id == "data") { dados = r.ReadBytes(tam); break; }
            else r.ReadBytes(tam + (tam & 1));
        }
        if (dados == null || bits != 16 || canais < 1 || taxa <= 0) throw new InvalidDataException("WAV precisa ser PCM 16 bits");
        var pcm = new short[dados.Length / 2 / canais];
        for (int i = 0; i < pcm.Length; i++) pcm[i] = BitConverter.ToInt16(dados, i * 2 * canais);
        // tira silêncio do começo e do fim (o sintetizador deixa uma folga)
        const int Limiar = 300;
        int ini = Array.FindIndex(pcm, v => Math.Abs((int)v) > Limiar);
        int fim = Array.FindLastIndex(pcm, v => Math.Abs((int)v) > Limiar);
        if (ini < 0) return Array.Empty<short>();
        ini = Math.Max(0, ini - taxa / 20); fim = Math.Min(pcm.Length - 1, fim + taxa / 10);
        pcm = pcm[ini..(fim + 1)];
        short ant = 0;
        return taxa == Taxa ? pcm : GravadorChamada.Reamostrar(pcm, taxa, Taxa, ref ant);
    }

    /// <summary>Soma o aviso (16 kHz) ao quadro <paramref name="pcm"/> na taxa dele, a partir de <paramref name="pos"/>.</summary>
    internal static void Misturar(short[] pcm, int taxa, short[] aviso, ref int pos)
    {
        if (taxa <= 0) return;
        double passo = (double)Taxa / taxa;   // 2 em 8 kHz, 1 em 16 kHz
        double p = pos;
        for (int i = 0; i < pcm.Length && p < aviso.Length; i++, p += passo)
        {
            int k = (int)p;
            // em 8 kHz tira a média de duas amostras (filtro simples para não "chiar" ao reduzir a taxa)
            int v = passo >= 2 && k + 1 < aviso.Length ? (aviso[k] + aviso[k + 1]) / 2 : aviso[k];
            pcm[i] = (short)Math.Clamp(pcm[i] + v, short.MinValue, short.MaxValue);
        }
        pos = (int)Math.Min(p, aviso.Length);
    }
}
