using System.Windows;

namespace Softphone.App;

public partial class ContatoWindow : Window
{
    private readonly Contato _contato;

    private ContatoWindow(Contato contato)
    {
        InitializeComponent();
        Theme.AplicarBarraTitulo(this);
        _contato = contato;
        Title = string.IsNullOrEmpty(contato.Nome) && string.IsNullOrEmpty(contato.Numero) ? "Novo contato" : "Editar contato";
        CaixaNome.Text = contato.Nome;
        CaixaNumero.Text = contato.Numero;
        CaixaSetor.Text = contato.Setor ?? "";
        CaixaEmpresa.Text = contato.Empresa ?? "";
        Loaded += (_, _) => { CaixaNome.Focus(); CaixaNome.SelectAll(); };
    }

    /// <summary>Edita o contato no lugar; retorna ele mesmo, ou null se cancelou.</summary>
    public static Contato? Editar(Window dono, Contato contato)
    {
        var j = new ContatoWindow(contato) { Owner = dono };
        return j.ShowDialog() == true ? contato : null;
    }

    private void Salvar_Click(object sender, RoutedEventArgs e)
    {
        var nome = CaixaNome.Text.Trim();
        var numero = CaixaNumero.Text.Trim();
        if (nome.Length == 0 || numero.Length == 0)
        {
            TxtErro.Text = "Informe o nome e o número.";
            TxtErro.Visibility = Visibility.Visible;
            return;
        }
        _contato.Nome = nome;
        _contato.Numero = numero;
        _contato.Setor = string.IsNullOrWhiteSpace(CaixaSetor.Text) ? null : CaixaSetor.Text.Trim();
        _contato.Empresa = string.IsNullOrWhiteSpace(CaixaEmpresa.Text) ? null : CaixaEmpresa.Text.Trim();
        DialogResult = true;
    }
}
