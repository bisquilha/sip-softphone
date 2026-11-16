using System;
using System.Windows;

namespace Softphone.App;

public partial class EntrarHubWindow : Window
{
    private readonly ChatServico _chat;

    public EntrarHubWindow(ChatServico chat)
    {
        InitializeComponent();
        Theme.AplicarBarraTitulo(this);
        _chat = chat;
        CaixaUsuario.Text = Environment.UserName;
        Loaded += (_, _) => CaixaSenha.Focus();
    }

    private async void Entrar_Click(object sender, RoutedEventArgs e)
    {
        BtnEntrar.IsEnabled = false;
        TxtErro.Visibility = Visibility.Collapsed;
        var erro = await _chat.EntrarComSenhaAsync(CaixaUsuario.Text.Trim(), CaixaSenha.Password);
        CaixaSenha.Clear();
        BtnEntrar.IsEnabled = true;
        if (erro is null) { DialogResult = true; return; }
        TxtErro.Text = erro;
        TxtErro.Visibility = Visibility.Visible;
        CaixaSenha.Focus();
    }
}
