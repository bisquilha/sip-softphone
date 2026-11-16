namespace Softphone.Core;

/// <summary>Dados da conta SIP. São os mesmos campos da tela "Conta" do MicroSIP.</summary>
public sealed class ContaSip
{
    public string NomeConta { get; set; } = "";
    /// <summary>Servidor SIP (registrar). Aceita "host" ou "host:porta".</summary>
    public string Servidor { get; set; } = "";
    /// <summary>Proxy de saída (opcional). Vazio = manda direto para o servidor.</summary>
    public string Proxy { get; set; } = "";
    /// <summary>Usuário (ramal) que aparece no AOR: sip:usuario@dominio.</summary>
    public string Usuario { get; set; } = "";
    /// <summary>Domínio do AOR. Vazio = usa o servidor.</summary>
    public string Dominio { get; set; } = "";
    /// <summary>Login de autenticação. Vazio = usa o usuário.</summary>
    public string Login { get; set; } = "";
    /// <summary>Senha em texto puro só na memória; o app guarda criptografada (DPAPI).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string Senha { get; set; } = "";
    public string NomeExibicao { get; set; } = "";
    /// <summary>UDP ou TCP.</summary>
    public string Transporte { get; set; } = "UDP";
    /// <summary>Porta local SIP. 0 = escolhe uma livre.</summary>
    public int PortaLocal { get; set; }
    public int RegistroSegundos { get; set; } = 300;
    /// <summary>Intervalo do keep-alive de NAT (CRLF). 0 = desligado.</summary>
    public int ManterAtivoSegundos { get; set; } = 15;

    [System.Text.Json.Serialization.JsonIgnore]
    public string DominioEfetivo => string.IsNullOrWhiteSpace(Dominio) ? SemPorta(Servidor) : Dominio.Trim();
    [System.Text.Json.Serialization.JsonIgnore]
    public string LoginEfetivo => string.IsNullOrWhiteSpace(Login) ? Usuario.Trim() : Login.Trim();
    [System.Text.Json.Serialization.JsonIgnore]
    public bool Valida => !string.IsNullOrWhiteSpace(Servidor) && !string.IsNullOrWhiteSpace(Usuario);

    private static string SemPorta(string host)
    {
        host = host.Trim();
        var i = host.LastIndexOf(':');
        return i > 0 && !host.Contains(']') ? host[..i] : host;
    }
}

public enum EstadoRegistro { Desligado, Registrando, Registrado, Falha }

public enum DirecaoChamada { Saida, Entrada }

public enum EstadoChamada
{
    /// <summary>Saída: INVITE enviado, aguardando resposta.</summary>
    Discando,
    /// <summary>Saída: o destino está tocando (180/183).</summary>
    Chamando,
    /// <summary>Entrada: tocando aqui, aguardando atender.</summary>
    Recebendo,
    /// <summary>Atendendo / negociando mídia.</summary>
    Conectando,
    EmAndamento,
    EmEspera,
    Encerrada,
}

/// <summary>Como a chamada terminou (para o histórico).</summary>
public enum ResultadoChamada { Nenhum, Atendida, Perdida, Recusada, Ocupado, NaoAtendida, Falhou, Cancelada, Transferida, Encaminhada }

/// <summary>Resultado de uma transferência: Ok, ou o motivo em português para mostrar a quem transferiu.</summary>
public sealed record ResultadoTransferencia(bool Ok, string? Motivo);
