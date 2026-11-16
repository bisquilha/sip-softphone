using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Softphone.Core;

/// <summary>
/// 1.3 (Claude Code, 10/11/2026): recorta o áudio de cada lado da chamada em trechos de fala para a legenda ao vivo.
/// Lado "atendente" = meu microfone (canal esquerdo da gravação); "interlocutor" = o que chega (canal direito): os dois lados já vêm
/// separados, então o papel é certo (não precisa adivinhar quem falou). Silêncio não gera trecho. Durante a fala emite trechos
/// provisórios (Final = false, o mesmo início, texto cada vez mais completo) e, na pausa, o definitivo (Final = true).
/// Nada é gravado em disco: o áudio vive só na memória até o trecho ser entregue.
/// </summary>
public sealed class CapturaLegenda
{
    /// <summary>Trecho de fala pronto para transcrever. <see cref="Id"/> é o mesmo nos provisórios e no definitivo da mesma fala.</summary>
    public sealed record Trecho(string Papel, long Id, byte[] Wav, double InicioSeg, bool Final);

    public event Action<Trecho>? Pronto;

    /// <summary>Energia (RMS, 16 bits) acima da qual o quadro conta como fala.</summary>
    public int LimiarFala { get; set; } = 450;
    /// <summary>Pausa que fecha a fala (ms).</summary>
    public int PausaMs { get; set; } = 650;
    /// <summary>Fala mais curta que isso (ms) é descartada (estalo, tosse).</summary>
    public int MinimoMs { get; set; } = 500;
    /// <summary>Fala contínua mais longa que isso (ms) é fechada à força.</summary>
    public int MaximoMs { get; set; } = 12000;
    /// <summary>De quanto em quanto tempo (ms) de fala contínua sai um trecho provisório.</summary>
    public int ProvisorioMs { get; set; } = 3000;

    private readonly Stopwatch _relogio = Stopwatch.StartNew();
    private readonly Lado _eu, _outro;
    private long _proximoId;
    private volatile bool _parada;

    public CapturaLegenda()
    {
        _eu = new Lado(this, "atendente");
        _outro = new Lado(this, "interlocutor");
    }

    public void Parar()
    {
        _parada = true;
        _eu.Fechar(); _outro.Fechar();
    }

    public void Eu(short[] pcm, int taxa) { if (!_parada) _eu.Processar(pcm, taxa); }
    public void Outro(short[] pcm, int taxa) { if (!_parada) _outro.Processar(pcm, taxa); }

    private void Emitir(string papel, long id, short[] amostras, int taxa, double inicio, bool final)
    {
        try { Pronto?.Invoke(new Trecho(papel, id, Wav(amostras, taxa), inicio, final)); } catch { /* a legenda nunca pode derrubar a chamada */ }
    }

    /// <summary>WAV PCM 16 bits mono (cabeçalho de 44 bytes).</summary>
    public static byte[] Wav(short[] pcm, int taxa)
    {
        int dados = pcm.Length * 2;
        var b = new byte[44 + dados];
        void Texto(int pos, string s) { for (int i = 0; i < 4; i++) b[pos + i] = (byte)s[i]; }
        void Int(int pos, int v) => BitConverter.GetBytes(v).CopyTo(b, pos);
        void Curto(int pos, short v) => BitConverter.GetBytes(v).CopyTo(b, pos);
        Texto(0, "RIFF"); Int(4, 36 + dados); Texto(8, "WAVE"); Texto(12, "fmt "); Int(16, 16); Curto(20, 1); Curto(22, 1);
        Int(24, taxa); Int(28, taxa * 2); Curto(32, 2); Curto(34, 16); Texto(36, "data"); Int(40, dados);
        MemoryMarshal.AsBytes(pcm.AsSpan()).CopyTo(b.AsSpan(44));
        return b;
    }

    public static double Rms(short[] pcm)
    {
        if (pcm.Length == 0) return 0;
        double soma = 0;
        foreach (var v in pcm) soma += (double)v * v;
        return Math.Sqrt(soma / pcm.Length);
    }

    private sealed class Lado
    {
        private readonly CapturaLegenda _dono;
        private readonly string _papel;
        private readonly List<short> _buf = new();
        private readonly object _trava = new();
        private int _taxa = 8000;
        private bool _falando;
        private int _silencioMs, _falaMs, _desdeProvisorioMs;
        private long _id;
        private double _inicio;

        public Lado(CapturaLegenda dono, string papel) { _dono = dono; _papel = papel; }

        public void Processar(short[] pcm, int taxa)
        {
            if (pcm.Length == 0 || taxa <= 0) return;
            (short[] amostras, int taxaFala, double inicio, long id, bool final)? emitir = null;
            lock (_trava)
            {
                _taxa = taxa;
                int ms = pcm.Length * 1000 / taxa;
                bool fala = Rms(pcm) >= _dono.LimiarFala;
                if (!_falando)
                {
                    if (!fala) return;     // silêncio fora da fala: nada a guardar
                    _falando = true; _silencioMs = 0; _falaMs = 0; _desdeProvisorioMs = 0;
                    _id = Interlocked.Increment(ref _dono._proximoId);
                    _inicio = Math.Max(0, _dono._relogio.Elapsed.TotalSeconds - ms / 1000.0);
                    _buf.Clear();
                }
                _buf.AddRange(pcm);
                if (fala) { _silencioMs = 0; _falaMs += ms; _desdeProvisorioMs += ms; }
                else _silencioMs += ms;

                if (_silencioMs >= _dono.PausaMs || _buf.Count * 1000L / taxa >= _dono.MaximoMs)
                {
                    // fim da fala (pausa, ou longa demais): sai o definitivo se houve fala suficiente
                    if (_falaMs >= _dono.MinimoMs) emitir = (_buf.ToArray(), taxa, _inicio, _id, true);
                    _falando = false; _buf.Clear();
                }
                else if (_desdeProvisorioMs >= _dono.ProvisorioMs && _falaMs >= _dono.MinimoMs)
                {
                    _desdeProvisorioMs = 0;
                    emitir = (_buf.ToArray(), taxa, _inicio, _id, false);
                }
            }
            if (emitir is { } e) _dono.Emitir(_papel, e.id, e.amostras, e.taxaFala, e.inicio, e.final);
        }

        public void Fechar()
        {
            (short[], int, double, long)? ultimo = null;
            lock (_trava)
            {
                if (_falando && _falaMs >= _dono.MinimoMs) ultimo = (_buf.ToArray(), _taxa, _inicio, _id);
                _falando = false; _buf.Clear();
            }
            if (ultimo is { } u) _dono.Emitir(_papel, u.Item4, u.Item1, u.Item2, u.Item3, true);   // chamada acabou no meio da fala: fecha o que ficou
        }
    }
}
