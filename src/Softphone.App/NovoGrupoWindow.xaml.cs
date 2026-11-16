using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace Softphone.App;

public partial class NovoGrupoWindow : Window
{
    public sealed class Item { public PessoaVM Pessoa { get; init; } = null!; public bool Marcado { get; set; } }

    private readonly List<Item> _todos;

    public string Titulo => CaixaNome.Text.Trim();
    public string[] Logins => _todos.Where(i => i.Marcado).Select(i => i.Pessoa.Login).ToArray();

    public NovoGrupoWindow(IEnumerable<PessoaVM> pessoas, string? eu)
    {
        InitializeComponent();
        Theme.AplicarBarraTitulo(this);
        _todos = pessoas.Where(p => !string.Equals(p.Login, eu, StringComparison.OrdinalIgnoreCase)).Select(p => new Item { Pessoa = p }).ToList();
        Filtrar();
        Loaded += (_, _) => CaixaNome.Focus();
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
        TxtSelecionados.Text = n == 0 ? "Participantes" : $"Participantes ({n} escolhido{(n > 1 ? "s" : "")} + você)";
    }

    private void Criar_Click(object sender, RoutedEventArgs e)
    {
        if (Titulo.Length == 0) { TxtErro.Text = "Dê um nome ao grupo."; CaixaNome.Focus(); return; }
        if (Logins.Length == 0) { TxtErro.Text = "Escolha pelo menos uma pessoa."; return; }
        DialogResult = true;
    }
}
