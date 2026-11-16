using System;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using Softphone.Contratos;

namespace Softphone.App;

public sealed class ResultadoBuscaVM
{
    public ResultadoBuscaVM(ResultadoBuscaDto r, ChatServico chat)
    {
        Dto = r;
        var autor = string.Equals(r.Mensagem.Autor, chat.EuLogin, StringComparison.OrdinalIgnoreCase) ? "Você" : chat.Pessoa(r.Mensagem.Autor)?.Nome ?? r.Mensagem.Autor;
        var conversa = chat.Conversas.FirstOrDefault(c => c.Id == r.Mensagem.Conversa)?.Titulo ?? r.TituloConversa ?? "Conversa";
        Onde = r.Grupo || conversa != autor ? $"{autor} · {conversa}" : autor;
        var d = r.Mensagem.EnviadaEm.ToLocalTime();
        Quando = d.Date == DateTime.Today ? d.ToString("HH:mm") : d.ToString("dd/MM/yy HH:mm");
        var texto = string.IsNullOrWhiteSpace(r.Mensagem.Texto) && r.Mensagem.Arquivo != null ? "📎 " + r.Mensagem.Arquivo.Nome : r.Mensagem.Texto;
        Trecho = texto.Replace('\n', ' ');
    }

    public ResultadoBuscaDto Dto { get; }
    public string Onde { get; }
    public string Quando { get; }
    public string Trecho { get; }
}

/// <summary>Busca nas minhas conversas (texto e nome de arquivo). Hub 1.1.</summary>
public partial class BuscaWindow : Window
{
    private static BuscaWindow? _aberta;
    private readonly ChatServico _chat;

    public static void Abrir(Window? dono)
    {
        if (_aberta is { } j) { j.Activate(); j.Caixa.Focus(); return; }
        _aberta = new BuscaWindow(App.Atual.Chat) { Owner = dono };
        _aberta.Closed += (_, _) => _aberta = null;
        _aberta.Show();
    }

    public BuscaWindow(ChatServico chat)
    {
        InitializeComponent();
        Theme.AplicarBarraTitulo(this);
        _chat = chat;
        Caixa.TextChanged += (_, _) => Dica.Visibility = Caixa.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        Loaded += (_, _) => Caixa.Focus();
    }

    private async void Buscar_Click(object sender, RoutedEventArgs e)
    {
        var texto = Caixa.Text.Trim();
        if (texto.Length < 2) { TxtInfo.Text = "Digite pelo menos 2 letras."; return; }
        TxtInfo.Text = "Buscando...";
        try
        {
            var r = await _chat.BuscarAsync(texto);
            Lista.ItemsSource = r.Select(x => new ResultadoBuscaVM(x, _chat)).ToList();
            TxtInfo.Text = r.Count == 0 ? $"Nada encontrado para \"{texto}\"." : r.Count >= 50 ? "Mostrando as 50 mais recentes. Refine a busca para achar mais antigas." : $"{r.Count} mensagem(ns).";
        }
        catch (Exception ex)
        {
            var msg = ex.Message;
            var i = msg.IndexOf("HubException: ", StringComparison.Ordinal);
            TxtInfo.Text = i >= 0 ? msg[(i + 14)..] : msg;
        }
    }

    private void Caixa_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Escape) Close(); }

    /// <summary>Só para o modo de teste.</summary>
    internal void TesteBuscar(string texto) { Caixa.Text = texto; Buscar_Click(this, new RoutedEventArgs()); }

    private void Lista_DoubleClick(object sender, MouseButtonEventArgs e) => AbrirSelecionado();
    private void Lista_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) AbrirSelecionado(); }

    private void AbrirSelecionado()
    {
        if (Lista.SelectedItem is ResultadoBuscaVM r) _ = _chat.AbrirNaMensagemAsync(r.Dto.Mensagem.Conversa, r.Dto.Mensagem.Id);
    }
}
