using System;
using System.Collections.Generic;
using System.Linq;
using Softphone.Contratos;

namespace Softphone.App;

internal static class TextoPresenca
{
    public static string Nome(EstadoPresenca e) => e switch
    {
        EstadoPresenca.Disponivel => "Disponível",
        EstadoPresenca.Ausente => "Ausente",
        EstadoPresenca.EmLigacao => "Em ligação",
        EstadoPresenca.NaoPerturbe => "Não perturbe",
        _ => "Offline",
    };

    /// <summary>Chave do pincel do tema para a bolinha de presença.</summary>
    public static string Cor(EstadoPresenca e) => e switch
    {
        EstadoPresenca.Disponivel => "DotOk",
        EstadoPresenca.Ausente => "DotWait",
        EstadoPresenca.EmLigacao => "DotBad",
        EstadoPresenca.NaoPerturbe => "DotBad",
        _ => "DotOff",
    };

    public static string HaQuanto(DateTime? quando)
    {
        if (quando is null) return "";
        var d = DateTime.Now - quando.Value.ToLocalTime();
        if (d.TotalMinutes < 2) return "agora há pouco";
        if (d.TotalMinutes < 60) return $"há {(int)d.TotalMinutes} min";
        if (d.TotalHours < 24) return $"há {(int)d.TotalHours} h";
        if (d.TotalDays < 2) return "ontem";
        return quando.Value.ToLocalTime().ToString("dd/MM");
    }

    public static string Iniciais(string nome)
    {
        var p = nome.Split(new[] { ' ', '-', '.' }, StringSplitOptions.RemoveEmptyEntries).Where(x => x.Length > 1 && char.IsLetter(x[0])).ToArray();
        if (p.Length == 0) return "?";
        return p.Length == 1 ? p[0][..1].ToUpperInvariant() : (p[0][..1] + p[^1][..1]).ToUpperInvariant();
    }
}

/// <summary>Pessoa do diretório (Hub), com presença ao vivo.</summary>
public sealed class PessoaVM : Observavel
{
    public PessoaVM(PessoaDto p) { Atualizar(p); }

    public string Login { get; private set; } = "";
    public string Nome { get; private set; } = "";
    public string? Setor { get; private set; }
    public string? Unidade { get; private set; }
    public string? Ramal { get; private set; }
    /// <summary>E-mail do AD (1.1.3: convidados do "Agendar no Outlook").</summary>
    public string? Email { get; private set; }
    public string? Recado { get; private set; }
    public DateTime? VistoEm { get; private set; }
    public EstadoPresenca Presenca { get; private set; }

    public string Iniciais => TextoPresenca.Iniciais(Nome);
    public string CorPresenca => TextoPresenca.Cor(Presenca);
    public bool Online => Presenca != EstadoPresenca.Offline;
    public bool TemRamal => !string.IsNullOrWhiteSpace(Ramal);
    public string Detalhe => string.Join(" · ", new[] { Setor, Unidade, TemRamal ? "ramal " + Ramal : null }.Where(x => !string.IsNullOrWhiteSpace(x)));
    public string TextoStatus
    {
        get
        {
            var s = Presenca == EstadoPresenca.Offline && VistoEm != null ? $"Offline · visto {TextoPresenca.HaQuanto(VistoEm)}" : TextoPresenca.Nome(Presenca);
            return string.IsNullOrWhiteSpace(Recado) || Presenca == EstadoPresenca.Offline ? s : $"{s} · {Recado}";
        }
    }

    public void Atualizar(PessoaDto p)
    {
        Login = p.Login; Nome = p.Nome; Setor = p.Setor; Unidade = p.Unidade; Ramal = p.Ramal; Email = p.Email;
        Recado = p.Recado; VistoEm = p.VistoEm; Presenca = p.Presenca;
        AvisarTudo();
    }

    public void MudarPresenca(EstadoPresenca e, string? recado)
    {
        if (e == EstadoPresenca.Offline) VistoEm = DateTime.UtcNow;
        Presenca = e;
        if (e != EstadoPresenca.Offline) Recado = recado;
        AvisarTudo();
    }

    /// <summary>Atualiza textos que dependem da hora ("visto há 5 min").</summary>
    public void Recalcular() => AvisarTudo();

    public bool Combina(string busca) =>
        busca.Length == 0 ||
        Nome.Contains(busca, StringComparison.CurrentCultureIgnoreCase) ||
        Login.Contains(busca, StringComparison.OrdinalIgnoreCase) ||
        (Setor?.Contains(busca, StringComparison.CurrentCultureIgnoreCase) ?? false) ||
        (Unidade?.Contains(busca, StringComparison.CurrentCultureIgnoreCase) ?? false) ||
        (Ramal?.Contains(busca) ?? false);
}

/// <summary>Linha da aba Conversas.</summary>
public sealed class ConversaVM : Observavel
{
    private readonly Func<string, PessoaVM?> _pessoa;
    private readonly string _eu;

    public ConversaVM(ConversaDto c, string eu, Func<string, PessoaVM?> pessoa)
    {
        _eu = eu;
        _pessoa = pessoa;
        Dto = c;
    }

    public ConversaDto Dto { get; private set; }
    public long Id => Dto.Id;
    public bool Grupo => Dto.Grupo;

    /// <summary>Em conversa direta: a outra pessoa.</summary>
    public PessoaVM? Outra => Grupo ? null : _pessoa(Dto.Membros.FirstOrDefault(m => !string.Equals(m, _eu, StringComparison.OrdinalIgnoreCase)) ?? _eu);

    public string Titulo => Grupo ? (Dto.Titulo ?? "Grupo") : Outra?.Nome ?? Dto.Membros.FirstOrDefault(m => m != _eu) ?? "Conversa";
    public string Iniciais => Grupo ? "" : TextoPresenca.Iniciais(Titulo);
    public bool IniciaisSaoIcone => Grupo;
    public string CorPresenca => Outra?.CorPresenca ?? "DotOff";
    public bool MostrarPresenca => !Grupo;
    public int NaoLidas => Dto.NaoLidas;
    public bool TemNaoLidas => Dto.NaoLidas > 0;
    public DateTime Quando => Dto.Ultima?.EnviadaEm.ToLocalTime() ?? DateTime.MinValue;
    public string Hora
    {
        get
        {
            if (Dto.Ultima is null) return "";
            var d = Dto.Ultima.EnviadaEm.ToLocalTime();
            return d.Date == DateTime.Today ? d.ToString("HH:mm") : d.Date == DateTime.Today.AddDays(-1) ? "ontem" : d.ToString("dd/MM");
        }
    }
    public string Previa
    {
        get
        {
            var u = Dto.Ultima;
            if (u is null) return Grupo ? $"{Dto.Membros.Length} participantes" : "Nenhuma mensagem ainda";
            string quem = string.Equals(u.Autor, _eu, StringComparison.OrdinalIgnoreCase) ? "Você: " : Grupo ? (_pessoa(u.Autor)?.Nome.Split(' ')[0] ?? u.Autor) + ": " : "";
            string texto = u.Texto.StartsWith(HubNomes.PrefixoConvite, StringComparison.Ordinal) ? "📹 Chamada de vídeo / reunião"
                : u.Arquivo != null && string.IsNullOrWhiteSpace(u.Texto) ? "📎 " + u.Arquivo.Nome : u.Texto.Replace('\n', ' ');
            return quem + texto;
        }
    }

    public void Atualizar(ConversaDto c) { Dto = c; AvisarTudo(); }
    public void Recalcular() => AvisarTudo();
}

/// <summary>1.2.11: uma "pílula" de reação embaixo do balão (emoji, quantas pessoas, se eu estou entre elas).</summary>
public sealed record ReacaoVM(long MensagemId, string Emoji, int Quantidade, bool Minha, string Quem)
{
    public string Texto => Quantidade > 1 ? $"{Emoji} {Quantidade}" : Emoji;
    public string Dica => Quem + " reagiu" + (Minha ? " (clique para tirar)" : "");
}

/// <summary>Balão de mensagem na janela de conversa.</summary>
public sealed class MensagemVM : Observavel
{
    private readonly Func<string, string> _nome;
    private readonly string? _eu;

    public MensagemVM(MensagemDto m, bool minha, string autorNome, bool mostrarAutor, Func<string, string>? nome = null, string? eu = null)
    {
        Dto = m; Minha = minha; AutorNome = autorNome; MostrarAutor = mostrarAutor;
        _nome = nome ?? (l => l);
        _eu = eu;
    }

    public MensagemDto Dto { get; private set; }
    public long Id => Dto.Id;
    public bool Minha { get; }
    public bool DeOutro => !Minha;
    public string AutorNome { get; }
    public bool MostrarAutor { get; }
    // convite de reunião ("📹 Reunião: <link>\n<título>") vira um cartão com botão "Entrar"
    public bool EhReuniao => !Dto.Apagada && Dto.Texto.StartsWith(HubNomes.PrefixoConvite, StringComparison.Ordinal);
    private string[] LinhasReuniao => Dto.Texto[HubNomes.PrefixoConvite.Length..].Split('\n', 2);
    public string LinkReuniao => EhReuniao ? LinhasReuniao[0].Trim() : "";
    public string SalaReuniao => EhReuniao ? LinkReuniao.TrimEnd('/').Split('/').Last() : "";
    public string TituloReuniao => EhReuniao && LinhasReuniao.Length > 1 ? LinhasReuniao[1].Trim() : "Reunião";
    public string Texto => EhReuniao ? "" : Dto.Texto;
    public bool TemTexto => !EhReuniao && !string.IsNullOrWhiteSpace(Dto.Texto);
    public bool TemArquivo => Dto.Arquivo != null;
    public string ArquivoNome => Dto.Arquivo?.Nome ?? "";
    public string ArquivoTamanho => Dto.Arquivo is null ? "" : Dto.Arquivo.Tamanho < 1024 * 1024 ? $"{Math.Max(1, Dto.Arquivo.Tamanho / 1024)} KB" : $"{Dto.Arquivo.Tamanho / 1024.0 / 1024:0.0} MB";
    public string Hora => Dto.EnviadaEm.ToLocalTime().ToString(Dto.EnviadaEm.ToLocalTime().Date == DateTime.Today ? "HH:mm" : "dd/MM HH:mm")
                          + (Editada ? " · editada" : "");

    // ----- 1.1 -----
    public bool Apagada => Dto.Apagada;
    public bool Editada => Dto.EditadaEm != null && !Dto.Apagada;
    public bool TemCitacao => Dto.RespostaA != null;
    public string CitacaoAutor => Dto.RespostaAutor is null ? "" : _nome(Dto.RespostaAutor);
    public string CitacaoTrecho => Dto.RespostaTrecho ?? "";
    /// <summary>Alguém me marcou com @ nesta mensagem.</summary>
    public bool MencionaMe => !Minha && _eu != null && Dto.Mencoes?.Any(m => string.Equals(m, _eu, StringComparison.OrdinalIgnoreCase)) == true;
    public bool PodeMudar => Minha && !Dto.Apagada && !EhReuniao;
    public bool PodeResponder => !Dto.Apagada;

    // ----- 1.2.11: reações -----
    public bool PodeReagir => !Dto.Apagada;
    public bool TemReacoes => !Dto.Apagada && Dto.Reacoes is { Length: > 0 };
    public IReadOnlyList<ReacaoVM> Reacoes => Dto.Apagada || Dto.Reacoes is null ? Array.Empty<ReacaoVM>()
        : Dto.Reacoes.Select(r => new ReacaoVM(Dto.Id, r.Emoji, r.Logins.Length,
            _eu != null && r.Logins.Any(l => string.Equals(l, _eu, StringComparison.OrdinalIgnoreCase)),
            string.Join(", ", r.Logins.Select(l => _eu != null && string.Equals(l, _eu, StringComparison.OrdinalIgnoreCase) ? "você" : _nome(l))))).ToList();

    /// <summary>Destaque rápido quando a busca ou a citação leva até esta mensagem.</summary>
    public bool Destacada { get => _destacada; set { if (_destacada != value) { _destacada = value; Avisar(); } } }
    private bool _destacada;

    /// <summary>Mensagem editada/apagada pelo autor (chegou pelo Hub).</summary>
    public void Substituir(MensagemDto m) { Dto = m; AvisarTudo(); }

    private bool _lida;
    /// <summary>Minha mensagem já lida por todos os outros (✓✓).</summary>
    public bool Lida { get => _lida; set { if (_lida != value) { _lida = value; Avisar(); Avisar(nameof(Marca)); } } }
    public string Marca => Minha ? (Lida ? "✓✓" : "✓") : "";
}