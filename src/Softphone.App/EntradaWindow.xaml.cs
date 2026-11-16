using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Softphone.Contratos;
using Softphone.Core;

namespace Softphone.App;

/// <summary>
/// Pergunta o destino (Transferir e Consultar). 1.1.3: busca no diretório da empresa (quem tem ramal) e nos contatos,
/// filtrando enquanto digita; antes só listava os contatos salvos e um nome digitado ia para o PABX como se fosse ramal.
/// </summary>
public partial class EntradaWindow : Window
{
    public sealed record Opcao(string Nome, string Numero, string Detalhe, string? Cor, string Estado = "estado desconhecido")
    {
        public bool TemPresenca => Cor != null;
        public bool Combina(string busca) =>
            busca.Length == 0 ||
            Nome.Contains(busca, StringComparison.CurrentCultureIgnoreCase) ||
            Numero.Contains(busca, StringComparison.OrdinalIgnoreCase) ||
            Detalhe.Contains(busca, StringComparison.CurrentCultureIgnoreCase);
    }

    private readonly List<Opcao> _todas;
    private string? _resultado;

    private EntradaWindow(string titulo, string pergunta, string botao, string? numeroDaLigacao)
    {
        InitializeComponent();
        Theme.AplicarBarraTitulo(this);
        Title = titulo;
        TxtPergunta.Text = pergunta;
        BtnOk.Content = botao;
        var app = App.Atual;
        var proprio = numeroDaLigacao is null ? "" : Telefone.LimparDestino(numeroDaLigacao);
        var empresa = app.Chat.Pessoas.Where(p => p.TemRamal && !string.Equals(p.Login, app.Chat.EuLogin, StringComparison.OrdinalIgnoreCase))
            .Select(p => new Opcao(p.Nome, p.Ramal!, string.Join(" · ", new[] { p.Setor, p.Unidade }.Where(x => !string.IsNullOrWhiteSpace(x))), p.CorPresenca, EstadoDoDestino(p.Presenca)));
        var contatos = app.Contatos.Select(c => new Opcao(c.Nome, c.Numero, "contato", null));
        // o próprio número de quem está na ligação não aparece (transferir para ele era o erro do log de 05/11)
        _todas = empresa.Concat(contatos)
            .Where(o => Telefone.LimparDestino(o.Numero) is { Length: > 0 } n && n != proprio)
            .GroupBy(o => Telefone.LimparDestino(o.Numero)).Select(g => g.First())
            .OrderBy(o => o.Nome, StringComparer.CurrentCultureIgnoreCase).ToList();
        Filtrar();
        Loaded += (_, _) => Caixa.Focus();
    }

    /// <summary>1.3: antes de transferir, o estado do destino em palavras (a cor sozinha não basta). É a presença no Softphone, não o registro no PABX.</summary>
    internal static string EstadoDoDestino(EstadoPresenca e) => e switch
    {
        EstadoPresenca.Disponivel => "livre",
        EstadoPresenca.EmLigacao => "ocupado (em ligação)",
        EstadoPresenca.NaoPerturbe => "não perturbe",
        EstadoPresenca.Ausente => "ausente",
        _ => "offline",
    };

    /// <summary>Retorna o número limpo, ou null se cancelou. <paramref name="numeroDaLigacao"/> fica fora das sugestões.</summary>
    public static string? Perguntar(Window dono, string titulo, string pergunta, string botao, string? numeroDaLigacao = null)
    {
        var j = new EntradaWindow(titulo, pergunta, botao, numeroDaLigacao) { Owner = dono };
        return j.ShowDialog() == true ? j._resultado : null;
    }

    private void Filtrar()
    {
        var b = Caixa.Text.Trim();
        var lista = _todas.Where(o => o.Combina(b)).Take(200).ToList();
        Sugestoes.ItemsSource = lista;
        // nome: a primeira pessoa fica marcada (Enter escolhe); número: só marca se for exatamente o ramal de alguém
        if (b.Length > 0 && !SoDigitos(b)) Sugestoes.SelectedIndex = lista.Count > 0 ? 0 : -1;
        else Sugestoes.SelectedItem = lista.FirstOrDefault(o => Telefone.LimparDestino(o.Numero) == Telefone.LimparDestino(b));
        TxtDica.Text = lista.Count == 0 && b.Length > 0 && !SoDigitos(b)
            ? "Ninguém com esse nome. Digite o ramal ou o número."
            : "Digite o nome, o ramal ou um número externo.";
    }

    private static bool SoDigitos(string s) => s.Length > 0 && s.All(ch => char.IsDigit(ch) || ch is ' ' or '-' or '(' or ')' or '+' or '*' or '#');

    private void Caixa_TextChanged(object sender, TextChangedEventArgs e) => Filtrar();

    private void Caixa_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        int n = Sugestoes.Items.Count;
        if (n == 0) return;
        if (e.Key == Key.Down) { Sugestoes.SelectedIndex = Math.Min(n - 1, Sugestoes.SelectedIndex + 1); Sugestoes.ScrollIntoView(Sugestoes.SelectedItem); e.Handled = true; }
        else if (e.Key == Key.Up) { Sugestoes.SelectedIndex = Math.Max(0, Sugestoes.SelectedIndex - 1); Sugestoes.ScrollIntoView(Sugestoes.SelectedItem); e.Handled = true; }
    }

    private void Sugestoes_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Sugestoes.SelectedItem is Opcao) Ok_Click(sender, e);
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var texto = Caixa.Text.Trim();
        string numero;
        if (Sugestoes.SelectedItem is Opcao escolhida)
            numero = Telefone.LimparDestino(escolhida.Numero);       // pessoa marcada na lista (pela busca, setas ou clique)
        else if (SoDigitos(texto))
            numero = Telefone.LimparDestino(texto);                 // número digitado (ramal ou externo)
        else
        {
            TxtDica.Text = texto.Length == 0 ? "Digite o nome, o ramal ou um número externo." : "Escolha alguém da lista ou digite o ramal/número.";
            Caixa.Focus();
            return;
        }
        if (numero.Length == 0) { Caixa.Focus(); return; }
        _resultado = numero;
        DialogResult = true;
    }
}
