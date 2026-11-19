using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Softphone.App;

/// <summary>
/// Só para testes automáticos da TI: ativado por SOFTPHONE_TESTE=&lt;pasta&gt;. Lê &lt;pasta&gt;\comando.txt
/// (uma linha por comando) e grava imagens das janelas. Sem a variável, não faz nada.
/// </summary>
internal static class ModoTeste
{
    public static void Iniciar(MainWindow janela)
    {
        var pasta = Environment.GetEnvironmentVariable("SOFTPHONE_TESTE");
        if (string.IsNullOrWhiteSpace(pasta)) return;
        Directory.CreateDirectory(pasta);
        var arquivo = Path.Combine(pasta, "comando.txt");
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        timer.Tick += (_, _) =>
        {
            if (!File.Exists(arquivo)) return;
            string[] linhas;
            try { linhas = File.ReadAllLines(arquivo); File.Delete(arquivo); } catch { return; }
            foreach (var l in linhas.Where(x => x.Trim().Length > 0))
            {
                try { Executar(janela, pasta, l.Trim()); }
                catch (Exception ex) { File.AppendAllText(Path.Combine(pasta, "erros.txt"), $"{l}: {ex}\n"); }
            }
            File.WriteAllText(Path.Combine(pasta, "pronto.txt"), DateTime.Now.ToString("O"));
        };
        timer.Start();
    }

    private static void Executar(MainWindow j, string pasta, string linha)
    {
        var partes = linha.Split(' ', 2);
        string cmd = partes[0].ToLowerInvariant(), arg = partes.Length > 1 ? partes[1] : "";
        var tel = App.Atual.Telefone;
        // "a chamada da vez": a que está em conversa; senão a mais recente
        var primeira = tel.Chamadas.FirstOrDefault(c => c.Estado == Core.EstadoChamada.EmAndamento) ?? tel.Chamadas.LastOrDefault();
        switch (cmd)
        {
            case "captura":
                int i = 0;
                foreach (Window w in Application.Current.Windows)
                {
                    if (!w.IsVisible) continue;
                    var nome = w == j ? arg : $"{Path.GetFileNameWithoutExtension(arg)}-{w.GetType().Name}-{i++}.png";
                    Renderizar(w, Path.Combine(pasta, nome));
                }
                break;
            // 1.1.3: janelas novas (abrem e ficam abertas para o "captura"; o "fechar" as fecha)
            case "entrada": j.Dispatcher.BeginInvoke(() => EntradaWindow.Perguntar(j, "Transferir chamada", "Transferir Cliente para:", "Transferir", arg)); break;
            case "entradatexto": foreach (Window w in Application.Current.Windows) if (w is EntradaWindow ew) ew.Caixa.Text = arg; break;
            case "novareuniao": j.Dispatcher.BeginInvoke(() => _ = App.Atual.Chat.NovaReuniaoAsync(j)); break;
            case "marcar":
                foreach (Window w in Application.Current.Windows)
                    if (w is EscolherPessoasWindow pw) { pw.CaixaBusca.Text = arg; foreach (EscolherPessoasWindow.Item it in pw.Lista.Items) it.Marcado = true; pw.Lista.Items.Refresh(); }
                break;
            case "reuniao": new ReuniaoWindow(new Contratos.ReuniaoDto("softphone-teste", "Reunião de teste", "https://reuniao.teste.invalid/softphone-teste",
                "https://reuniao.teste.invalid/softphone-teste", DateTime.UtcNow.AddHours(1))).Show(); break;
            case "ligar": _ = j.LigarAsync(arg); break;
            case "digitar": j.CaixaNumero.Text = arg; break;
            case "atender":
                var tocando = tel.Chamadas.FirstOrDefault(c => c.Estado == Core.EstadoChamada.Recebendo);
                if (tocando != null) _ = tel.AtenderAsync(tocando);
                break;
            case "desligar": if (primeira != null) tel.Desligar(primeira); break;
            case "mudo": if (primeira != null) tel.AlternarMudo(primeira); break;
            case "espera": if (primeira != null) _ = tel.AlternarEsperaAsync(primeira); break;
            case "dtmf": if (primeira != null) foreach (var ch in arg) _ = tel.EnviarDtmfAsync(primeira, ch); break;
            case "aba":
                (arg switch { "recentes" => j.AbaRecentes, "contatos" => j.AbaContatos, "pessoas" => j.AbaContatos, "conversas" => j.AbaConversas, "atalhos" => j.AbaAtalhos, _ => j.AbaDiscador }).IsChecked = true;
                break;
            // 1.2.4: tema pelo id (Claro, Escuro, Oliva...) e atalhos de exemplo (valem na próxima vez que a aba Atalhos abrir)
            case "tema": App.Atual.Config.Tema = arg; Theme.Reaplicar(); break;
            // só testes: "conta 127.0.0.1:5090 127.0.0.1 1001 senha" registra no PABX simulado (PabxTeste --servidor)
            case "conta":
                { var p = arg.Split(' '); var cfg = App.Atual.Config; cfg.Conta.Servidor = p[0]; cfg.Conta.Dominio = p[1]; cfg.Conta.Usuario = p[2]; cfg.Senha = p[3]; _ = App.Atual.ConectarAsync(); }
                break;
            case "atalhosdemo":
                App.Atual.Config.BotoesAtalho = new System.Collections.Generic.List<Core.AtalhoTela>
                {
                    new() { Nome = "Recepção", Tipo = "ligar", Valor = "1001", Cor = "verde" },
                    new() { Nome = "Ana", Tipo = "conversa", Valor = "ana.silva", Cor = "azul" },
                    new() { Nome = "Almoço", Tipo = "status", Valor = "Ausente", Cor = "ambar" },
                    new() { Nome = "GLPI", Tipo = "link", Valor = "https://glpi.corp.example.com", Cor = "roxo" },
                    new() { Nome = "Transferir TI", Tipo = "transferir", Valor = "2001", Cor = "cinza" },
                    new() { Nome = "Senha sala", Tipo = "dtmf", Valor = "1234#", Cor = "vermelho" },
                    new() { Nome = "Vendas externo", Tipo = "ligar", Valor = "+55 11 3333-4444", Cor = "verde" },
                };
                break;
            case "config": j.Dispatcher.BeginInvoke(() => j.AbrirConfiguracoes(primeiraVez: arg == "primeira")); break;
            case "fechar":
                foreach (Window w in Application.Current.Windows) if (w != j && w.IsVisible) w.Close();
                break;
            case "naoperturbe": j.DefinirNaoPerturbe(arg == "on"); break;
            case "chat": _ = App.Atual.Chat.AbrirComAsync(arg); break;
            case "cfgaba": foreach (Window w in Application.Current.Windows) if (w is ConfigWindow cw) cw.Abas.SelectedIndex = int.Parse(arg); break;
            case "buscar": j.CaixaBusca.Text = arg; break;
            case "msg":
                var janela = App.Atual.Chat.JanelasAbertas.FirstOrDefault();
                if (janela != null) { janela.Caixa.Text = arg; janela.Caixa.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, PresentationSource.FromVisual(janela)!, 0, System.Windows.Input.Key.Enter) { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent }); }
                break;
            // 1.2.11: reage à última mensagem da conversa aberta e abre o seletor de emojis numa janela (para "captura")
            case "reagir": _ = App.Atual.Chat.JanelasAbertas.FirstOrDefault()?.TesteReagirUltimaAsync(arg); break;
            case "seletor":
                new Window { Title = "Seletor", SizeToContent = SizeToContent.WidthAndHeight, Content = new EmojiSeletor(new List<string> { "👍", "🎉" }, true), Left = 40, Top = 40 }.Show();
                break;
            case "presenca":
                if (Enum.TryParse<Softphone.Contratos.EstadoPresenca>(arg, out var est)) _ = App.Atual.Chat.EscolherPresencaAsync(est, null);
                break;
            case "gravar": if (primeira != null) App.Atual.Gravacoes.Alternar(primeira); break;
            case "gravacoes": GravacoesWindow.Abrir(null); break;
            // 1.1.7: "Meu perfil para a IA" (sem modal, para o "captura"); "perfilaba 1" troca para a aba do setor
            case "gravsel": GravacoesWindow.TesteSelecionar(int.Parse(arg)); break;
            case "gravreproc": GravacoesWindow.TesteReprocessar(); break;
            case "sair": _ = App.Atual.SairAsync(); break;
            // ----- 1.1 -----
            case "conferencia":
                var ativa = tel.Chamadas.FirstOrDefault(c => c.Estado == Core.EstadoChamada.EmAndamento);
                var outra = tel.Chamadas.FirstOrDefault(c => c != ativa && c.Conectada);
                if (ativa != null && outra != null) _ = tel.ConferenciaAsync(ativa, outra);
                break;
            case "separar": if (primeira != null) _ = tel.SepararConferenciaAsync(primeira); break;
            case "ramais": RamaisWindow.Abrir(); break;
            case "busca": BuscaWindow.Abrir(j); foreach (Window w in Application.Current.Windows) if (w is BuscaWindow bw) bw.TesteBuscar(arg); break;
            case "responder": App.Atual.Chat.JanelasAbertas.FirstOrDefault()?.TesteResponderUltima(); break;
            case "editar": App.Atual.Chat.JanelasAbertas.FirstOrDefault()?.TesteEditarUltima(); break;
            case "apagar": _ = App.Atual.Chat.JanelasAbertas.FirstOrDefault()?.TesteApagarUltimaAsync(); break;
            case "texto": { var cj = App.Atual.Chat.JanelasAbertas.FirstOrDefault(); if (cj != null) { cj.Caixa.Text = arg; cj.Caixa.CaretIndex = arg.Length; } } break;
            case "link": App.Atual.AbrirLink(arg); break;
            case "comunicado":
                var pc = arg.Split('|');
                _ = App.Atual.Chat.Cliente?.CriarComunicadoAsync(new Softphone.Contratos.NovoComunicadoDto(pc[0], pc.ElementAtOrDefault(1) ?? "", pc.ElementAtOrDefault(2) ?? "info", null, 1));
                break;
        }
    }

    private static void Renderizar(Window w, string caminho)
    {
        var raiz = (FrameworkElement)w.Content;
        double escala = 1.5;
        int largura = (int)Math.Ceiling(raiz.ActualWidth * escala), altura = (int)Math.Ceiling(raiz.ActualHeight * escala);
        if (largura == 0 || altura == 0) return;
        var rtb = new RenderTargetBitmap(largura, altura, 96 * escala, 96 * escala, PixelFormats.Pbgra32);
        var fundo = new DrawingVisual();
        using (var dc = fundo.RenderOpen())
        {
            var bg = w.Background is SolidColorBrush b && b.Color.A > 0 ? b : (Brush)Application.Current.Resources["BgWindow"];
            dc.DrawRectangle(bg, null, new Rect(0, 0, raiz.ActualWidth, raiz.ActualHeight));
        }
        rtb.Render(fundo);
        rtb.Render(raiz);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(caminho);
        enc.Save(fs);
    }
}
