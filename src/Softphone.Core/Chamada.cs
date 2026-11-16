using SIPSorcery.Media;
using SIPSorcery.SIP.App;

namespace Softphone.Core;

/// <summary>Uma chamada (entrada ou saída). Os eventos chegam em threads da biblioteca SIP; a tela deve despachar para a thread de UI.</summary>
public sealed class Chamada
{
    internal Chamada(SIPUserAgent ua, DirecaoChamada direcao, string numero, string? nome)
    {
        UA = ua;
        Direcao = direcao;
        Numero = numero;
        Nome = string.IsNullOrWhiteSpace(nome) ? null : nome.Trim().Trim('"');
        Estado = direcao == DirecaoChamada.Saida ? EstadoChamada.Discando : EstadoChamada.Recebendo;
    }

    public Guid Id { get; } = Guid.NewGuid();
    public DirecaoChamada Direcao { get; }
    public string Numero { get; }
    public string? Nome { get; internal set; }
    public string Exibicao => Nome is null ? Numero : Nome;

    public EstadoChamada Estado { get; internal set; }
    public ResultadoChamada Resultado { get; internal set; }
    /// <summary>Texto curto do motivo do fim (ex.: "Ocupado", "Número inexistente").</summary>
    public string? Motivo { get; internal set; }

    public DateTime Inicio { get; } = DateTime.Now;
    public DateTime? Atendida { get; internal set; }
    public DateTime? Fim { get; internal set; }
    public TimeSpan Duracao => Atendida is null ? TimeSpan.Zero : (Fim ?? DateTime.Now) - Atendida.Value;

    public bool Ativa => Estado != EstadoChamada.Encerrada;
    public bool Conectada => Estado is EstadoChamada.EmAndamento or EstadoChamada.EmEspera;

    public bool Mudo
    {
        get => Audio?.Mudo ?? _mudo;
        internal set { _mudo = value; if (Audio != null) Audio.Mudo = value; }
    }
    private bool _mudo;

    /// <summary>Esta chamada é a consulta de uma transferência assistida (a original está em espera).</summary>
    public Chamada? ConsultaDe { get; internal set; }

    internal SIPUserAgent UA { get; }
    internal SIPServerUserAgent? Uas { get; set; }
    internal VoIPMediaSession? Midia { get; set; }
    internal IAudioChamada? Audio { get; set; }
    internal int Encerrando;
    /// <summary>Motivo quando o fim foi pedido daqui (desligar/transferir); o callback da biblioteca usa.</summary>
    internal string? MotivoLocal;

    /// <summary>Gravando (só quando o usuário clicou em Gravar; o outro lado ouve o aviso).</summary>
    public bool Gravando => Gravador != null;
    public DateTime? GravandoDesde => Gravador?.Inicio;
    internal GravadorChamada? Gravador;
    /// <summary>1.3: captura de fala para a legenda ao vivo (null = desligada nesta chamada).</summary>
    public CapturaLegenda? Legenda { get; internal set; }

    /// <summary>Codec negociado (PCMA, PCMU, G722).</summary>
    public string? Codec { get; internal set; }
    /// <summary>Quadros de áudio do microfone enviados (diagnóstico). Zero numa chamada atendida = microfone não abriu.</summary>
    public long QuadrosEnviados => Audio?.QuadrosEnviados ?? 0;

    /// <summary>Pacotes de áudio recebidos (diagnóstico).</summary>
    public long PacotesRecebidos => Audio?.PacotesRecebidos ?? 0;

    /// <summary>Qualidade agora (atualizada a cada segundo pelo Telefone; evento QualidadeMudou).</summary>
    public QualidadeInstante Qualidade { get; internal set; } = QualidadeInstante.Vazia;
    /// <summary>Resumo da qualidade da ligação inteira (preenchido ao encerrar; null se não teve áudio).</summary>
    public QualidadeResumo? QualidadeFinal { get; internal set; }
    internal MedidorQualidade Medidor { get; } = new();

    /// <summary>Em conferência a três com esta outra chamada.</summary>
    public Chamada? ConferenciaCom { get; internal set; }
    public bool EmConferencia => ConferenciaCom != null;

    public override string ToString() => $"{Direcao} {Numero} [{Estado}]";
}
