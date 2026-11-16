using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace Softphone.App;

/// <summary>
/// Escolher pessoas do diretório para a reunião (1.1.3, Claude Code): "Nova reunião" (Iniciar e chamar / Agendar no
/// Outlook) e "Adicionar pessoas" com a reunião aberta (Chamar). Mesma busca da janela de novo grupo, com a presença.
/// </summary>
public partial class EscolherPessoasWindow : Window
{
    public enum Escolha { Nada, Iniciar, Outlook }

    public sealed class Item { public PessoaVM Pessoa { get; init; } = null!; public bool Marcado { get; set; } }

    private readonly List<Item> _todos;
    private readonly bool _exigirAlguem;

    public Escolha Resultado { get; private set; }
    public IReadOnlyList<PessoaVM> Escolhidas => _todos.Where(i => i.Marcado).Select(i => i.Pessoa).ToList();

    /// <param name="adicionar">true = "Adicionar pessoas" (reunião já aberta): só o botão Chamar, sem Outlook.</param>
    public EscolherPessoasWindow(IEnumerable<PessoaVM> pessoas, string? eu, bool adicionar, IEnumerable<string>? jaChamados = null)
    {
        InitializeComponent();
        Theme.AplicarBarraTitulo(this);
        var fora = new HashSet<string>(jaChamados ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        if (eu != null) fora.Add(eu);
        // online primeiro, depois por nome
        _todos = pessoas.Where(p => !fora.Contains(p.Login))
            .OrderByDescending(p => p.Online).ThenBy(p => p.Nome, StringComparer.CurrentCultureIgnoreCase)
            .Select(p => new Item { Pessoa = p }).ToList();
        _exigirAlguem = adicionar;
        if (adicionar)
        {
            Title = "Adicionar pessoas à reunião";
            TxtExplica.Text = "Escolha quem chamar. O Softphone de quem estiver online toca na hora, como uma chamada de vídeo.";
            BtnOk.Content = "Chamar";
            BtnOutlook.Visibility = Visibility.Collapsed;
        }
        Filtrar();
        Loaded += (_, _) => CaixaBusca.Focus();
    }

    private void Filtrar()
    {
        var b = CaixaBusca.Text.Trim();
        // marcados sempre aparecem no topo, para não "sumirem" ao trocar a busca
        Lista.ItemsSource = _todos.Where(i => i.Marcado).Concat(_todos.Where(i => !i.Marcado && i.Pessoa.Combina(b)).Take(300)).ToList();
    }

    private void Busca_TextChanged(object sender, TextChangedEventArgs e) => Filtrar();

    private void Marcar_Changed(object sender, RoutedEventArgs e)
    {
        int n = _todos.Count(i => i.Marcado);
        int off = _todos.Count(i => i.Marcado && !i.Pessoa.Online);
        TxtSelecionados.Text = n == 0 ? "Pessoas" : $"Pessoas ({n} escolhida{(n > 1 ? "s" : "")}{(off > 0 ? $", {off} offline" : "")})";
        TxtErro.Text = "";
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (_exigirAlguem && Escolhidas.Count == 0) { TxtErro.Text = "Escolha pelo menos uma pessoa."; return; }
        Resultado = Escolha.Iniciar;
        DialogResult = true;
    }

    private void Outlook_Click(object sender, RoutedEventArgs e)
    {
        Resultado = Escolha.Outlook;
        DialogResult = true;
    }
}
