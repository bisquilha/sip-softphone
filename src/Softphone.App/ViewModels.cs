using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Softphone.Core;

namespace Softphone.App;

public abstract class Observavel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Avisar([CallerMemberName] string? nome = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nome));
    protected void AvisarTudo() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
}

/// <summary>Cartão de uma chamada ativa na tela.</summary>
public sealed class ChamadaVM : Observavel
{
    public ChamadaVM(Chamada c, string? nomeContato)
    {
        Chamada = c;
        NomeContato = nomeContato;
    }

    public Chamada Chamada { get; }
    /// <summary>Nome vindo da agenda local (tem prioridade sobre o nome que o PABX mandou).</summary>
    public string? NomeContato { get; }

    /// <summary>1.3: empresa e setor de quem está na linha (contatos, diretório). "ambíguo" quando mais de um contato tem o mesmo número.</summary>
    public string? Detalhe { get; init; }

    public string Titulo => NomeContato ?? Chamada.Nome ?? Chamada.Numero;
    public string Subtitulo
    {
        get
        {
            if (Titulo == Chamada.Numero)
                return Detalhe ?? ((Chamada.Direcao == DirecaoChamada.Entrada ? "Chamada recebida" : "Chamada de saída") + " · número desconhecido");
            return string.IsNullOrWhiteSpace(Detalhe) ? Chamada.Numero : Detalhe + " · " + Chamada.Numero;
        }
    }
    public string Iniciais
    {
        get
        {
            var partes = Titulo.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (partes.Length == 0 || char.IsDigit(partes[0][0])) return ""; // ícone de pessoa
            return partes.Length == 1 ? partes[0][..1].ToUpperInvariant() : (partes[0][..1] + partes[^1][..1]).ToUpperInvariant();
        }
    }
    public bool IniciaisSaoIcone => Iniciais == "";

    public string EstadoTexto => Chamada.Estado switch
    {
        EstadoChamada.Discando => "Discando...",
        EstadoChamada.Chamando => "Chamando...",
        EstadoChamada.Recebendo => "Tocando",
        EstadoChamada.Conectando => "Conectando...",
        EstadoChamada.EmAndamento => (Chamada.Mudo ? $"{Tempo} · mudo" : Tempo) + (Gravando ? " · ● gravando" : ""),
        EstadoChamada.EmEspera => $"Em espera · {Tempo}" + (Gravando ? " · ● gravando" : ""),
        _ => Chamada.Motivo ?? "Encerrada",
    };

    public bool Gravando => Chamada.Gravando;
    public string TextoGravar => Gravando ? "Parar" : "Gravar";

    /// <summary>O que já se falou com esse número (resumo de ligações gravadas antes). Vem do Hub depois que a chamada aparece.</summary>
    public string? Contexto
    {
        get => _contexto;
        set { _contexto = value; Avisar(); Avisar(nameof(TemContexto)); }
    }
    private string? _contexto;
    public bool TemContexto => !string.IsNullOrEmpty(_contexto);

    public string Tempo
    {
        get
        {
            var d = Chamada.Duracao;
            return d.TotalHours >= 1 ? d.ToString(@"h\:mm\:ss") : d.ToString(@"m\:ss");
        }
    }

    public bool Tocando => Chamada.Estado == EstadoChamada.Recebendo;

    private bool _popupAberto;
    /// <summary>1.3: enquanto a chamada toca, o aviso em janela própria é a ÚNICA interface dela. O cartão da lista só aparece depois que atende
    /// (antes os dois mostravam Atender/Recusar ao mesmo tempo: era a "tela duplicada ao atender").</summary>
    public bool PopupAberto { get => _popupAberto; set { _popupAberto = value; Avisar(nameof(ExibirCartao)); } }
    public bool ExibirCartao => !(Tocando && _popupAberto);
    public bool Conectada => Chamada.Conectada;
    public bool EmEspera => Chamada.Estado == EstadoChamada.EmEspera;
    public bool EmAndamento => Chamada.Estado == EstadoChamada.EmAndamento;
    public bool Mudo => Chamada.Mudo;
    public bool EhConsulta => Chamada.ConsultaDe != null && Chamada.Conectada;
    public bool NaoTocando => !Tocando;
    public string TextoEspera => EmConferencia ? "Separar" : EmEspera ? "Retomar" : "Espera";
    public string IconeEspera => EmConferencia ? "" : EmEspera ? "" : "";

    // ---------- conferência a três ----------
    public bool EmConferencia => Chamada.EmConferencia;
    public string TextoConferencia => Chamada.ConferenciaCom is { } o ? "Em conferência com " + o.Exibicao : "";
    /// <summary>Há outra chamada conectada e nenhuma das duas está em conferência (a tela calcula).</summary>
    public bool PodeConferencia
    {
        get => _podeConferencia;
        set { if (_podeConferencia != value) { _podeConferencia = value; Avisar(); } }
    }
    private bool _podeConferencia;

    // ---------- qualidade da ligação ----------
    public bool MostrarQualidade => Chamada.Conectada && Chamada.Qualidade.Nivel != NivelQualidade.SemDados;
    public NivelQualidade NivelQualidade => Chamada.Qualidade.Nivel;
    /// <summary>Barras de sinal (Segoe MDL2): 4 barras boa, 3 regular, 1 ruim, sem sinal = sem áudio.</summary>
    public string IconeQualidade => Chamada.Qualidade.Nivel switch
    {
        NivelQualidade.Boa => "",
        NivelQualidade.Regular => "",
        NivelQualidade.Ruim => "",
        _ => "",
    };
    public string QualidadeCurta
    {
        get
        {
            var q = Chamada.Qualidade;
            return q.Nivel switch
            {
                NivelQualidade.Boa => "Ligação boa",
                NivelQualidade.Regular => $"Ligação regular · perda {q.PerdaPct:N0}%",
                NivelQualidade.Ruim => $"Ligação ruim · perda {q.PerdaPct:N0}%",
                NivelQualidade.SemAudio => "Não está chegando áudio",
                _ => "",
            };
        }
    }
    public string QualidadeDetalhe
    {
        get
        {
            var q = Chamada.Qualidade;
            return $"{q.Texto}\nPacotes recebidos: {q.Recebidos:N0} · perdidos: {q.Perdidos:N0}" +
                   (q.PerdaRemotaPct is double pr ? $"\nO outro lado perdeu {pr:N1}% do seu áudio" : "") +
                   "\n\nNota de 1 a 4,5: acima de 4,1 é boa; abaixo de 3,6 a conversa falha." +
                   "\nPerda e variação altas costumam ser a rede (Wi-Fi fraco, internet da unidade).";
        }
    }
    public void AtualizarQualidade()
    {
        Avisar(nameof(MostrarQualidade)); Avisar(nameof(NivelQualidade)); Avisar(nameof(IconeQualidade));
        Avisar(nameof(QualidadeCurta)); Avisar(nameof(QualidadeDetalhe));
    }
    /// <summary>Esta chamada participou de uma conferência (vai marcada no histórico).</summary>
    public bool FoiConferencia { get; set; }
    /// <summary>Segundos seguidos com a ligação ruim (para avisar uma vez só).</summary>
    public int SegundosRuim { get; set; }
    public bool AvisouRuim { get; set; }
    public string TextoConsulta => EhConsulta ? $"Transferir para {Chamada.Numero}" : "";

    /// <summary>Com mais de uma chamada, a que está em espera vira um cartão compacto (só Retomar/Desligar).</summary>
    public bool Compacto
    {
        get => _compacto;
        set { if (_compacto != value) { _compacto = value; AvisarTudo(); } }
    }
    private bool _compacto;
    public bool AcoesCompletas => Conectada && !Compacto;
    public bool DesligarGrande => NaoTocando && !Compacto;

    public void Atualizar() => AvisarTudo();
    public void Tique() { if (Chamada.Conectada) Avisar(nameof(EstadoTexto)); }
}

/// <summary>Linha da aba Recentes.</summary>
public sealed class HistoricoVM
{
    public HistoricoVM(RegistroHistorico r, string? nomeContato)
    {
        Registro = r;
        Titulo = nomeContato ?? r.Nome ?? r.Numero;
    }

    public RegistroHistorico Registro { get; }
    public string Titulo { get; }
    public string Numero => Registro.Numero;
    public bool Perdida => Registro.Direcao == DirecaoChamada.Entrada && Registro.Resultado is ResultadoChamada.Perdida;
    /// <summary>Seta (fonte Segoe UI Symbol): ↗ saída, ↙ recebida (vermelha se perdida).</summary>
    public string Icone => Registro.Direcao == DirecaoChamada.Saida ? "↗" : "↙";
    public bool Falhou => Registro.Direcao == DirecaoChamada.Saida && Registro.Resultado is not (ResultadoChamada.Atendida or ResultadoChamada.Transferida);
    public string Detalhe
    {
        get
        {
            var d = Registro.Data;
            string quando = d.Date == DateTime.Today ? d.ToString("HH:mm") : d.Date == DateTime.Today.AddDays(-1) ? "ontem " + d.ToString("HH:mm") : d.ToString("dd/MM HH:mm");
            string info = Registro.Resultado switch
            {
                ResultadoChamada.Atendida => TimeSpan.FromSeconds(Registro.DuracaoSegundos).ToString(Registro.DuracaoSegundos >= 3600 ? @"h\:mm\:ss" : @"m\:ss"),
                ResultadoChamada.Transferida => "transferida",
                ResultadoChamada.Perdida => "perdida",
                ResultadoChamada.Recusada => "recusada",
                ResultadoChamada.Ocupado => "ocupado",
                ResultadoChamada.NaoAtendida => "não atendeu",
                ResultadoChamada.Cancelada => "cancelada",
                _ => Registro.Motivo ?? "falhou",
            };
            bool mostraNumero = Titulo != Registro.Numero;
            string ruim = Registro.Mos is double m && MedidorQualidade.Classificar(m) == NivelQualidade.Ruim ? " · ligação ruim" : "";
            return (mostraNumero ? Registro.Numero + " · " : "") + quando + " · " + info + (Registro.Conferencia == true ? " · conferência" : "") + ruim;
        }
    }

    /// <summary>Dica com a qualidade medida (só ligações atendidas a partir da 1.1).</summary>
    public string? DicaQualidade => Registro.Mos is double m
        ? $"Qualidade: {QualidadeInstante.NomeNivel(MedidorQualidade.Classificar(m))} (nota {m:N1}, perda {Registro.PerdaPct ?? 0:N1}%, variação {Registro.JitterMs ?? 0:N0} ms)"
        : null;
}

public sealed class ContatoVM
{
    public ContatoVM(Contato c) { Contato = c; }
    public Contato Contato { get; }
    public string Nome => Contato.Nome;
    public string Numero => Contato.Numero;
    public string Detalhe => string.IsNullOrWhiteSpace(Contato.Setor) ? Contato.Numero : $"{Contato.Numero} · {Contato.Setor}";
    public string Iniciais
    {
        get
        {
            var p = Nome.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return p.Length == 0 ? "?" : p.Length == 1 ? p[0][..1].ToUpperInvariant() : (p[0][..1] + p[^1][..1]).ToUpperInvariant();
        }
    }
}
