namespace Softphone.Core;

public enum NivelQualidade { SemDados, Boa, Regular, Ruim, SemAudio }

/// <summary>Qualidade da ligação agora (janela dos últimos ~5 s), medida no RTP que chega.</summary>
/// <param name="PerdaPct">Pacotes que não chegaram (%).</param>
/// <param name="JitterMs">Variação do atraso (RFC 3550).</param>
/// <param name="RttMs">Ida e volta, quando o PABX manda relatório RTCP (senão null).</param>
/// <param name="PerdaRemotaPct">Quanto do MEU áudio o outro lado perdeu (RTCP), se houver.</param>
/// <param name="Mos">Nota de 1 a 4,5 (modelo E simplificado).</param>
public sealed record QualidadeInstante(double PerdaPct, double JitterMs, double? RttMs, double? PerdaRemotaPct, double Mos, NivelQualidade Nivel, long Recebidos, long Perdidos)
{
    public static readonly QualidadeInstante Vazia = new(0, 0, null, null, 0, NivelQualidade.SemDados, 0, 0);

    public string Texto => Nivel switch
    {
        NivelQualidade.SemDados => "medindo...",
        NivelQualidade.SemAudio => "sem áudio chegando",
        _ => $"{NomeNivel(Nivel)} · nota {Mos:N1} · perda {PerdaPct:N1}% · variação {JitterMs:N0} ms" + (RttMs is double r ? $" · ida e volta {r:N0} ms" : ""),
    };

    public static string NomeNivel(NivelQualidade n) => n switch
    {
        NivelQualidade.Boa => "Boa",
        NivelQualidade.Regular => "Regular",
        NivelQualidade.Ruim => "Ruim",
        NivelQualidade.SemAudio => "Sem áudio",
        _ => "—",
    };
}

/// <summary>Resumo da ligação inteira (vai para o histórico e para o Hub).</summary>
/// <param name="Medido">1.3: false quando nenhum pacote de áudio chegou na ligação inteira. Aí perda/variação NÃO são medições
/// (os "100% de perda e 0 ms" eram o valor sintético de "sem áudio"): o painel mostra "Sem medição". Sentido medido: áudio recebido.</param>
public sealed record QualidadeResumo(double MosMedio, double MosMinimo, double PerdaPct, double JitterMedioMs, double? RttMedioMs, NivelQualidade Nivel, int Segundos, bool Medido = true);

/// <summary>
/// Mede perda, jitter e (com RTCP) ida e volta a partir dos pacotes RTP recebidos. Thread-safe: os pacotes chegam na
/// thread da biblioteca e a tela lê <see cref="Instante"/> uma vez por segundo.
/// </summary>
public sealed class MedidorQualidade
{
    private readonly object _t = new();
    private readonly Func<long> _relogioMs;
    private bool _iniciado;
    private ushort _ultimoSeq;
    private long _ciclos;            // voltas do número de sequência (16 bits)
    private long _primeiroExt, _maiorExt;
    private long _recebidos;
    private double _jitter;          // em unidades do relógio RTP
    private long _transitoAnt;
    private bool _temTransito;
    private int _relogioRtp = 8000;
    private long _ultimoPacoteMs;
    private double? _rtt, _perdaRemota;
    private readonly Queue<(long ms, long ext, long rec)> _historico = new();
    // para o resumo
    private double _somaMos, _minMos = double.MaxValue, _somaJitter, _somaRtt;
    private int _amostras, _amostrasRtt;
    private long? _primeiraMedicaoMs;   // primeiro Instante() (a chamada conectou): base para "nunca chegou áudio"
    /// <summary>Sem nenhum pacote por este tempo depois de conectar: "sem áudio" (antes ficava "sem dados" para sempre).</summary>
    public const int EsperaPrimeiroAudioMs = 6000;
    /// <summary>Em espera não conta "sem áudio" (o outro lado pode parar de mandar).</summary>
    public bool Pausado { get; set; }

    public MedidorQualidade(Func<long>? relogioMs = null)
    {
        _relogioMs = relogioMs ?? (() => Environment.TickCount64);
    }

    /// <summary>Um pacote RTP chegou.</summary>
    public void Pacote(ushort seq, uint timestamp, int relogioRtp = 8000)
    {
        long agora = _relogioMs();
        lock (_t)
        {
            _relogioRtp = relogioRtp > 0 ? relogioRtp : 8000;
            if (!_iniciado)
            {
                _iniciado = true; _ultimoSeq = seq; _primeiroExt = _maiorExt = seq;
            }
            else
            {
                int delta = (ushort)(seq - _ultimoSeq);
                if (delta > 0 && delta < 3000)
                {
                    if (seq < _ultimoSeq) _ciclos++;
                    _ultimoSeq = seq;
                    long ext = _ciclos * 65536 + seq;
                    if (ext > _maiorExt) _maiorExt = ext;
                }
                // atrasado/duplicado (delta 0 ou "negativo"): conta como recebido, não mexe na sequência
            }
            _recebidos++;
            // jitter RFC 3550: D = (Rj - Ri) - (Sj - Si), J += (|D| - J) / 16
            long transito = agora * _relogioRtp / 1000 - timestamp;
            if (_temTransito)
            {
                double d = Math.Abs(transito - _transitoAnt);
                if (d < _relogioRtp * 2) _jitter += (d - _jitter) / 16;  // ignora salto de timestamp (silêncio, troca de fluxo)
            }
            _transitoAnt = transito; _temTransito = true;
            _ultimoPacoteMs = agora;
        }
    }

    /// <summary>Relatório RTCP do outro lado sobre o áudio que EU mando.</summary>
    /// <param name="fracaoPerdida">Fração perdida (0-255 = 0-100%).</param>
    /// <param name="lsr">"Last SR" (32 bits do meio do NTP do meu último SR); 0 = sem RTT.</param>
    /// <param name="dlsr">Atraso desde aquele SR, em 1/65536 s.</param>
    public void RelatorioRemoto(byte fracaoPerdida, uint lsr, uint dlsr, DateTime? agoraUtc = null)
    {
        lock (_t)
        {
            _perdaRemota = fracaoPerdida * 100.0 / 256;
            if (lsr != 0)
            {
                uint agora = NtpMeio(agoraUtc ?? DateTime.UtcNow);
                uint rtt = agora - lsr - dlsr;              // em 1/65536 s (aritmética de 32 bits)
                double ms = rtt * 1000.0 / 65536;
                if (ms is > 0 and < 5000) _rtt = _rtt == null ? ms : _rtt * 0.7 + ms * 0.3;
            }
        }
    }

    public static uint NtpMeio(DateTime utc)
    {
        var desde1900 = utc - new DateTime(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        ulong seg = (ulong)desde1900.TotalSeconds;
        ulong frac = (ulong)((desde1900.TotalSeconds - Math.Floor(desde1900.TotalSeconds)) * 4294967296.0);
        return (uint)(((seg & 0xFFFF) << 16) | (frac >> 16));
    }

    /// <summary>Qualidade agora (e alimenta o resumo). Chame ~1 vez por segundo.</summary>
    public QualidadeInstante Instante()
    {
        long agora = _relogioMs();
        lock (_t)
        {
            _primeiraMedicaoMs ??= agora;
            if (!_iniciado)
            {
                if (Pausado || agora - _primeiraMedicaoMs < EsperaPrimeiroAudioMs) return QualidadeInstante.Vazia;
                SemAudioNoResumo();
                return new QualidadeInstante(100, 0, _rtt, _perdaRemota, 1, NivelQualidade.SemAudio, 0, 0);
            }
            if (!Pausado && agora - _ultimoPacoteMs > 4000)
            {
                // R11: segundo sem áudio entra no resumo como a pior nota (antes saía antes de contar, e uma ligação que
                // ficou muda continuava "Boa" no histórico e no painel da TI)
                SemAudioNoResumo();
                return new QualidadeInstante(100, 0, _rtt, _perdaRemota, 1, NivelQualidade.SemAudio, _recebidos, Perdidos());
            }

            _historico.Enqueue((agora, _maiorExt, _recebidos));
            while (_historico.Count > 1 && agora - _historico.Peek().ms > 5000) _historico.Dequeue();
            var (ms0, ext0, rec0) = _historico.Peek();
            long esperados = _maiorExt - ext0, chegaram = _recebidos - rec0;
            double perda = esperados > 0 ? Math.Clamp(100.0 * (esperados - chegaram) / esperados, 0, 100) : 0;
            double jitterMs = _jitter * 1000 / _relogioRtp;
            double mos = Mos(perda, jitterMs, _rtt);
            var nivel = Classificar(mos);
            if (!Pausado)
            {
                _somaMos += mos; _minMos = Math.Min(_minMos, mos); _somaJitter += jitterMs; _amostras++;
                if (_rtt is double r) { _somaRtt += r; _amostrasRtt++; }
            }
            return new QualidadeInstante(perda, jitterMs, _rtt, _perdaRemota, mos, nivel, _recebidos, Perdidos());
        }
    }

    private long Perdidos() => Math.Max(0, _maiorExt - _primeiroExt + 1 - _recebidos);

    private void SemAudioNoResumo() { _somaMos += 1; _minMos = 1; _amostras++; }

    /// <summary>Resumo da ligação (null se não houve medição).</summary>
    public QualidadeResumo? Resumo()
    {
        lock (_t)
        {
            if (_amostras == 0) return null;
            long esperados = _maiorExt - _primeiroExt + 1;
            double perda = !_iniciado ? 100 : esperados > 0 ? Math.Clamp(100.0 * Perdidos() / esperados, 0, 100) : 0;
            double mosMedio = _somaMos / _amostras;
            return new QualidadeResumo(mosMedio, _minMos, perda, _somaJitter / _amostras, _amostrasRtt > 0 ? _somaRtt / _amostrasRtt : null, Classificar(mosMedio), _amostras, Medido: _iniciado);
        }
    }

    /// <summary>
    /// Modelo E simplificado (G.711): atraso efetivo = ida (RTT/2, ou 40 ms se não souber) + 2 × jitter + 10;
    /// R = 93,2 − penalidade do atraso − 2,5 × perda%; MOS = 1 + 0,035R + 7×10⁻⁶·R(R−60)(100−R).
    /// </summary>
    public static double Mos(double perdaPct, double jitterMs, double? rttMs)
    {
        double ida = rttMs is double r ? r / 2 : 40;
        double efetivo = ida + jitterMs * 2 + 10;
        double rr = 93.2 - (efetivo < 160 ? efetivo / 40 : (efetivo - 120) / 10) - 2.5 * perdaPct;
        rr = Math.Clamp(rr, 0, 100);
        double mos = 1 + 0.035 * rr + 0.000007 * rr * (rr - 60) * (100 - rr);
        return Math.Clamp(mos, 1, 4.5);
    }

    /// <summary>Boa ≥ 4,1 (até ~4% de perda), Regular ≥ 3,6 (até ~8%), abaixo disso Ruim.</summary>
    public static NivelQualidade Classificar(double mos) => mos >= 4.1 ? NivelQualidade.Boa : mos >= 3.6 ? NivelQualidade.Regular : NivelQualidade.Ruim;
}
