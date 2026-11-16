using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Softphone.Core;

public enum TipoToque { Nenhum, Campainha, ChamadaEmEspera, Retorno, Ocupado }

/// <summary>
/// Sons locais: campainha (dispositivo de toque), tom de retorno/ocupado e bipe de chamada em espera
/// (dispositivo de conversa). Tudo gerado por código, sem arquivos.
/// </summary>
public sealed class Toques : IDisposable
{
    private readonly object _trava = new();
    private WaveOutEvent? _saida;
    private TipoToque _atual;

    /// <summary>Índice WinMM onde toca a campainha (-1 = padrão). Útil para tocar no alto-falante do PC com headset.</summary>
    public int DispositivoCampainha { get; set; } = -1;
    /// <summary>Índice WinMM do headset/alto-falante da conversa (-1 = padrão).</summary>
    public int DispositivoConversa { get; set; } = -1;
    /// <summary>Volume da campainha, 0 a 1.</summary>
    public float VolumeCampainha { get; set; } = 0.6f;
    /// <summary>Desliga todos os sons locais (testes automáticos).</summary>
    public bool Silencioso { get; set; }
    /// <summary>Toque personalizado (.wav). Vazio ou inválido = toque padrão gerado.</summary>
    public string? ArquivoCampainha { get; set; }
    /// <summary>1.3: toque pronto da campainha (id de <see cref="Presets"/>) quando não há arquivo .wav. Vazio = clássico.</summary>
    public string? Preset { get; set; }

    /// <summary>1.3: opções de toque (todas geradas por código, sem arquivos): id e nome para a tela de Configurações.</summary>
    public static readonly (string Id, string Nome)[] Presets =
    {
        ("classico", "Clássico"), ("suave", "Suave"), ("duplo", "Duplo"), ("rapido", "Rápido"), ("digital", "Digital"), ("sino", "Sino"),
        // 1.2.7 (11/11/2026, pedido do Ana): mais opções para escolher
        ("telefone", "Telefone antigo"), ("marimba", "Marimba"), ("dingdong", "Ding-dong"), ("alerta", "Alerta"), ("melodia", "Melodia"), ("moderno", "Moderno"),
    };

    /// <summary>1.2.7: som das teclas do discador (e do retorno local dos tons enviados). "dtmf" é o de sempre; "nenhum" desliga.</summary>
    public static readonly (string Id, string Nome)[] SonsTecla =
    {
        ("dtmf", "Tons de telefone (padrão)"), ("clique", "Clique"), ("toc", "Toc de madeira"), ("bip", "Bip curto"), ("suave", "Bip suave"),
        ("gota", "Gota"), ("notas", "Notas musicais (cada tecla uma nota)"), ("nenhum", "Sem som"),
    };

    /// <summary>1.2.7: som da tecla (id de <see cref="SonsTecla"/>). Vazio = tons de telefone.</summary>
    public string? PresetTecla { get; set; }

    /// <summary>Duração de um ciclo do toque (ms): para testes e para a tela saber que o id existe.</summary>
    public static int DuracaoCicloMs(string? id) => PadraoCampainha(id).Sum(s => s.Ms);

    /// <summary>Duração do som de uma tecla (ms); 0 = sem som (nenhum, ou tecla que não existe).</summary>
    public static int DuracaoTeclaMs(string? id, char tecla) => PadraoTecla(id, tecla) is { } p ? p.Ms : 0;

    private static (Segmento[] Seg, float Volume, int Ms)? PadraoTecla(string? id, char tecla)
    {
        const string Teclas = "123456789*0#";
        if (Teclas.IndexOf(tecla) < 0) return null;
        (int baixa, int alta) = tecla switch
        {
            '1' => (697, 1209), '2' => (697, 1336), '3' => (697, 1477),
            '4' => (770, 1209), '5' => (770, 1336), '6' => (770, 1477),
            '7' => (852, 1209), '8' => (852, 1336), '9' => (852, 1477),
            '*' => (941, 1209), '0' => (941, 1336), _ => (941, 1477),
        };
        // notas de uma escala maior, da mais grave (1) à mais aguda (#)
        int[] notas = { 262, 294, 330, 349, 392, 440, 494, 523, 587, 659, 698, 784 };
        int nota = notas[Teclas.IndexOf(tecla)];
        return (string.IsNullOrWhiteSpace(id) ? "dtmf" : id) switch
        {
            "nenhum" => null,
            "clique" => (new[] { S(2400, 0, 14) }, 0.18f, 14),
            "toc" => (new[] { S(420, 0, 35) }, 0.22f, 35),
            "bip" => (new[] { S(1000, 0, 70) }, 0.14f, 70),
            "suave" => (new[] { S(660, 0, 90) }, 0.12f, 90),
            "gota" => (new[] { S(1400, 0, 20), S(900, 0, 45) }, 0.14f, 65),
            "notas" => (new[] { S(nota, 0, 160) }, 0.15f, 160),
            _ => (new[] { S(baixa, alta, 120) }, 0.15f, 120),   // dtmf (e qualquer id desconhecido)
        };
    }

    private static Segmento[] PadraoCampainha(string? id) => id switch
    {
        "telefone" => TelefoneAntigo(),
        "marimba" => new[] { S(523, 0, 140), S(659, 0, 140), S(784, 0, 140), S(1047, 0, 260), S(0, 0, 2000) },
        "dingdong" => new[] { S(988, 1976, 350), S(784, 1568, 500), S(0, 0, 2200) },
        "alerta" => new[] { S(880, 0, 180), S(660, 0, 180), S(880, 0, 180), S(660, 0, 180), S(0, 0, 1800) },
        "melodia" => new[] { S(659, 0, 160), S(784, 0, 160), S(880, 0, 160), S(784, 0, 160), S(659, 0, 320), S(0, 0, 2000) },
        "moderno" => new[] { S(1319, 0, 120), S(1760, 0, 200), S(0, 0, 150), S(1319, 0, 120), S(1760, 0, 200), S(0, 0, 2000) },
        "suave" => new[] { S(392, 523, 700), S(0, 0, 400), S(392, 523, 700), S(0, 0, 2400) },
        "duplo" => new[] { S(523, 659, 160), S(0, 0, 90), S(523, 659, 160), S(0, 0, 90), S(523, 659, 160), S(0, 0, 1800) },
        "rapido" => new[] { S(880, 0, 120), S(0, 0, 80), S(880, 0, 120), S(0, 0, 80), S(880, 0, 120), S(0, 0, 80), S(880, 0, 120), S(0, 0, 1200) },
        "digital" => new[] { S(1047, 1319, 180), S(0, 0, 60), S(1319, 1568, 180), S(0, 0, 60), S(1568, 2093, 260), S(0, 0, 1700) },
        "sino" => new[] { S(784, 1568, 90), S(659, 1319, 160), S(0, 0, 100), S(784, 1568, 90), S(659, 1319, 160), S(0, 0, 2200) },
        _ => new[] { S(480, 620, 400), S(0, 0, 200), S(480, 620, 400), S(0, 0, 2000) },
    };

    /// <summary>Trinado do telefone de mesa antigo: duas rajadas de tom alternado e uma pausa.</summary>
    private static Segmento[] TelefoneAntigo()
    {
        var l = new List<Segmento>();
        for (int rajada = 0; rajada < 2; rajada++)
        {
            for (int i = 0; i < 14; i++) { l.Add(S(i % 2 == 0 ? 1400 : 1000, 0, 28)); }
            l.Add(S(0, 0, 350));
        }
        l.Add(S(0, 0, 1700));
        return l.ToArray();
    }

    public TipoToque Atual { get { lock (_trava) return _atual; } }

    public void Tocar(TipoToque tipo)
    {
        lock (_trava)
        {
            if (_atual == tipo) return;
            PararInterno();
            _atual = tipo;
            if (tipo == TipoToque.Nenhum || Silencioso) return;

            // Brasil: tom de 425 Hz (retorno 1 s ligado / 4 s desligado; ocupado 250/250 ms).
            Segmento[] padrao = tipo switch
            {
                TipoToque.Campainha => PadraoCampainha(Preset),
                TipoToque.ChamadaEmEspera => new[] { S(425, 0, 200), S(0, 0, 150), S(425, 0, 200), S(0, 0, 3500) },
                TipoToque.Retorno => new[] { S(425, 0, 1000), S(0, 0, 4000) },
                TipoToque.Ocupado => new[] { S(425, 0, 250), S(0, 0, 250) },
                _ => Array.Empty<Segmento>(),
            };
            float volume = tipo == TipoToque.Campainha ? VolumeCampainha : 0.25f;
            int dispositivo = tipo == TipoToque.Campainha ? DispositivoCampainha : DispositivoConversa;
            bool repetir = tipo != TipoToque.Ocupado;
            int limiteMs = tipo == TipoToque.Ocupado ? 3000 : int.MaxValue;
            try
            {
                var saida = new WaveOutEvent { DeviceNumber = dispositivo, DesiredLatency = 150 };
                ISampleProvider? arquivo = null;
                if (tipo == TipoToque.Campainha && !string.IsNullOrWhiteSpace(ArquivoCampainha) && File.Exists(ArquivoCampainha))
                {
                    try { arquivo = new VolumeSampleProvider(new LoopWav(ArquivoCampainha).ToSampleProvider()) { Volume = volume }; }
                    catch { arquivo = null; } // arquivo inválido: usa o toque padrão
                }
                saida.Init(arquivo ?? new GeradorTom(padrao, volume, repetir, limiteMs));
                saida.Play();
                _saida = saida;
            }
            catch
            {
                // Sem placa de som / dispositivo removido: segue sem som, a tela continua avisando.
            }
        }
    }

    /// <summary>Retorno sonoro curto de uma tecla DTMF (não interrompe a campainha).</summary>
    public void Dtmf(char tecla)
    {
        if (Silencioso) return;
        if (PadraoTecla(PresetTecla, tecla) is not { } p) return;   // sem som, ou tecla que não existe
        try
        {
            var saida = new WaveOutEvent { DeviceNumber = DispositivoConversa, DesiredLatency = 100 };
            saida.Init(new GeradorTom(p.Seg, p.Volume, false, p.Ms));
            saida.PlaybackStopped += (_, _) => saida.Dispose();
            saida.Play();
        }
        catch { }
    }

    public void Parar() { lock (_trava) { PararInterno(); _atual = TipoToque.Nenhum; } }

    private void PararInterno()
    {
        var s = _saida;
        _saida = null;
        if (s == null) return;
        try { s.Stop(); } catch { }
        s.Dispose();
    }

    public void Dispose() => Parar();

    private static Segmento S(int f1, int f2, int ms) => new(f1, f2, ms);

    /// <summary>Toca um .wav em repetição (com 1 s de pausa entre as voltas).</summary>
    private sealed class LoopWav : WaveStream
    {
        private readonly WaveFileReader _r;
        private int _silencioRestante;
        public LoopWav(string caminho) { _r = new WaveFileReader(caminho); }
        public override WaveFormat WaveFormat => _r.WaveFormat;
        public override long Length => long.MaxValue;
        public override long Position { get => _r.Position; set => _r.Position = value; }
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_silencioRestante > 0)
            {
                int n = Math.Min(count, _silencioRestante);
                Array.Clear(buffer, offset, n);
                _silencioRestante -= n;
                return n;
            }
            int lidos = _r.Read(buffer, offset, count);
            if (lidos == 0)
            {
                _r.Position = 0;
                _silencioRestante = _r.WaveFormat.AverageBytesPerSecond / _r.WaveFormat.BlockAlign * _r.WaveFormat.BlockAlign;
                return Read(buffer, offset, count);
            }
            return lidos;
        }
        protected override void Dispose(bool disposing) { if (disposing) _r.Dispose(); base.Dispose(disposing); }
    }

    private readonly record struct Segmento(int F1, int F2, int Ms);

    /// <summary>Gera a sequência de tons (uma ou duas frequências por segmento) em float 8 kHz.</summary>
    private sealed class GeradorTom : ISampleProvider
    {
        private const int Taxa = 16000;
        private readonly Segmento[] _segmentos;
        private readonly float _volume;
        private readonly bool _repetir;
        private readonly long _limiteAmostras;
        private int _indice;
        private int _posNoSegmento;
        private long _total;
        private double _fase1, _fase2;

        public GeradorTom(Segmento[] segmentos, float volume, bool repetir, int limiteMs)
        {
            _segmentos = segmentos;
            _volume = volume;
            _repetir = repetir;
            _limiteAmostras = limiteMs == int.MaxValue ? long.MaxValue : (long)limiteMs * Taxa / 1000;
        }

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(Taxa, 1);

        public int Read(float[] buffer, int offset, int count)
        {
            int escritos = 0;
            while (escritos < count)
            {
                if (_total >= _limiteAmostras) return escritos;
                if (_indice >= _segmentos.Length)
                {
                    if (!_repetir) return escritos;
                    _indice = 0;
                }
                var seg = _segmentos[_indice];
                int tamanho = seg.Ms * Taxa / 1000;
                if (_posNoSegmento >= tamanho) { _indice++; _posNoSegmento = 0; continue; }

                float amostra = 0;
                if (seg.F1 > 0)
                {
                    // rampa de 5 ms no início/fim do segmento para não estalar
                    int rampa = Taxa / 200;
                    float env = Math.Min(1f, Math.Min(_posNoSegmento, tamanho - _posNoSegmento) / (float)rampa);
                    double v = Math.Sin(_fase1);
                    if (seg.F2 > 0) v = (v + Math.Sin(_fase2)) / 2;
                    amostra = (float)v * _volume * env;
                    _fase1 += 2 * Math.PI * seg.F1 / Taxa;
                    _fase2 += 2 * Math.PI * seg.F2 / Taxa;
                }
                buffer[offset + escritos] = amostra;
                escritos++;
                _posNoSegmento++;
                _total++;
            }
            return escritos;
        }
    }
}
