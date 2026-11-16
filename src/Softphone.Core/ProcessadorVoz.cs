using System.Runtime.InteropServices;

namespace Softphone.Core;

/// <summary>Quanto de ruído de fundo tirar do microfone.</summary>
public enum NivelRuido { Desligado = -1, Baixo = 0, Moderado = 1, Alto = 2, MuitoAlto = 3 }

/// <summary>Tratamento do microfone (Configurações > Áudio).</summary>
public sealed class OpcoesVoz
{
    /// <summary>Tira do microfone o que sai do alto-falante (eco no viva-voz).</summary>
    public bool CancelarEco { get; set; } = true;
    public NivelRuido Ruido { get; set; } = NivelRuido.Moderado;
    /// <summary>Controle automático de volume do microfone (AGC).</summary>
    public bool VolumeAutomatico { get; set; }
    public bool Ativo => CancelarEco || Ruido != NivelRuido.Desligado || VolumeAutomatico;
}

/// <summary>
/// Cancelamento de eco (AEC3), redução de ruído, filtro de graves e controle de volume do WebRTC (webrtc-apm.dll,
/// licença BSD, vem do pacote SoundFlow.Extensions.WebRtc.Apm; a ligação com a DLL é esta classe).
/// Trabalha em quadros de 10 ms. <see cref="Referencia"/> recebe o que vai para o alto-falante (outra thread) e
/// <see cref="Processar"/> limpa o microfone no lugar. O AEC3 acha sozinho o atraso entre os dois.
/// Se a DLL não carregar, <see cref="Criar"/> devolve null e a chamada segue sem tratamento.
/// </summary>
public sealed unsafe class ProcessadorVoz : IDisposable
{
    private const string Dll = "webrtc-apm";

    private static readonly object TravaCarga = new();
    private static bool? _disponivel;
    /// <summary>Motivo de a DLL não ter carregado (para o log).</summary>
    public static string? ErroCarga { get; private set; }

    public static bool Disponivel
    {
        get
        {
            lock (TravaCarga)
            {
                if (_disponivel == null)
                {
                    try { _disponivel = webrtc_apm_get_frame_size(16000) == 160; if (_disponivel == false) ErroCarga = "tamanho de quadro inesperado"; }
                    catch (Exception ex) { _disponivel = false; ErroCarga = ex.GetType().Name + ": " + ex.Message; }
                }
                return _disponivel.Value;
            }
        }
    }

    public static ProcessadorVoz? Criar(int taxa, OpcoesVoz? opcoes)
    {
        if (opcoes == null || !opcoes.Ativo || !Disponivel) return null;
        try { return new ProcessadorVoz(taxa, opcoes); }
        catch (Exception ex) { ErroCarga = ex.Message; return null; }
    }

    private readonly object _travaCaptura = new(), _travaReverso = new();
    private IntPtr _apm, _cfgCaptura, _cfgReverso;
    private int _taxaReverso;
    private readonly int _taxa, _quadro;
    private readonly float* _buf;          // quadro do microfone
    private float* _bufReverso;            // quadro do alto-falante
    private short[] _restoReverso = Array.Empty<short>();
    private int _nResto;
    private bool _fechado;

    public OpcoesVoz Opcoes { get; }
    public int Taxa => _taxa;
    /// <summary>Estimativa do atraso alto-falante -> microfone (só uma dica: o AEC3 mede sozinho).</summary>
    public int AtrasoMs { get; set; } = 100;
    /// <summary>Quadros de 10 ms tratados / com erro (diagnóstico e testes).</summary>
    public long Quadros { get; private set; }
    public long Erros { get; private set; }

    private ProcessadorVoz(int taxa, OpcoesVoz op)
    {
        Opcoes = op;
        _taxa = taxa;
        _quadro = (int)webrtc_apm_get_frame_size(taxa);
        if (_quadro <= 0) throw new InvalidOperationException("Taxa não aceita pelo WebRTC: " + taxa);
        _apm = webrtc_apm_create();
        if (_apm == IntPtr.Zero) throw new InvalidOperationException("webrtc_apm_create falhou");
        var cfg = webrtc_apm_config_create();
        try
        {
            webrtc_apm_config_set_echo_canceller(cfg, op.CancelarEco ? 1 : 0, 0);
            webrtc_apm_config_set_noise_suppression(cfg, op.Ruido == NivelRuido.Desligado ? 0 : 1, op.Ruido == NivelRuido.Desligado ? 1 : (int)op.Ruido);
            // AGC1 adaptativo digital: alvo -3 dBFS, compressão 9 dB, limitador ligado (padrões do WebRTC)
            webrtc_apm_config_set_gain_controller1(cfg, op.VolumeAutomatico ? 1 : 0, 1, 3, 9, 1);
            webrtc_apm_config_set_gain_controller2(cfg, 0);
            webrtc_apm_config_set_high_pass_filter(cfg, 1);
            webrtc_apm_config_set_pre_amplifier(cfg, 0, 1f);
            webrtc_apm_config_set_pipeline(cfg, Math.Max(taxa, 16000), 0, 0, 0);
            Checar(webrtc_apm_apply_config(_apm, cfg), "apply_config");
        }
        finally { webrtc_apm_config_destroy(cfg); }
        _cfgCaptura = webrtc_apm_stream_config_create(taxa, 1);
        Checar(webrtc_apm_initialize(_apm), "initialize");
        _buf = (float*)NativeMemory.AllocZeroed((nuint)_quadro, sizeof(float));
    }

    private static void Checar(int erro, string onde)
    {
        if (erro != 0) throw new InvalidOperationException($"WebRTC {onde}: erro {erro}");
    }

    /// <summary>O que vai tocar no alto-falante (o "eco" a tirar). Qualquer tamanho; junta em quadros de 10 ms.</summary>
    public void Referencia(ReadOnlySpan<short> pcm, int taxa)
    {
        if (!Opcoes.CancelarEco || pcm.IsEmpty) return;
        lock (_travaReverso)
        {
            if (_fechado) return;
            if (taxa != _taxaReverso)
            {
                if (_cfgReverso != IntPtr.Zero) webrtc_apm_stream_config_destroy(_cfgReverso);
                if (_bufReverso != null) NativeMemory.Free(_bufReverso);
                int q = (int)webrtc_apm_get_frame_size(taxa);
                if (q <= 0) { _taxaReverso = 0; _cfgReverso = IntPtr.Zero; _bufReverso = null; return; }
                _cfgReverso = webrtc_apm_stream_config_create(taxa, 1);
                _bufReverso = (float*)NativeMemory.AllocZeroed((nuint)q, sizeof(float));
                _restoReverso = new short[q];
                _nResto = 0;
                _taxaReverso = taxa;
            }
            int quadro = _restoReverso.Length;
            int i = 0;
            while (i < pcm.Length)
            {
                int n = Math.Min(quadro - _nResto, pcm.Length - i);
                pcm.Slice(i, n).CopyTo(_restoReverso.AsSpan(_nResto));
                _nResto += n; i += n;
                if (_nResto < quadro) break;
                for (int k = 0; k < quadro; k++) _bufReverso[k] = _restoReverso[k] / 32768f;
                float* p = _bufReverso;
                if (webrtc_apm_process_reverse_stream(_apm, (IntPtr)(&p), _cfgReverso, _cfgReverso, (IntPtr)(&p)) != 0) Erros++;
                _nResto = 0;
            }
        }
    }

    /// <summary>Limpa o microfone no próprio vetor (quadros inteiros de 10 ms; uma sobra no fim passa sem tratar).</summary>
    public void Processar(short[] pcm)
    {
        lock (_travaCaptura)
        {
            if (_fechado) return;
            for (int ini = 0; ini + _quadro <= pcm.Length; ini += _quadro)
            {
                for (int k = 0; k < _quadro; k++) _buf[k] = pcm[ini + k] / 32768f;
                webrtc_apm_set_stream_delay_ms(_apm, AtrasoMs);
                float* p = _buf;
                if (webrtc_apm_process_stream(_apm, (IntPtr)(&p), _cfgCaptura, _cfgCaptura, (IntPtr)(&p)) != 0) { Erros++; continue; }
                for (int k = 0; k < _quadro; k++)
                {
                    float v = _buf[k] * 32768f;
                    pcm[ini + k] = v >= 32767f ? short.MaxValue : v <= -32768f ? short.MinValue : (short)v;
                }
                Quadros++;
            }
        }
    }

    public void Dispose()
    {
        lock (_travaCaptura)
        lock (_travaReverso)
        {
            if (_fechado) return;
            _fechado = true;
            if (_cfgCaptura != IntPtr.Zero) webrtc_apm_stream_config_destroy(_cfgCaptura);
            if (_cfgReverso != IntPtr.Zero) webrtc_apm_stream_config_destroy(_cfgReverso);
            if (_apm != IntPtr.Zero) webrtc_apm_destroy(_apm);
            _cfgCaptura = _cfgReverso = _apm = IntPtr.Zero;
            NativeMemory.Free(_buf);
            if (_bufReverso != null) NativeMemory.Free(_bufReverso);
            _bufReverso = null;
        }
    }

    // ---------- API C da webrtc-apm.dll (mesmas assinaturas do wrapper do SoundFlow, licença MIT) ----------
    [DllImport(Dll)] private static extern IntPtr webrtc_apm_create();
    [DllImport(Dll)] private static extern void webrtc_apm_destroy(IntPtr apm);
    [DllImport(Dll)] private static extern IntPtr webrtc_apm_config_create();
    [DllImport(Dll)] private static extern void webrtc_apm_config_destroy(IntPtr config);
    [DllImport(Dll)] private static extern void webrtc_apm_config_set_echo_canceller(IntPtr config, int enabled, int mobileMode);
    [DllImport(Dll)] private static extern void webrtc_apm_config_set_noise_suppression(IntPtr config, int enabled, int level);
    [DllImport(Dll)] private static extern void webrtc_apm_config_set_gain_controller1(IntPtr config, int enabled, int mode, int targetLevelDbfs, int compressionGainDb, int enableLimiter);
    [DllImport(Dll)] private static extern void webrtc_apm_config_set_gain_controller2(IntPtr config, int enabled);
    [DllImport(Dll)] private static extern void webrtc_apm_config_set_high_pass_filter(IntPtr config, int enabled);
    [DllImport(Dll)] private static extern void webrtc_apm_config_set_pre_amplifier(IntPtr config, int enabled, float fixedGainFactor);
    [DllImport(Dll)] private static extern void webrtc_apm_config_set_pipeline(IntPtr config, int maxInternalRate, int multiChannelRender, int multiChannelCapture, int downmixMethod);
    [DllImport(Dll)] private static extern int webrtc_apm_apply_config(IntPtr apm, IntPtr config);
    [DllImport(Dll)] private static extern IntPtr webrtc_apm_stream_config_create(int sampleRateHz, nuint numChannels);
    [DllImport(Dll)] private static extern void webrtc_apm_stream_config_destroy(IntPtr config);
    [DllImport(Dll)] private static extern int webrtc_apm_initialize(IntPtr apm);
    [DllImport(Dll)] private static extern int webrtc_apm_process_stream(IntPtr apm, IntPtr src, IntPtr inputConfig, IntPtr outputConfig, IntPtr dest);
    [DllImport(Dll)] private static extern int webrtc_apm_process_reverse_stream(IntPtr apm, IntPtr src, IntPtr inputConfig, IntPtr outputConfig, IntPtr dest);
    [DllImport(Dll)] private static extern void webrtc_apm_set_stream_delay_ms(IntPtr apm, int delay);
    [DllImport(Dll)] private static extern nuint webrtc_apm_get_frame_size(int sampleRateHz);
}
