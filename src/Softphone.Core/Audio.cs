using System.Net;
using System.Runtime.InteropServices;
using NAudio.Wave;
using SIPSorcery.Media;
using SIPSorceryMedia.Abstractions;

namespace Softphone.Core;

/// <summary>Fonte + saída de áudio de UMA chamada (cada chamada tem a sua).</summary>
public interface IAudioChamada : IAudioSource, IAudioSink, IDisposable
{
    /// <summary>Mudo: continua mandando RTP (silêncio) para não perder o NAT.</summary>
    bool Mudo { get; set; }
    /// <summary>Em espera: não manda o microfone nem toca o que chega.</summary>
    bool Pausado { get; set; }
    /// <summary>Pacotes RTP de áudio recebidos (diagnóstico / testes).</summary>
    long PacotesRecebidos { get; }
    /// <summary>Quadros de áudio enviados (diagnóstico / testes).</summary>
    long QuadrosEnviados { get; }
    /// <summary>Quando não é nulo, os dois lados da conversa vão para ele.</summary>
    GravadorChamada? Gravador { get; set; }
    /// <summary>1.3: quando não é nulo, recebe os dois lados para a legenda ao vivo (nada é gravado).</summary>
    CapturaLegenda? Legenda { get; set; }
    /// <summary>Mistura um aviso falado (PCM 16 kHz) no áudio que sai e no que toca aqui.</summary>
    void TocarAviso(short[] pcm16k);
    /// <summary>
    /// Conferência a três: esta chamada vira a "principal" (usa o microfone e o alto-falante daqui) e a outra passa a usar
    /// os dela. Cada lado ouve a minha voz e a do outro; eu ouço os dois.
    /// </summary>
    void IniciarConferencia(IAudioChamada outra);
    /// <summary>Desfaz a conferência (cada chamada volta a usar o próprio áudio).</summary>
    void EncerrarConferencia();
    bool EmConferencia { get; }
    /// <summary>Como está o tratamento de voz (eco/ruído), para o log.</summary>
    string DescricaoVoz { get; }
}

public interface IFabricaAudio
{
    IAudioChamada Criar();
}

/// <summary>Codecs oferecidos, na ordem de preferência. G.711 A-law primeiro (padrão no Brasil).</summary>
public static class Codecs
{
    /// <summary>Nomes aceitos na configuração, na ordem padrão.</summary>
    public static readonly string[] Todos = { "PCMA", "PCMU", "G722" };

    public static List<AudioFormat> Padrao() => De(Todos);

    /// <summary>Lista de formatos na ordem dada (ignora nomes desconhecidos; vazio = padrão).</summary>
    public static List<AudioFormat> De(IEnumerable<string>? nomes)
    {
        var lista = new List<AudioFormat>();
        foreach (var n in nomes ?? Array.Empty<string>())
        {
            switch (n.Trim().ToUpperInvariant())
            {
                case "PCMA": lista.Add(new AudioFormat(SDPWellKnownMediaFormatsEnum.PCMA)); break;
                case "PCMU": lista.Add(new AudioFormat(SDPWellKnownMediaFormatsEnum.PCMU)); break;
                case "G722": lista.Add(new AudioFormat(SDPWellKnownMediaFormatsEnum.G722)); break;
            }
        }
        return lista.Count == 0 ? De(Todos) : lista;
    }

    public static string Descricao(string nome) => nome switch
    {
        "PCMA" => "G.711 A-law (padrão no Brasil)",
        "PCMU" => "G.711 µ-law",
        "G722" => "G.722 (voz em alta definição, se o PABX aceitar)",
        _ => nome,
    };
}

/// <summary>
/// Base comum: formatos, codificação e eventos que a SIPSorcery espera de uma fonte/saída de áudio.
/// As subclasses só precisam entregar PCM 16 bits mono (EnviarPcm) e tocar o PCM recebido (TocarPcm).
/// </summary>
public abstract class AudioChamadaBase : IAudioChamada
{
    private readonly AudioEncoder _codificador = new();
    private List<AudioFormat> _formatos = Codecs.Padrao();
    protected AudioFormat FormatoFonte { get; private set; }
    protected AudioFormat FormatoSaida { get; private set; }
    private int _modoRecepcao; // 0 = ainda não sabe, 1 = GotAudioRtp, 2 = GotEncodedMediaFrame
    private long _recebidos, _enviados;

    public bool Mudo { get; set; }
    public bool Pausado { get; set; }
    public long PacotesRecebidos => Interlocked.Read(ref _recebidos);
    public long QuadrosEnviados => Interlocked.Read(ref _enviados);
    public GravadorChamada? Gravador { get; set; }
    public CapturaLegenda? Legenda { get; set; }

    private short[]? _aviso;
    private int _posAvisoEnvio, _posAvisoLocal;
    public void TocarAviso(short[] pcm16k) { _posAvisoEnvio = 0; _posAvisoLocal = 0; _aviso = pcm16k.Length > 0 ? pcm16k : null; }

    public event EncodedSampleDelegate? OnAudioSourceEncodedSample;
    public event Action<EncodedAudioFrame>? OnAudioSourceEncodedFrameReady;
#pragma warning disable CS0067 // exigido pela interface; não geramos amostras cruas
    public event RawAudioSampleDelegate? OnAudioSourceRawSample;
#pragma warning restore CS0067
    public event SourceErrorDelegate? OnAudioSourceError;
    public event SourceErrorDelegate? OnAudioSinkError;

    protected AudioChamadaBase() : this(null) { }

    protected AudioChamadaBase(List<AudioFormat>? formatos)
    {
        if (formatos is { Count: > 0 }) _formatos = formatos;
        FormatoFonte = _formatos[0];
        FormatoSaida = _formatos[0];
    }

    // ---------- fonte (microfone -> RTP) ----------
    public List<AudioFormat> GetAudioSourceFormats() => _formatos;
    public void SetAudioSourceFormat(AudioFormat audioFormat) { FormatoFonte = audioFormat; AoMudarFormatoFonte(); }
    public void RestrictFormats(Func<AudioFormat, bool> filter) => _formatos = _formatos.Where(filter).ToList();
    public void ExternalAudioSourceRawSample(AudioSamplingRatesEnum samplingRate, uint durationMilliseconds, short[] sample) => EnviarPcm(sample);
    public bool HasEncodedAudioSubscribers() => OnAudioSourceEncodedSample != null || OnAudioSourceEncodedFrameReady != null;
    public bool IsAudioSourcePaused() => Pausado;
    public Task PauseAudio() { Pausado = true; return Task.CompletedTask; }
    public Task ResumeAudio() { Pausado = false; return Task.CompletedTask; }
    public abstract Task StartAudio();
    public abstract Task CloseAudio();
    protected virtual void AoMudarFormatoFonte() { }

    /// <summary>Taxa de amostragem do PCM que a fonte deve entregar (8 kHz, ou 16 kHz no G.722).</summary>
    protected int TaxaFonte => FormatoFonte.ClockRate;
    protected int TaxaSaida => FormatoSaida.ClockRate;

    // ---------- conferência a três ----------
    private volatile AudioChamadaBase? _principal;   // não nulo: esta é a "segunda" e usa o áudio da principal
    private volatile AudioChamadaBase? _segunda;     // não nulo: esta é a principal
    private readonly FilaPcm _recebidoConf = new();  // o que chegou do outro lado, para a outra perna ouvir
    private short _antConf;
    public bool EmConferencia => _principal != null || _segunda != null;
    public virtual string DescricaoVoz => "sem tratamento de voz";

    public void IniciarConferencia(IAudioChamada outra)
    {
        if (outra is not AudioChamadaBase b || b == this) throw new ArgumentException("Áudio de conferência inválido.");
        _recebidoConf.Limpar(); b._recebidoConf.Limpar();
        b._principal = this;
        _segunda = b;
    }

    public void EncerrarConferencia()
    {
        var b = _segunda; var p = _principal;
        _segunda = null; _principal = null;
        if (b != null) b._principal = null;
        if (p != null) p._segunda = null;
        _recebidoConf.Limpar();
    }

    /// <summary>Na conferência, a principal toca aqui o áudio que chegou na segunda (mistura com o dela).</summary>
    protected virtual void TocarExtra(short[] pcm, int taxa) { }

    /// <summary>Codifica e entrega um quadro de PCM capturado.</summary>
    protected void EnviarPcm(short[] pcm)
    {
        if (pcm.Length == 0 || FormatoFonte.IsEmpty()) return;
        if (_principal != null) return; // na conferência, a segunda usa o microfone da principal
        if (Mudo || Pausado) pcm = new short[pcm.Length];
        var segunda = _segunda;
        // aviso de gravação: o outro lado ouve mesmo com o microfone no mudo (só não na espera). Na conferência, quem grava
        // uma perna grava as duas vozes, então o aviso de qualquer perna é misturado antes de repartir o áudio e chega
        // às duas (R04: antes a 2ª perna ficava sem o aviso).
        if (!Pausado)
        {
            MisturarAvisoDe(this, pcm);
            if (segunda != null && !segunda.Pausado) MisturarAvisoDe(segunda, pcm);
        }
        if (segunda != null)
        {
            // a outra perna ouve a minha voz + quem está nesta perna; esta perna ouve a minha voz + quem está na outra
            segunda.EnviarDaConferencia(pcm, TaxaFonte, _recebidoConf);
            segunda._recebidoConf.MisturarEm(pcm, TaxaFonte);
        }
        Gravador?.Eu(pcm, TaxaFonte); Legenda?.Eu(pcm, TaxaFonte);
        Codificar(pcm);
    }

    private void MisturarAvisoDe(AudioChamadaBase dono, short[] pcm)
    {
        var aviso = dono._aviso;
        if (aviso != null && dono._posAvisoEnvio < aviso.Length) AvisoGravacao.Misturar(pcm, TaxaFonte, aviso, ref dono._posAvisoEnvio);
    }

    private void EnviarDaConferencia(short[] vozPrincipal, int taxa, FilaPcm recebidoPrincipal)
    {
        if (FormatoFonte.IsEmpty()) return;
        var pcm = Pausado ? new short[vozPrincipal.Length * TaxaFonte / taxa]
            : (short[])GravadorChamada.Reamostrar(vozPrincipal, taxa, TaxaFonte, ref _antConf).Clone();
        if (!Pausado) recebidoPrincipal.MisturarEm(pcm, TaxaFonte);
        Gravador?.Eu(pcm, TaxaFonte); Legenda?.Eu(pcm, TaxaFonte);
        Codificar(pcm);
    }

    private void Codificar(short[] pcm)
    {
        byte[] codificado;
        lock (_codificador) codificado = _codificador.EncodeAudio(pcm, FormatoFonte);
        uint duracaoRtp = (uint)(pcm.Length * (long)FormatoFonte.RtpClockRate / FormatoFonte.ClockRate);
        var quadro = OnAudioSourceEncodedFrameReady;
        if (quadro != null)
        {
            uint ms = (uint)(pcm.Length * 1000L / FormatoFonte.ClockRate);
            quadro(new EncodedAudioFrame(0, FormatoFonte, ms, codificado));
        }
        else
        {
            OnAudioSourceEncodedSample?.Invoke(duracaoRtp, codificado);
        }
        Interlocked.Increment(ref _enviados);
    }

    protected void ErroFonte(string msg) => OnAudioSourceError?.Invoke(msg);
    protected void ErroSaida(string msg) => OnAudioSinkError?.Invoke(msg);

    // ---------- saída (RTP -> alto-falante) ----------
    public List<AudioFormat> GetAudioSinkFormats() => _formatos;
    public void SetAudioSinkFormat(AudioFormat audioFormat) { FormatoSaida = audioFormat; AoMudarFormatoSaida(); }
    public abstract Task StartAudioSink();
    public abstract Task CloseAudioSink();
    public Task PauseAudioSink() => Task.CompletedTask;
    public Task ResumeAudioSink() => Task.CompletedTask;
    protected virtual void AoMudarFormatoSaida() { }

    public void GotAudioRtp(IPEndPoint remoteEndPoint, uint ssrc, uint seqnum, uint timestamp, int payloadID, bool marker, byte[] payload)
    {
        if (Interlocked.CompareExchange(ref _modoRecepcao, 1, 0) == 2) return;
        if (payloadID != FormatoSaida.FormatID) return; // ex.: 101 = telephone-event (DTMF)
        Receber(payload, FormatoSaida);
    }

    public void GotEncodedMediaFrame(EncodedAudioFrame frame)
    {
        if (Interlocked.CompareExchange(ref _modoRecepcao, 2, 0) == 1) return;
        var formato = frame.AudioFormat.IsEmpty() ? FormatoSaida : frame.AudioFormat;
        if (formato.Codec == AudioCodecsEnum.PCMU || formato.Codec == AudioCodecsEnum.PCMA || formato.Codec == AudioCodecsEnum.G722)
            Receber(frame.EncodedAudio, formato);
    }

    private void Receber(byte[] payload, AudioFormat formato)
    {
        Interlocked.Increment(ref _recebidos);
        if (Pausado || payload.Length == 0) return;
        try
        {
            short[] pcm;
            lock (_codificador) pcm = _codificador.DecodeAudio(payload, formato);
            Gravador?.Outro(pcm, formato.ClockRate); Legenda?.Outro(pcm, formato.ClockRate);
            var principal = _principal;
            if (principal != null || _segunda != null) _recebidoConf.Adicionar(pcm, formato.ClockRate);
            // eu também ouço o aviso, inclusive quando a gravação começou na 2ª perna da conferência
            var aviso = _aviso;
            if (aviso != null && _posAvisoLocal < aviso.Length)
            {
                pcm = (short[])pcm.Clone();
                AvisoGravacao.Misturar(pcm, formato.ClockRate, aviso, ref _posAvisoLocal);
            }
            if (principal != null) { principal.TocarExtra(pcm, formato.ClockRate); return; }
            TocarPcm(pcm, formato.ClockRate);
        }
        catch (Exception ex)
        {
            ErroSaida("Falha ao decodificar áudio: " + ex.Message);
        }
    }

    protected abstract void TocarPcm(short[] pcm, int taxa);

    public virtual void Dispose()
    {
        EncerrarConferencia();
        lock (_codificador) _codificador.Dispose();
    }
}

/// <summary>Fila curta de PCM (até 300 ms) que uma perna da conferência enche e a outra consome, na taxa que precisar.</summary>
internal sealed class FilaPcm
{
    private readonly object _t = new();
    private short[] _buf = new short[4800];
    private int _ini, _n, _taxa;
    private short _ant;

    public void Limpar() { lock (_t) { _ini = 0; _n = 0; } }

    public void Adicionar(short[] pcm, int taxa)
    {
        lock (_t)
        {
            if (taxa != _taxa) { _taxa = taxa; _ini = 0; _n = 0; _buf = new short[taxa * 3 / 10]; }
            foreach (var s in pcm)
            {
                if (_n == _buf.Length) { _ini = (_ini + 1) % _buf.Length; _n--; } // cheia: descarta o mais velho
                _buf[(_ini + _n) % _buf.Length] = s;
                _n++;
            }
        }
    }

    /// <summary>Soma (com saturação) o próximo pedaço da fila em <paramref name="destino"/>; o que faltar é silêncio.</summary>
    public void MisturarEm(short[] destino, int taxaDestino)
    {
        short[] pedaco;
        int taxa;
        lock (_t)
        {
            taxa = _taxa;
            if (taxa == 0 || _n == 0) return;
            int precisa = (int)((long)destino.Length * taxa / taxaDestino);
            pedaco = new short[precisa];
            int m = Math.Min(precisa, _n);
            for (int i = 0; i < m; i++) pedaco[i] = _buf[(_ini + i) % _buf.Length];
            _ini = (_ini + m) % _buf.Length; _n -= m;
        }
        var conv = GravadorChamada.Reamostrar(pedaco, taxa, taxaDestino, ref _ant);
        int k = Math.Min(conv.Length, destino.Length);
        for (int i = 0; i < k; i++) destino[i] = (short)Math.Clamp(destino[i] + conv[i], short.MinValue, short.MaxValue);
    }
}

/// <summary>Áudio real: microfone e alto-falante pelo WinMM (NAudio), com cancelamento de eco e redução de ruído.</summary>
public sealed class AudioNAudio : AudioChamadaBase
{
    private readonly int _dispositivoEntrada;
    private readonly int _dispositivoSaida;
    private readonly object _trava = new();
    private WaveInEvent? _entrada;
    private WaveOutEvent? _saida;
    private SaidaMisturada? _mistura;
    private int _taxaBuffer;
    private bool _fonteIniciada;
    private bool _fechado;
    private readonly float _ganho, _volume;
    private readonly OpcoesVoz? _opcoesVoz;
    private volatile ProcessadorVoz? _voz;
    private short _antExtra;

    /// <param name="dispositivoEntrada">Índice WinMM do microfone (-1 = padrão do Windows).</param>
    /// <param name="dispositivoSaida">Índice WinMM do alto-falante/headset (-1 = padrão do Windows).</param>
    /// <param name="ganho">Ganho do microfone (1 = normal).</param>
    /// <param name="volume">Volume da conversa (1 = normal).</param>
    /// <param name="voz">Cancelamento de eco / ruído / volume automático (null = nada).</param>
    public AudioNAudio(int dispositivoEntrada, int dispositivoSaida, List<AudioFormat>? codecs = null, float ganho = 1f, float volume = 1f, OpcoesVoz? voz = null) : base(codecs)
    {
        _dispositivoEntrada = dispositivoEntrada;
        _dispositivoSaida = dispositivoSaida;
        _ganho = ganho;
        _volume = volume;
        _opcoesVoz = voz;
    }

    public override string DescricaoVoz
    {
        get
        {
            if (_opcoesVoz is not { Ativo: true }) return "tratamento de voz desligado";
            var v = _voz;
            if (v == null) return "tratamento de voz indisponível" + (ProcessadorVoz.ErroCarga is { } e ? ": " + e : "");
            var o = v.Opcoes;
            return $"eco {(o.CancelarEco ? "ligado" : "desligado")}, ruído {o.Ruido}, volume automático {(o.VolumeAutomatico ? "ligado" : "desligado")} ({v.Taxa / 1000} kHz, {v.Quadros} quadros, {v.Erros} erros)";
        }
    }

    private static void Aplicar(short[] pcm, float fator)
    {
        if (Math.Abs(fator - 1f) < 0.01f) return;
        for (int i = 0; i < pcm.Length; i++) pcm[i] = (short)Math.Clamp(pcm[i] * fator, short.MinValue, short.MaxValue);
    }

    public override Task StartAudio()
    {
        lock (_trava)
        {
            if (_fechado || _fonteIniciada) return Task.CompletedTask;
            _fonteIniciada = true;
            AbrirMicrofone();
        }
        return Task.CompletedTask;
    }

    protected override void AoMudarFormatoFonte()
    {
        lock (_trava)
        {
            if (_fonteIniciada && _entrada != null && _entrada.WaveFormat.SampleRate != TaxaFonte)
            {
                FecharMicrofone();
                AbrirMicrofone();
            }
        }
    }

    private void AbrirMicrofone()
    {
        try
        {
            var antigo = _voz;
            _voz = ProcessadorVoz.Criar(TaxaFonte, _opcoesVoz);
            antigo?.Dispose();
            var mic = new WaveInEvent
            {
                DeviceNumber = _dispositivoEntrada,
                WaveFormat = new WaveFormat(TaxaFonte, 16, 1),
                BufferMilliseconds = 20,
                NumberOfBuffers = 4,
            };
            mic.DataAvailable += (_, e) =>
            {
                if (e.BytesRecorded <= 0) return;
                var pcm = new short[e.BytesRecorded / 2];
                Buffer.BlockCopy(e.Buffer, 0, pcm, 0, pcm.Length * 2);
                _voz?.Processar(pcm);  // eco e ruído saem antes do ganho (menos chance de saturar)
                Aplicar(pcm, _ganho);
                EnviarPcm(pcm);
            };
            mic.RecordingStopped += (_, e) => { if (e.Exception != null) ErroFonte("Microfone parou: " + e.Exception.Message); };
            mic.StartRecording();
            _entrada = mic;
        }
        catch (Exception ex)
        {
            ErroFonte(DispositivosAudio.ExplicarErroMicrofone(ex));
        }
    }

    private void FecharMicrofone()
    {
        var mic = _entrada;
        _entrada = null;
        if (mic == null) return;
        try { mic.StopRecording(); } catch { }
        mic.Dispose();
    }

    public override Task StartAudioSink() => Task.CompletedTask; // abre no primeiro pacote (já sabe a taxa)

    protected override void TocarPcm(short[] pcm, int taxa) => Tocar(pcm, taxa, extra: false);

    /// <summary>Conferência: o áudio da outra perna toca junto, no mesmo alto-falante (e entra na referência do eco).</summary>
    protected override void TocarExtra(short[] pcm, int taxa) => Tocar(pcm, taxa, extra: true);

    private void Tocar(short[] pcm, int taxa, bool extra)
    {
        SaidaMisturada? mistura;
        lock (_trava)
        {
            if (_fechado) return;
            // a saída segue a taxa desta chamada; o áudio extra é convertido para ela
            if (_mistura == null || (!extra && _taxaBuffer != taxa)) AbrirSaida(extra ? Math.Max(TaxaSaida, 8000) : taxa);
            mistura = _mistura;
        }
        if (mistura == null) return;
        var buffer = extra ? mistura.Extra : mistura.Principal;
        if (extra && taxa != mistura.Taxa) pcm = GravadorChamada.Reamostrar(pcm, taxa, mistura.Taxa, ref _antExtra);
        else pcm = (short[])pcm.Clone();
        // Se acumulou atraso (rede engasgou e depois despejou tudo), descarta para não ficar com eco atrasado.
        if (buffer.BufferedDuration > TimeSpan.FromMilliseconds(400)) buffer.ClearBuffer();
        Aplicar(pcm, _volume);
        var bytes = new byte[pcm.Length * 2];
        Buffer.BlockCopy(pcm, 0, bytes, 0, bytes.Length);
        buffer.AddSamples(bytes, 0, bytes.Length);
    }

    private void AbrirSaida(int taxa)
    {
        FecharSaida();
        try
        {
            var mistura = new SaidaMisturada(taxa, () => _voz);
            _saida = new WaveOutEvent { DeviceNumber = _dispositivoSaida, DesiredLatency = 120, NumberOfBuffers = 3 };
            _saida.Init(mistura);
            _saida.Play();
            _mistura = mistura;
            _taxaBuffer = taxa;
        }
        catch (Exception ex)
        {
            _mistura = null;
            ErroSaida("Não foi possível abrir o alto-falante: " + ex.Message);
        }
    }

    private void FecharSaida()
    {
        var saida = _saida;
        _saida = null;
        _mistura = null;
        if (saida == null) return;
        try { saida.Stop(); } catch { }
        saida.Dispose();
    }

    public override Task CloseAudio()
    {
        lock (_trava) { FecharMicrofone(); _fonteIniciada = false; }
        return Task.CompletedTask;
    }

    public override Task CloseAudioSink()
    {
        lock (_trava) FecharSaida();
        return Task.CompletedTask;
    }

    public override void Dispose()
    {
        lock (_trava)
        {
            _fechado = true;
            FecharMicrofone();
            FecharSaida();
        }
        _voz?.Dispose();
        base.Dispose();
    }

    /// <summary>
    /// O que o alto-falante toca: a chamada + (na conferência) a outra perna. No momento em que a placa de som pede
    /// o áudio, a mesma amostra vai para o cancelador de eco como referência — assim o atraso até o microfone é estável.
    /// </summary>
    private sealed class SaidaMisturada : IWaveProvider
    {
        private readonly Func<ProcessadorVoz?> _voz;
        private byte[] _tmp = Array.Empty<byte>();
        public BufferedWaveProvider Principal { get; }
        public BufferedWaveProvider Extra { get; }
        public WaveFormat WaveFormat { get; }
        public int Taxa => WaveFormat.SampleRate;

        public SaidaMisturada(int taxa, Func<ProcessadorVoz?> voz)
        {
            _voz = voz;
            WaveFormat = new WaveFormat(taxa, 16, 1);
            Principal = new BufferedWaveProvider(WaveFormat) { BufferDuration = TimeSpan.FromSeconds(1), DiscardOnBufferOverflow = true };
            Extra = new BufferedWaveProvider(WaveFormat) { BufferDuration = TimeSpan.FromSeconds(1), DiscardOnBufferOverflow = true };
        }

        public int Read(byte[] buffer, int offset, int count)
        {
            count &= ~1;
            Principal.Read(buffer, offset, count); // ReadFully: completa com silêncio
            if (Extra.BufferedBytes > 0)
            {
                if (_tmp.Length < count) _tmp = new byte[count];
                Extra.Read(_tmp, 0, count);
                var a = MemoryMarshal.Cast<byte, short>(buffer.AsSpan(offset, count));
                var b = MemoryMarshal.Cast<byte, short>(_tmp.AsSpan(0, count));
                for (int i = 0; i < a.Length; i++) a[i] = (short)Math.Clamp(a[i] + b[i], short.MinValue, short.MaxValue);
            }
            _voz()?.Referencia(MemoryMarshal.Cast<byte, short>(buffer.AsSpan(offset, count)), Taxa);
            return count;
        }
    }
}

public sealed class FabricaAudioNAudio : IFabricaAudio
{
    public int Entrada { get; set; } = -1;
    public int Saida { get; set; } = -1;
    /// <summary>Codecs na ordem de preferência (vazio = padrão).</summary>
    public List<string> Codecs { get; set; } = new();
    public float Ganho { get; set; } = 1f;
    public float Volume { get; set; } = 1f;
    public OpcoesVoz Voz { get; set; } = new();
    public IAudioChamada Criar() => new AudioNAudio(Entrada, Saida, Softphone.Core.Codecs.De(Codecs), Ganho, Volume,
        new OpcoesVoz { CancelarEco = Voz.CancelarEco, Ruido = Voz.Ruido, VolumeAutomatico = Voz.VolumeAutomatico });
}

/// <summary>Áudio sintético (tom) sem placa de som. Usado nos testes automáticos; mede as frequências que chegam.</summary>
public sealed class AudioTeste : AudioChamadaBase
{
    private Timer? _relogio;
    private double _fase;
    private readonly object _tAnalise = new();
    private short[] _anel = new short[4000];
    private int _posAnel, _taxaAnel = 8000;
    private long _extras;

    public AudioTeste(double frequencia = 440) { Frequencia = frequencia; }

    /// <summary>Volume do tom (0 = silêncio: o RTP continua indo, só não faz barulho).</summary>
    public double Amplitude { get; set; } = 8000;
    /// <summary>Frequência do tom que este lado "fala".</summary>
    public double Frequencia { get; set; }
    /// <summary>Amostras tocadas como "outra perna" da conferência.</summary>
    public long AmostrasExtras => Interlocked.Read(ref _extras);

    public override Task StartAudio()
    {
        _relogio ??= new Timer(_ =>
        {
            int n = TaxaFonte / 50; // 20 ms
            var pcm = new short[n];
            for (int i = 0; i < n; i++)
            {
                pcm[i] = (short)(Math.Sin(_fase) * Amplitude);
                _fase += 2 * Math.PI * Frequencia / TaxaFonte;
            }
            EnviarPcm(pcm);
        }, null, 0, 20);
        return Task.CompletedTask;
    }

    public override Task CloseAudio() { _relogio?.Dispose(); _relogio = null; return Task.CompletedTask; }
    public override Task StartAudioSink() => Task.CompletedTask;
    public override Task CloseAudioSink() => Task.CompletedTask;

    protected override void TocarPcm(short[] pcm, int taxa)
    {
        lock (_tAnalise)
        {
            if (taxa != _taxaAnel) { _taxaAnel = taxa; _anel = new short[taxa / 2]; _posAnel = 0; }
            foreach (var s in pcm) { _anel[_posAnel] = s; _posAnel = (_posAnel + 1) % _anel.Length; }
        }
    }

    protected override void TocarExtra(short[] pcm, int taxa) => Interlocked.Add(ref _extras, pcm.Length);

    /// <summary>Amplitude de uma frequência no áudio recebido nos últimos 0,5 s (Goertzel; tom de 8000 dá ~8000).</summary>
    public double NivelRecebido(double freq)
    {
        short[] x; int taxa;
        lock (_tAnalise) { x = (short[])_anel.Clone(); taxa = _taxaAnel; }
        double coef = 2 * Math.Cos(2 * Math.PI * freq / taxa), s1 = 0, s2 = 0;
        foreach (var v in x) { double s = v + coef * s1 - s2; s2 = s1; s1 = s; }
        double p = s1 * s1 + s2 * s2 - coef * s1 * s2;
        return 2 * Math.Sqrt(Math.Max(0, p)) / x.Length;
    }

    public override void Dispose() { _relogio?.Dispose(); base.Dispose(); }
}

public sealed class FabricaAudioTeste : IFabricaAudio
{
    public double Frequencia { get; set; } = 440;
    public IAudioChamada Criar() => new AudioTeste(Frequencia);
}

/// <summary>Lista os dispositivos de áudio do WinMM (o índice é o que vai para a fábrica).</summary>
public static class DispositivosAudio
{
    public static IReadOnlyList<string> Microfones()
    {
        var lista = new List<string>();
        for (int i = 0; i < WaveInEvent.DeviceCount; i++) lista.Add(WaveInEvent.GetCapabilities(i).ProductName);
        return lista;
    }

    public static IReadOnlyList<string> AltoFalantes()
    {
        var lista = new List<string>();
        int total = NAudio.Wave.WaveInterop.waveOutGetNumDevs();
        for (int i = 0; i < total; i++)
        {
            var caps = new WaveOutCapabilities();
            NAudio.Wave.WaveInterop.waveOutGetDevCaps((IntPtr)i, out caps, System.Runtime.InteropServices.Marshal.SizeOf(caps));
            lista.Add(caps.ProductName);
        }
        return lista;
    }

    /// <summary>
    /// Mensagem para o usuário quando o microfone não abre. "InvalidParameter" em TODOS os formatos é o sintoma
    /// de bloqueio por software de segurança (visto em 04/11/2026: o Kaspersky barra .exe novo/sem assinatura;
    /// o mesmo código rodando pelo dotnet.exe assinado abre normalmente).
    /// </summary>
    public static string ExplicarErroMicrofone(Exception ex)
    {
        var msg = ex.Message ?? "";
        if (msg.Contains("InvalidParameter", StringComparison.OrdinalIgnoreCase) || msg.Contains("NotAllowed", StringComparison.OrdinalIgnoreCase))
        {
            // 05/11: a 1.1.4 aberta da pasta de distribuição caiu aqui. O antivírus libera o microfone só para o Softphone
            // instalado em Program Files; aberto de outra pasta, a dica certa é "abra pelo atalho".
            var exe = Environment.ProcessPath ?? "";
            var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (exe.Length > 0 && !exe.StartsWith(pf, StringComparison.OrdinalIgnoreCase) && !exe.EndsWith("dotnet.exe", StringComparison.OrdinalIgnoreCase))
                return "O Windows não deixou o Softphone usar o microfone (o outro lado não vai te ouvir). " +
                       $"Este Softphone foi aberto de fora da pasta de instalação ({Path.GetDirectoryName(exe)}), e o antivírus só libera o " +
                       "microfone para o Softphone instalado em Program Files. Feche e abra pelo atalho Softphone do menu Iniciar.";
            return "O Windows não deixou o Softphone usar o microfone (o outro lado não vai te ouvir). " +
                   "Normalmente é o antivírus bloqueando programas novos: peça à TI para liberar o Softphone.exe.";
        }
        if (msg.Contains("Allocated", StringComparison.OrdinalIgnoreCase))
            return "O microfone está em uso exclusivo por outro programa. Feche-o e tente de novo.";
        if (msg.Contains("BadDeviceId", StringComparison.OrdinalIgnoreCase) || msg.Contains("NoDriver", StringComparison.OrdinalIgnoreCase))
            return "O microfone escolhido não foi encontrado (headset desconectado?). Confira em Configurações > Áudio.";
        return "Não foi possível abrir o microfone: " + msg;
    }

    /// <summary>Índice pelo nome salvo; -1 (padrão do Windows) se vazio ou não encontrado.</summary>
    public static int Indice(IReadOnlyList<string> lista, string? nome)
    {
        if (string.IsNullOrWhiteSpace(nome)) return -1;
        for (int i = 0; i < lista.Count; i++) if (string.Equals(lista[i], nome, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }
}

/// <summary>Teste de microfone das configurações: mede o nível (0 a 1) sem gravar nada.</summary>
public sealed class TesteMicrofone : IDisposable
{
    private WaveInEvent? _mic;

    /// <summary>Nível de pico de cada quadro de 50 ms (0 a 1). Chega numa thread de áudio.</summary>
    public event Action<float>? Nivel;

    /// <summary>Abre o microfone. Retorna null se abriu, ou a mensagem de erro para o usuário.</summary>
    public string? Iniciar(int dispositivo)
    {
        try
        {
            _mic = new WaveInEvent { DeviceNumber = dispositivo, WaveFormat = new WaveFormat(8000, 16, 1), BufferMilliseconds = 50 };
            _mic.DataAvailable += (_, e) =>
            {
                int pico = 0;
                for (int i = 0; i + 1 < e.BytesRecorded; i += 2) pico = Math.Max(pico, Math.Abs((int)BitConverter.ToInt16(e.Buffer, i)));
                Nivel?.Invoke(Math.Min(1f, pico / 32768f));
            };
            _mic.StartRecording();
            return null;
        }
        catch (Exception ex)
        {
            Dispose();
            return DispositivosAudio.ExplicarErroMicrofone(ex);
        }
    }

    public void Dispose()
    {
        var m = _mic;
        _mic = null;
        if (m == null) return;
        try { m.StopRecording(); } catch { }
        m.Dispose();
    }
}