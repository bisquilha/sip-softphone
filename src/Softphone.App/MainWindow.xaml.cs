using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Softphone.Contratos;
using Softphone.Core;

namespace Softphone.App;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<ChamadaVM> _chamadas = new();
    private readonly Dictionary<Guid, ChamadaEntranteWindow> _avisos = new();
    private readonly DispatcherTimer _relogio = new() { Interval = TimeSpan.FromSeconds(1) };
    private int _perdidasNaoVistas;
    private App App => App.Atual;
    private Telefone Tel => App.Telefone;

    public MainWindow()
    {
        InitializeComponent();
        Theme.AplicarBarraTitulo(this);
        ListaChamadas.ItemsSource = _chamadas;
        PosicionarJanela();

        Tel.RegistroMudou += (e, m) => Dispatcher.BeginInvoke(() => AtualizarStatus(e, m));
        Tel.ChamadaNova += c => Dispatcher.BeginInvoke(() => AoChamadaNova(c));
        Tel.ChamadaMudou += c => Dispatcher.BeginInvoke(() => AoChamadaMudou(c));
        Tel.ChamadaEncerrada += c => Dispatcher.BeginInvoke(() => AoChamadaEncerrada(c));
        Tel.ChamadaRecusadaAuto += (n, nome, r) => Dispatcher.BeginInvoke(() =>
        {
            _ = App.Chat.EnviarChamadaAsync(new ChamadaRegistroDto(Guid.NewGuid().ToString("N"), n, "entrada", r == ResultadoChamada.Encaminhada ? "Encaminhada" : "Perdida",
                DateTime.Now, 0, false, r == ResultadoChamada.Encaminhada ? "encaminhada automaticamente" : Tel.NaoPerturbe ? "não perturbe" : "bloqueado"));
            RegistrarHistorico(new RegistroHistorico
        {
            // R15: encaminhamento automático não é chamada perdida
            Data = DateTime.Now, Direcao = DirecaoChamada.Entrada, Numero = n, Nome = nome,
            Resultado = r == ResultadoChamada.Encaminhada ? ResultadoChamada.Encaminhada : ResultadoChamada.Perdida,
            Motivo = r == ResultadoChamada.Encaminhada ? "encaminhada automaticamente" : Tel.NaoPerturbe ? "não perturbe" : "bloqueado",
            });
        });

        Tel.ProblemaAudio += (_, msg) => Dispatcher.BeginInvoke(() => MostrarAviso(ComCodigo("MIC", msg + " Confira o microfone em Configurações e a permissão do Windows.", msg), false));
        Tel.QualidadeMudou += c => Dispatcher.BeginInvoke(() => AoQualidadeMudou(c));

        _relogio.Tick += (_, _) => { foreach (var vm in _chamadas) vm.Tique(); };
        _relogio.Start();

        PreviewTextInput += Janela_PreviewTextInput;
        PreviewKeyDown += Janela_PreviewKeyDown;
        Closing += Janela_Closing;
        LocationChanged += (_, _) => { if (IsLoaded) SalvarPosicaoDepois(); };
        Loaded += (_, _) => AtualizarGravarAuto();

        AtualizarStatus(Tel.EstadoRegistro, Tel.MotivoRegistro);
        AtualizarNaoPerturbe();
        AtualizarRecentes();
        AtualizarContatos();
        ConfigurarChat();
        AtualizarModoTeclado();
        Loaded += (_, _) => CaixaNumero.Focus();
        SourceInitialized += (_, _) => RegistrarAtalhos();
        // as chamadas ocupam no máximo ~60% da altura; o resto fica para o teclado/abas
        // 1.2.10: o teclado precisa manter ~300 px + cabeçalho/abas (~170 px); o que passar disso é dos cartões, que rolam. Em tela baixa (ex.: notebook com 768 px de altura) o teclado não encolhe mais até ficar minúsculo.
        SizeChanged += (_, _) => RolagemChamadas.MaxHeight = Math.Clamp(ActualHeight - 470, 110, Math.Max(200, ActualHeight * 0.6));
    }

    // =================================================================
    // Status do registro
    // =================================================================

    private void AtualizarStatus(EstadoRegistro estado, string? motivo)
    {
        var conta = App.Config.Conta;
        // ramal de USO (o que se disca) quando a conta vem do AD; o login só registra no PABX
        var cfgApp = App.Config;
        string ramal = (cfgApp.ContaGerenciadaAd && !string.IsNullOrWhiteSpace(cfgApp.RamalDeUso) ? cfgApp.RamalDeUso : conta.Usuario)?.Trim() ?? "";
        TxtNome.Text = string.IsNullOrWhiteSpace(conta.NomeExibicao) ? (ramal.Length > 0 ? $"Ramal {ramal}" : "Softphone") : conta.NomeExibicao;
        (string texto, string cor) = estado switch
        {
            EstadoRegistro.Registrado => ($"Online · ramal {ramal}", "DotOk"),
            EstadoRegistro.Registrando => ("Conectando ao PABX...", "DotWait"),
            EstadoRegistro.Falha => ("Sem conexão com o PABX", "DotBad"),
            _ => (conta.Valida ? "Desconectado" : "Conta não configurada", "DotOff"),
        };
        if (estado == EstadoRegistro.Registrado && App.Chat?.Estado == Softphone.Core.EstadoHub.Conectado)
        {
            // telefone ok + chat conectado: mostra a presença (Disponível, Ausente, Em ligação, Não perturbe)
            var p = App.Chat.MinhaPresenca;
            texto = $"{TextoPresenca.Nome(p)} · ramal {ramal}" + (string.IsNullOrWhiteSpace(App.Config.Recado) ? "" : $" · {App.Config.Recado}");
            cor = TextoPresenca.Cor(p);
        }
        else if (Tel.NaoPerturbe && estado == EstadoRegistro.Registrado) { texto = $"Não perturbe · ramal {ramal}"; cor = "DotWait"; }
        TxtStatus.Text = texto;
        PontoStatus.SetResourceReference(Shape_Fill, cor);
        App.AtualizarBandeja($"Softphone - {texto}", Tel.NaoPerturbe);

        if (estado == EstadoRegistro.Falha) MostrarAviso(ComCodigo("REG", (motivo ?? "Falha no registro.") + " Confira o ramal e a senha em Configurações ou avise a TI.", motivo), true);
        else if (estado == EstadoRegistro.Registrado || estado == EstadoRegistro.Registrando) EsconderAviso();
        else if (!conta.Valida) MostrarAviso("Configure a sua conta (ramal e senha) para começar.", true);
    }

    private static readonly DependencyProperty Shape_Fill = System.Windows.Shapes.Shape.FillProperty;

    /// <summary>
    /// 1.3: mensagem de erro com a ação possível e um código de referência para a TI (também vai para o log do app, que a TI
    /// consegue achar pelo mesmo código). Categorias: REG (registro SIP), CHM (chamada), MIC (microfone), TRF (transferência), NET (rede).
    /// </summary>
    public static string ComCodigo(string categoria, string texto, string? detalheTecnico = null)
    {
        string codigo = $"EF-{categoria}-{DateTime.Now:HHmm}-{(uint)(texto + detalheTecnico).GetHashCode() % 0xFFFF:X4}";
        Log.Escrever($"ERRO {codigo}: {texto}" + (detalheTecnico != null ? " | " + detalheTecnico : ""));
        return $"{texto} (código {codigo})";
    }

    public void MostrarAviso(string texto, bool comBotaoConfig)
    {
        TxtAviso.Text = texto;
        BtnAvisoAcao.Visibility = comBotaoConfig ? Visibility.Visible : Visibility.Collapsed;
        Aviso.Visibility = Visibility.Visible;
    }

    private void EsconderAviso() => Aviso.Visibility = Visibility.Collapsed;

    private async void MostrarAvisoTemporario(string texto)
    {
        MostrarAviso(texto, false);
        await Task.Delay(6000);
        if (TxtAviso.Text == texto) EsconderAviso();
    }

    // =================================================================
    // Chamadas
    // =================================================================

    private ChamadaVM? Vm(Chamada c) => _chamadas.FirstOrDefault(v => v.Chamada == c);
    private ChamadaVM? VmDe(object sender) => (sender as FrameworkElement)?.DataContext as ChamadaVM;

    private void AoChamadaNova(Chamada c)
    {
        // 1.3: um evento repetido da mesma chamada não pode abrir segunda tela nem segundo aviso (uma interface por chamada)
        if (Vm(c) != null || _avisos.ContainsKey(c.Id)) { Log.Escrever($"Chamada {c.Id:N} já estava na tela: evento repetido ignorado."); return; }
        var ident = App.Identificar(c.Numero);
        var vm = new ChamadaVM(c, ident?.Nome ?? App.NomeDoContato(c.Numero)) { Detalhe = ident?.Detalhe };
        _chamadas.Insert(0, vm);
        // quem é / o que já se falou com esse número (das minhas ligações gravadas), sem atrasar o toque
        _ = Task.Run(async () =>
        {
            var ctx = await App.Gravacoes.ContextoAsync(c.Numero);
            if (ctx != null) _ = Dispatcher.BeginInvoke(() => vm.Contexto = ctx);
        });
        if (c.Direcao == DirecaoChamada.Entrada)
        {
            var aviso = new ChamadaEntranteWindow(vm);
            aviso.Atender += () => _ = AtenderAsync(vm);
            aviso.Recusar += () => Tel.Desligar(vm.Chamada);
            _avisos[c.Id] = aviso;
            vm.PopupAberto = true;   // 1.3: uma interface só enquanto toca
            aviso.Show();
            PiscarBarraDeTarefas();
            var cfg = App.Config;
            bool interno = new string(c.Numero.Where(char.IsDigit).ToArray()).Length is > 0 and <= 5;
            if (cfg.AtenderAutomatico && (!cfg.AtenderAutomaticoSoInternos || interno) && !Tel.Chamadas.Any(x => x != c && x.Conectada))
            {
                var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(Math.Max(0, cfg.AtenderAutomaticoSegundos)) };
                t.Tick += (_, _) => { t.Stop(); if (c.Estado == EstadoChamada.Recebendo) _ = AtenderAsync(vm); };
                t.Start();
            }
        }
        AtualizarModoTeclado();
    }

    private void AoChamadaMudou(Chamada c)
    {
        Vm(c)?.Atualizar();
        if (c.Estado != EstadoChamada.Recebendo) FecharAviso(c);
        AtualizarModoTeclado();
    }

    private void AoQualidadeMudou(Chamada c)
    {
        var vm = Vm(c);
        if (vm == null) return;
        vm.AtualizarQualidade();
        // ligação ruim por 8 s seguidos: avisa uma vez (normalmente é a rede: Wi-Fi, internet da unidade)
        bool ruim = c.Qualidade.Nivel is NivelQualidade.Ruim or NivelQualidade.SemAudio && c.Estado == EstadoChamada.EmAndamento;
        vm.SegundosRuim = ruim ? vm.SegundosRuim + 1 : 0;
        if (vm.SegundosRuim >= 8 && !vm.AvisouRuim)
        {
            vm.AvisouRuim = true;
            var q = c.Qualidade;
            Log.Escrever($"Ligação ruim com {c.Numero}: {q.Texto}");
            MostrarAvisoTemporario(q.Nivel == NivelQualidade.SemAudio
                ? $"Não está chegando áudio de {vm.Titulo}. Se não ouvir nada, desligue e ligue de novo."
                : $"A ligação com {vm.Titulo} está ruim (perda de {q.PerdaPct:N0}%). Costuma ser a rede: Wi-Fi fraco ou internet da unidade.");
        }
    }

    private readonly HashSet<Guid> _encerradasRegistradas = new();
    private void AoChamadaEncerrada(Chamada c)
    {
        var vm = Vm(c);
        if (vm != null) _chamadas.Remove(vm);
        FecharAviso(c);
        AtualizarModoTeclado();
        // 1.3: uma entrada lógica no histórico por chamada, mesmo se o evento de encerramento vier duas vezes
        if (!_encerradasRegistradas.Add(c.Id)) { Log.Escrever($"Chamada {c.Id:N}: encerramento repetido ignorado."); return; }
        if (_encerradasRegistradas.Count > 500) _encerradasRegistradas.Clear();
        _ = App.Chat.EnviarChamadaAsync(new ChamadaRegistroDto(c.Id.ToString("N"), c.Numero, c.Direcao == DirecaoChamada.Entrada ? "entrada" : "saida",
            c.Resultado.ToString(), c.Inicio, (int)c.Duracao.TotalSeconds, c.Resultado == ResultadoChamada.Transferida, c.Motivo));

        var q = c.QualidadeFinal;
        RegistrarHistorico(new RegistroHistorico
        {
            Data = c.Inicio, Direcao = c.Direcao, Numero = c.Numero, Nome = vm?.NomeContato ?? c.Nome,
            DuracaoSegundos = (int)c.Duracao.TotalSeconds, Resultado = c.Resultado, Motivo = c.Motivo,
            Mos = q == null ? null : Math.Round(q.MosMedio, 2), PerdaPct = q == null ? null : Math.Round(q.PerdaPct, 2),
            JitterMs = q == null ? null : Math.Round(q.JitterMedioMs, 1), Conferencia = vm?.FoiConferencia == true ? true : null,
        });
        // qualidade vai para o Hub (painel da TI); sem Hub ou Hub antigo, só fica no histórico daqui
        if (q != null && q.Segundos >= 5) _ = App.Chat.EnviarQualidadeAsync(c, q, vm?.FoiConferencia == true);

        if (c.Direcao == DirecaoChamada.Entrada && c.Resultado == ResultadoChamada.Perdida)
        {
            App.Notificar("Chamada perdida", vm?.Titulo ?? c.Numero);
        }
        else if (c.Direcao == DirecaoChamada.Saida && c.Atendida == null && c.Resultado is not (ResultadoChamada.Cancelada or ResultadoChamada.Nenhum))
        {
            MostrarAvisoTemporario(ComCodigo("CHM", $"{vm?.Titulo ?? c.Numero}: {c.Motivo}. Tente de novo ou use outro caminho.", c.Resultado.ToString()));
        }
    }

    private void FecharAviso(Chamada c)
    {
        if (Vm(c) is { } vmAviso) vmAviso.PopupAberto = false;
        if (_avisos.Remove(c.Id, out var aviso)) aviso.Close();
    }

    private void RegistrarHistorico(RegistroHistorico r)
    {
        App.Historico.Insert(0, r);
        if (App.Historico.Count > 1000) App.Historico.RemoveRange(1000, App.Historico.Count - 1000);
        App.SalvarHistorico();
        if (r.Direcao == DirecaoChamada.Entrada && r.Resultado == ResultadoChamada.Perdida && AbaRecentes.IsChecked != true) _perdidasNaoVistas++;
        AtualizarRecentes();
    }

    private async Task AtenderAsync(ChamadaVM vm)
    {
        try { await Tel.AtenderAsync(vm.Chamada); }
        catch (Exception ex) { MostrarAvisoTemporario(ComCodigo("CHM", "Não foi possível atender. Peça para ligarem de novo.", ex.Message)); }
    }

    private void Atender_Click(object sender, RoutedEventArgs e) { if (VmDe(sender) is { } vm) _ = AtenderAsync(vm); }
    private void Desligar_Click(object sender, RoutedEventArgs e) { if (VmDe(sender) is { } vm) Tel.Desligar(vm.Chamada); }
    private void Mudo_Click(object sender, RoutedEventArgs e) { if (VmDe(sender) is { } vm) Tel.AlternarMudo(vm.Chamada); }
    private void Gravar_Click(object sender, RoutedEventArgs e) { if (VmDe(sender) is { } vm) { App.Gravacoes.Alternar(vm.Chamada); vm.Atualizar(); } }
    private void Gravacoes_Click(object sender, RoutedEventArgs e) => GravacoesWindow.Abrir(null);
    private async void Espera_Click(object sender, RoutedEventArgs e)
    {
        if (VmDe(sender) is not { } vm) return;
        if (vm.Chamada.EmConferencia) await Tel.SepararConferenciaAsync(vm.Chamada); // "Separar": esta continua, a outra vai para a espera
        else await Tel.AlternarEsperaAsync(vm.Chamada);
    }

    private async void Conferencia_Click(object sender, RoutedEventArgs e)
    {
        if (VmDe(sender) is not { } vm) return;
        var outra = Tel.Chamadas.FirstOrDefault(c => c != vm.Chamada && c.Conectada && !c.EmConferencia);
        if (outra == null) return;
        if (!await Tel.ConferenciaAsync(vm.Chamada, outra)) MostrarAvisoTemporario("Não foi possível juntar as chamadas.");
        else { vm.FoiConferencia = true; if (Vm(outra) is { } vo) vo.FoiConferencia = true; }
    }

    private void Teclado_Click(object sender, RoutedEventArgs e)
    {
        AbaDiscador.IsChecked = true;
        CaixaNumero.Focus();
    }

    private async void Transferir_Click(object sender, RoutedEventArgs e)
    {
        if (VmDe(sender) is not { } vm) return;
        await TransferirChamadaAsync(vm);
    }

    private async Task TransferirChamadaAsync(ChamadaVM vm, string? destinoFixo = null)
    {
        var destino = destinoFixo ?? EntradaWindow.Perguntar(this, "Transferir chamada", $"Transferir {vm.Titulo} para:", "Transferir", vm.Chamada.Numero);
        if (destino == null) return;
        MostrarAvisoTemporario($"Transferindo para {destino}...");
        var r = await Tel.TransferirAsync(vm.Chamada, destino);
        if (!r.Ok) MostrarAvisoTemporario(ComCodigo("TRF", (r.Motivo ?? "O PABX não aceitou a transferência.") + " A chamada continua com você.", r.Motivo));
    }

    private async void Consulta_Click(object sender, RoutedEventArgs e)
    {
        if (VmDe(sender) is not { } vm) return;
        var destino = EntradaWindow.Perguntar(this, "Transferência assistida",
            $"{vm.Titulo} fica em espera enquanto você fala com:", "Ligar", vm.Chamada.Numero);
        if (destino == null) return;
        try { await Tel.IniciarConsultaAsync(vm.Chamada, destino); }
        catch (Exception ex) { MostrarAvisoTemporario("Não foi possível ligar: " + ex.Message); }
    }

    private async void ConcluirConsulta_Click(object sender, RoutedEventArgs e)
    {
        if (VmDe(sender) is not { } vm) return;
        var r = await Tel.ConcluirConsultaAsync(vm.Chamada);
        if (!r.Ok) MostrarAvisoTemporario(ComCodigo("TRF", (r.Motivo ?? "O PABX não aceitou a transferência.") + " As duas chamadas continuam com você.", r.Motivo));
    }

    private Chamada? ChamadaEmConversa => Tel.Chamadas.FirstOrDefault(c => c.Estado == EstadoChamada.EmAndamento);

    private double? _alturaSemChamadas;
    private int _cartoesNaUltimaMedida;

    /// <summary>
    /// 1.2.5 (11/11/2026, pedido do Ana): com ligação na tela o cartão toma o lugar do teclado, que encolhia até ficar ilegível.
    /// Agora a janela cresce o tamanho dos cartões (até caber na área útil da tela) e volta ao tamanho de antes quando a ligação acaba.
    /// Só mexe quando o número de cartões muda: se a pessoa redimensionar no meio da ligação, o app respeita.
    /// </summary>
    private void AjustarAlturaParaChamadas()
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (WindowState != WindowState.Normal) return;
            int cartoes = _chamadas.Count(v => v.ExibirCartao);
            if (cartoes == _cartoesNaUltimaMedida) return;
            _cartoesNaUltimaMedida = cartoes;
            if (cartoes == 0)
            {
                if (_alturaSemChamadas is double h) { Height = h; _alturaSemChamadas = null; }
                return;
            }
            _alturaSemChamadas ??= ActualHeight;
            var area = SystemParameters.WorkArea;
            double alvo = Math.Min(_alturaSemChamadas.Value + RolagemChamadas.ActualHeight, area.Height);
            if (Math.Abs(Height - alvo) < 1) return;
            Height = alvo;
            if (Top + alvo > area.Bottom) Top = Math.Max(area.Top, area.Bottom - alvo);
        }));
    }

    /// <summary>Com chamada em andamento, o teclado manda DTMF (URA); senão, digita o número.</summary>
    private void AtualizarModoTeclado()
    {
        AjustarAlturaParaChamadas();
        bool dtmf = ChamadaEmConversa != null;
        TxtModoTeclado.Visibility = dtmf ? Visibility.Visible : Visibility.Collapsed;
        BtnLigar.Visibility = dtmf ? Visibility.Collapsed : Visibility.Visible;
        CaixaNumero.IsReadOnly = dtmf;
        if (!dtmf && CaixaNumero.Tag as string == "dtmf") { CaixaNumero.Text = ""; CaixaNumero.Tag = null; }
        DicaNumero.Visibility = CaixaNumero.Text.Length == 0 && !dtmf ? Visibility.Visible : Visibility.Collapsed;

        bool temOutraEmFoco(ChamadaVM vm) => _chamadas.Any(o => o != vm && !o.EmEspera);
        // conferência: a primeira perna da lista fica com o cartão completo; a outra fica compacta (Separar / Desligar)
        bool outraPernaAntes(ChamadaVM vm) => vm.EmConferencia && _chamadas.TakeWhile(o => o != vm).Any(o => o.Chamada == vm.Chamada.ConferenciaCom);
        foreach (var vm in _chamadas)
        {
            vm.Compacto = (vm.EmEspera && temOutraEmFoco(vm)) || outraPernaAntes(vm);
            vm.PodeConferencia = vm.Conectada && !vm.EmConferencia && _chamadas.Any(o => o != vm && o.Conectada && !o.EmConferencia);
        }
    }

    private void EnviarTecla(char tecla)
    {
        var c = ChamadaEmConversa;
        if (c != null)
        {
            if (CaixaNumero.Tag as string != "dtmf") { CaixaNumero.Text = ""; CaixaNumero.Tag = "dtmf"; }
            CaixaNumero.Text += tecla;
            _ = Tel.EnviarDtmfAsync(c, tecla);
            return;
        }
        int pos = CaixaNumero.SelectionStart;
        CaixaNumero.Text = CaixaNumero.Text.Remove(pos, CaixaNumero.SelectionLength).Insert(pos, tecla.ToString());
        CaixaNumero.SelectionStart = pos + 1;
        Tel.Toques.Dtmf(tecla);
        CaixaNumero.Focus();
    }

    private void Tecla_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Uid.Length == 1) EnviarTecla(b.Uid[0]);
    }

    private void Apagar_Click(object sender, RoutedEventArgs e)
    {
        if (CaixaNumero.IsReadOnly) return;
        if (CaixaNumero.Text.Length > 0) CaixaNumero.Text = CaixaNumero.Text[..^1];
        CaixaNumero.SelectionStart = CaixaNumero.Text.Length;
        CaixaNumero.Focus();
    }

    private void Ligar_Click(object sender, RoutedEventArgs e) => _ = LigarAsync(CaixaNumero.Text);

    public async Task LigarAsync(string numero)
    {
        numero = Telefone.LimparDestino(numero);
        if (numero.Length == 0)
        {
            // Botão Ligar com a caixa vazia: traz o último número discado (como nos telefones de mesa).
            var ultimo = App.Historico.FirstOrDefault(h => h.Direcao == DirecaoChamada.Saida);
            if (ultimo != null) { CaixaNumero.Text = ultimo.Numero; CaixaNumero.SelectionStart = CaixaNumero.Text.Length; }
            return;
        }
        if (!App.Config.Conta.Valida) { AbrirConfiguracoes(primeiraVez: true); return; }
        // 1.3: clique repetido, Enter e atalho ao mesmo tempo ou duas origens (lista + link) não discam duas vezes o mesmo número
        if (_discando || (numero == _ultimoDiscado && (DateTime.Now - _ultimaDiscagem).TotalSeconds < 3)
            || Tel.Chamadas.Any(x => x.Direcao == DirecaoChamada.Saida && x.Estado is EstadoChamada.Discando or EstadoChamada.Chamando && Telefone.LimparDestino(x.Numero) == numero))
        { Log.Escrever($"Discagem repetida para {numero} ignorada."); return; }
        _discando = true; _ultimoDiscado = numero; _ultimaDiscagem = DateTime.Now;
        try
        {
            await Tel.LigarAsync(numero);
            CaixaNumero.Text = "";
            AbaDiscador.IsChecked = true;
        }
        catch (Exception ex)
        {
            MostrarAvisoTemporario(ComCodigo("CHM", "Não foi possível ligar. Confira o número e a conexão e tente de novo.", ex.Message));
        }
        finally { _discando = false; }
    }

    private bool _discando;
    private string _ultimoDiscado = "";
    private DateTime _ultimaDiscagem = DateTime.MinValue;

    /// <summary>Põe um número no discador (link clicado sem "ligar na hora", ou já em ligação).</summary>
    public void PreencherNumero(string numero)
    {
        AbaDiscador.IsChecked = true;
        if (CaixaNumero.IsReadOnly) { MostrarAvisoTemporario($"Número do link: {numero} (você está em ligação; ligue depois ou use Consultar)."); return; }
        CaixaNumero.Text = numero;
        CaixaNumero.SelectionStart = numero.Length;
        CaixaNumero.Focus();
    }

    private void CaixaNumero_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !CaixaNumero.IsReadOnly) { _ = LigarAsync(CaixaNumero.Text); e.Handled = true; }
    }

    private void CaixaNumero_TextChanged(object sender, TextChangedEventArgs e)
    {
        BtnApagar.Visibility = CaixaNumero.Text.Length > 0 && !CaixaNumero.IsReadOnly ? Visibility.Visible : Visibility.Hidden;
        DicaNumero.Visibility = CaixaNumero.Text.Length == 0 && !CaixaNumero.IsReadOnly ? Visibility.Visible : Visibility.Collapsed;
        var n = Telefone.LimparDestino(CaixaNumero.Text);
        TxtSugestao.Text = n.Length >= 3 && !CaixaNumero.IsReadOnly ? App.NomeDoContato(n) ?? "" : "";
    }

    private void Janela_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        // Durante a chamada, digitar 0-9 * # em qualquer lugar da janela (fora de outras caixas) manda DTMF.
        if (e.Text.Length != 1 || "0123456789*#".IndexOf(e.Text[0]) < 0) return;
        if (Keyboard.FocusedElement is TextBox tb && tb != CaixaNumero) return;
        if (ChamadaEmConversa != null || Keyboard.FocusedElement != CaixaNumero)
        {
            if (PainelDiscador.Visibility != Visibility.Visible) return;
            EnviarTecla(e.Text[0]);
            e.Handled = true;
        }
        else if (PainelDiscador.Visibility == Visibility.Visible && !CaixaNumero.IsReadOnly)
        {
            // 1.2.8 (11/11/2026, pedido do Ana): digitando no campo do número (teclado do computador, inclusive o numérico) o som da tecla
            // também toca; o texto entra normalmente (não marca como tratado).
            Tel.Toques.Dtmf(e.Text[0]);
        }
    }

    private void Janela_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            // Esc cancela a chamada que está chamando (saída); não derruba conversa em andamento.
            var chamando = Tel.Chamadas.FirstOrDefault(c => c.Estado is EstadoChamada.Discando or EstadoChamada.Chamando);
            if (chamando != null) { Tel.Desligar(chamando); e.Handled = true; }
        }
        else if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control && Keyboard.FocusedElement is not TextBox && Clipboard.ContainsText())
        {
            AbaDiscador.IsChecked = true;
            CaixaNumero.Text = Telefone.LimparDestino(Clipboard.GetText());
            CaixaNumero.SelectionStart = CaixaNumero.Text.Length;
            CaixaNumero.Focus();
            e.Handled = true;
        }
    }

    private void PiscarBarraDeTarefas()
    {
        if (IsActive) return;
        var info = new FLASHWINFO
        {
            cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<FLASHWINFO>(),
            hwnd = new WindowInteropHelper(this).Handle,
            dwFlags = 3 | 12, // FLASHW_ALL | FLASHW_TIMERNOFG
            uCount = 0,
        };
        if (info.hwnd != IntPtr.Zero) FlashWindowEx(ref info);
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct FLASHWINFO { public uint cbSize; public IntPtr hwnd; public uint dwFlags; public uint uCount; public uint dwTimeout; }
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool FlashWindowEx(ref FLASHWINFO pwfi);

    // =================================================================
    // Abas, Recentes, Contatos
    // =================================================================

    private void Aba_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized || PainelDiscador == null) return;
        PainelDiscador.Visibility = AbaDiscador.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PainelRecentes.Visibility = AbaRecentes.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PainelContatos.Visibility = AbaContatos.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PainelConversas.Visibility = AbaConversas.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PainelAtalhos.Visibility = AbaAtalhos.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        if (AbaAtalhos.IsChecked == true) MostrarAtalhos();
        else PararAtalhos();
        if (AbaRecentes.IsChecked == true) { _perdidasNaoVistas = 0; AtualizarRecentes(); }
        if (AbaDiscador.IsChecked == true) CaixaNumero.Focus();
        if (AbaContatos.IsChecked == true) CaixaBusca.Focus();
    }

    private void BuscaRecentes_TextChanged(object sender, TextChangedEventArgs e) { if (IsInitialized && ListaRecentes != null) AtualizarRecentes(); }
    private void FiltroRecentes_Changed(object sender, RoutedEventArgs e) { if (IsInitialized && ListaRecentes != null && ChkHistoricoCompleto != null && FiltroResultado != null) AtualizarRecentes(); }

    private static bool CasaResultado(ResultadoChamada r, int filtro) => filtro switch
    {
        1 => r is ResultadoChamada.Atendida or ResultadoChamada.Transferida,
        2 => r == ResultadoChamada.Perdida,
        3 => r == ResultadoChamada.Recusada,
        4 => r is ResultadoChamada.Falhou or ResultadoChamada.Ocupado or ResultadoChamada.NaoAtendida,
        _ => true,
    };

    private void AtualizarRecentes()
    {
        // 1.3: busca (nome, número, ramal, setor, data) + direção + resultado; sem filtro mostra as 300 mais recentes, com "Histórico completo" todas
        string consulta = BuscaRecentes?.Text ?? "";
        int dir = FiltroDirecao?.SelectedIndex ?? 0, res = FiltroResultado?.SelectedIndex ?? 0;
        bool completo = ChkHistoricoCompleto?.IsChecked == true;
        var filtradas = App.Historico.Where(h =>
            (dir == 0 || (dir == 1 ? h.Direcao == DirecaoChamada.Entrada : h.Direcao == DirecaoChamada.Saida))
            && CasaResultado(h.Resultado, res)
            && Softphone.Core.BuscaHistorico.Combina(App.NomeDoContato(h.Numero) ?? h.Nome, h.Numero, App.Contatos.FirstOrDefault(c => Armazenamento.MesmoNumero(c.Numero, h.Numero))?.Setor ?? App.Chat?.PessoaPorRamal(h.Numero)?.Setor, h.Data, consulta)).ToList();
        ListaRecentes.ItemsSource = (completo ? filtradas : filtradas.Take(300)).Select(h => new HistoricoVM(h, App.NomeDoContato(h.Numero))).ToList();
        if (TxtContagemRecentes != null)
            TxtContagemRecentes.Text = App.Historico.Count == 0 ? "" : filtradas.Count == App.Historico.Count
                ? $"{filtradas.Count} chamada(s)" : $"{filtradas.Count} de {App.Historico.Count} chamada(s)";
        TxtSemRecentes.Text = App.Historico.Count == 0 ? "Nenhuma chamada ainda." : "Nenhuma chamada com esses filtros.";
        TxtSemRecentes.Visibility = filtradas.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        TxtAbaRecentes.Text = _perdidasNaoVistas > 0 ? $"Recentes ({_perdidasNaoVistas})" : "Recentes";
        TxtAbaRecentes.SetResourceReference(TextBlock.ForegroundProperty, _perdidasNaoVistas > 0 ? "BadText" : "TextSecondary");
        if (_perdidasNaoVistas == 0) TxtAbaRecentes.ClearValue(TextBlock.ForegroundProperty);
    }

    private void AtualizarContatos()
    {
        string busca = CaixaBusca.Text.Trim();
        DicaBusca.Visibility = busca.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        var lista = App.Contatos
            .Where(c => busca.Length == 0 || c.Nome.Contains(busca, StringComparison.CurrentCultureIgnoreCase) || c.Numero.Contains(busca) ||
                        (c.Setor?.Contains(busca, StringComparison.CurrentCultureIgnoreCase) ?? false))
            .OrderBy(c => c.Nome, StringComparer.CurrentCultureIgnoreCase)
            .Select(c => new ContatoVM(c)).ToList();
        ListaContatos.ItemsSource = lista;
        TxtSemContatos.Visibility = FiltroMeus.IsChecked == true && App.Contatos.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Busca_TextChanged(object sender, TextChangedEventArgs e)
    {
        AtualizarContatos();
        _vistaEmpresa?.Refresh();
    }

    // =================================================================
    // Chat interno, presença e diretório da empresa
    // =================================================================

    private ListCollectionView? _vistaEmpresa;

    private void ConfigurarChat()
    {
        var chat = App.Chat;
        ListaConversas.ItemsSource = chat.Conversas;
        _vistaEmpresa = new ListCollectionView(chat.Pessoas)
        {
            Filter = o => o is PessoaVM p && !string.Equals(p.Login, chat.EuLogin, StringComparison.OrdinalIgnoreCase) &&
                          (ChkSoOnline.IsChecked != true || p.Online) && p.Combina(CaixaBusca.Text.Trim()),
            IsLiveFiltering = true,
        };
        _vistaEmpresa.LiveFilteringProperties.Add(nameof(PessoaVM.Online));
        ListaEmpresa.ItemsSource = _vistaEmpresa;
        chat.Mudou += AtualizarChat;
        chat.Conversas.CollectionChanged += (_, _) => AtualizarChat();
        chat.Pessoas.CollectionChanged += (_, _) => AtualizarChat();
        AtualizarChat();
    }

    private void AtualizarChat()
    {
        var chat = App.Chat;
        int n = chat.NaoLidasTotal;
        SeloConversas.Visibility = n > 0 ? Visibility.Visible : Visibility.Collapsed;
        TxtSeloConversas.Text = n > 99 ? "99+" : n.ToString();
        TxtSemConversas.Visibility = chat.Conversas.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        TxtSemEmpresa.Visibility = FiltroEmpresa.IsChecked == true && chat.Pessoas.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        (string? aviso, bool entrar) = !chat.Ativo
            ? ("O chat ainda não foi configurado neste computador (endereço do servidor em branco).", false)
            : chat.Estado switch
            {
                Softphone.Core.EstadoHub.Conectado => ((string?)null, false),
                Softphone.Core.EstadoHub.PrecisaSenha => (chat.Motivo ?? "Entre com o seu usuário de rede para usar o chat.", true),
                Softphone.Core.EstadoHub.Conectando => ("Conectando ao servidor do chat...", false),
                _ => ((chat.Motivo ?? "Sem conexão com o servidor do chat.") + " Tentando de novo...", false),
            };
        AvisoChat.Visibility = aviso is null ? Visibility.Collapsed : Visibility.Visible;
        TxtAvisoChat.Text = aviso ?? "";
        BtnEntrarChat.Visibility = entrar ? Visibility.Visible : Visibility.Collapsed;
        // 1.1: painel da TI só para admin (o Hub diz no login); busca e painel de ramais dependem do Hub
        bool conectado = chat.Estado == Softphone.Core.EstadoHub.Conectado;
        BtnAdmin.Visibility = conectado && chat.Cliente?.Admin == true ? Visibility.Visible : Visibility.Collapsed;
        BtnBuscarConversas.Visibility = conectado && chat.Cliente?.ServidorV11 == true ? Visibility.Visible : Visibility.Collapsed;
        BtnRamais.Visibility = chat.Ativo ? Visibility.Visible : Visibility.Collapsed;
        AtualizarStatus(Tel.EstadoRegistro, Tel.MotivoRegistro);
    }

    private void Ramais_Click(object sender, RoutedEventArgs e) => RamaisWindow.Abrir();
    // 1.1.1: a administração é o painel web do Hub (/admin/); o app só abre no navegador padrão
    private void Admin_Click(object sender, RoutedEventArgs e)
    {
        var url = App.Atual.Chat.Cliente?.UrlPainel;
        if (url == null) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex)
        {
            Log.Escrever("Painel web: " + ex.Message);
            MessageBox.Show(this, $"Não deu para abrir o navegador. Abra este endereço:\n{url}", "Softphone", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
    private void BuscarConversas_Click(object sender, RoutedEventArgs e) => BuscaWindow.Abrir(this);

    private void FiltroPessoas_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized || ListaEmpresa == null) return;
        bool empresa = FiltroEmpresa.IsChecked == true;
        ListaEmpresa.Visibility = empresa ? Visibility.Visible : Visibility.Collapsed;
        ListaContatos.Visibility = empresa ? Visibility.Collapsed : Visibility.Visible;
        BtnNovoContato.Visibility = empresa ? Visibility.Collapsed : Visibility.Visible;
        ChkSoOnline.Visibility = empresa ? Visibility.Visible : Visibility.Collapsed;
        AtualizarContatos();
        AtualizarChat();
    }

    private void ChkSoOnline_Changed(object sender, RoutedEventArgs e) => _vistaEmpresa?.Refresh();

    private void Conversa_Abrir(object sender, MouseButtonEventArgs e) { if (ListaConversas.SelectedItem is ConversaVM c) App.Chat.Abrir(c); }
    private void Conversa_Clique(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && ItemsControl.ContainerFromElement(ListaConversas, d) is ListBoxItem { DataContext: ConversaVM c }) App.Chat.Abrir(c);
    }
    private void Conversa_KeyDown(object sender, KeyEventArgs e)
    {
        if (ListaConversas.SelectedItem is not ConversaVM c) return;
        if (e.Key == Key.Enter) { App.Chat.Abrir(c); e.Handled = true; }
        else if (e.Key == Key.Delete) { _ = OcultarAsync(c); e.Handled = true; }
    }

    private ConversaVM? ConversaSelecionada => ListaConversas.SelectedItem as ConversaVM;

    private void MenuConversa_Opening(object sender, ContextMenuEventArgs e)
    {
        if (ConversaSelecionada is not { } c) { e.Handled = true; return; }
        MenuConversaSair.Visibility = c.Grupo ? Visibility.Visible : Visibility.Collapsed;
        MenuConversaExcluir.Visibility = App.Chat.SouCriador(c) ? Visibility.Visible : Visibility.Collapsed;
        MenuConversaVideo.Visibility = c.Grupo || c.Outra?.Online == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void MenuConversaAbrir_Click(object sender, RoutedEventArgs e) { if (ConversaSelecionada is { } c) App.Chat.Abrir(c); }
    private void MenuConversaVideo_Click(object sender, RoutedEventArgs e) { if (ConversaSelecionada is { } c) _ = App.Chat.VideoNaConversaAsync(c); }
    private void MenuConversaOcultar_Click(object sender, RoutedEventArgs e) { if (ConversaSelecionada is { } c) _ = OcultarAsync(c); }

    private async Task OcultarAsync(ConversaVM c)
    {
        if (await App.Chat.AcaoConversaAsync(c, h => h.OcultarConversaAsync(c.Id)))
            MostrarAvisoTemporario($"\"{c.Titulo}\" foi ocultada. Ela volta se chegar mensagem nova (o histórico continua guardado).");
    }

    private async void MenuConversaSair_Click(object sender, RoutedEventArgs e)
    {
        if (ConversaSelecionada is not { Grupo: true } c) return;
        if (MessageBox.Show(this, $"Sair do grupo \"{c.Titulo}\"? Você deixa de receber as mensagens dele.", "Sair do grupo",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await App.Chat.AcaoConversaAsync(c, h => h.SairDoGrupoAsync(c.Id));
    }

    private async void MenuConversaExcluir_Click(object sender, RoutedEventArgs e)
    {
        if (ConversaSelecionada is not { Grupo: true } c) return;
        if (MessageBox.Show(this, $"Excluir o grupo \"{c.Titulo}\" PARA TODOS?\n\nAs mensagens e os arquivos do grupo serão apagados de vez, para todos os {c.Dto.Membros.Length} participantes. Não dá para desfazer.",
                "Excluir grupo", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        await App.Chat.AcaoConversaAsync(c, h => h.ExcluirGrupoAsync(c.Id));
    }

    private void NovaConversa_Click(object sender, RoutedEventArgs e)
    {
        AbaContatos.IsChecked = true;
        FiltroEmpresa.IsChecked = true;
        CaixaBusca.Focus();
    }

    private async void NovoGrupo_Click(object sender, RoutedEventArgs e)
    {
        if (App.Chat.Estado != Softphone.Core.EstadoHub.Conectado) { MostrarAvisoTemporario("O chat está sem conexão no momento."); return; }
        var j = new NovoGrupoWindow(App.Chat.Pessoas, App.Chat.EuLogin) { Owner = this };
        if (j.ShowDialog() == true) await App.Chat.CriarGrupoAsync(j.Titulo, j.Logins);
    }

    private async void EntrarChat_Click(object sender, RoutedEventArgs e)
    {
        var j = new EntrarHubWindow(App.Chat) { Owner = this };
        j.ShowDialog();
        await System.Threading.Tasks.Task.CompletedTask;
    }

    private PessoaVM? PessoaDe(object sender) =>
        (sender as FrameworkElement)?.DataContext as PessoaVM ?? (ItemSelecionado(sender) as PessoaVM);

    private void Pessoa_DoubleClick(object sender, MouseButtonEventArgs e) { if (ListaEmpresa.SelectedItem is PessoaVM p) _ = App.Chat.AbrirComAsync(p.Login); }
    private void Pessoa_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter && ListaEmpresa.SelectedItem is PessoaVM p) { _ = App.Chat.AbrirComAsync(p.Login); e.Handled = true; } }
    private void PessoaConversar_Click(object sender, RoutedEventArgs e) { if (PessoaDe(sender) is { } p) _ = App.Chat.AbrirComAsync(p.Login); }
    private void PessoaVideo_Click(object sender, RoutedEventArgs e) { if (PessoaDe(sender) is { } p) _ = App.Chat.VideoComAsync(p.Login); }
    private void MenuPessoaVideo_Click(object sender, RoutedEventArgs e) { if (ItemSelecionado(sender) is PessoaVM p) _ = App.Chat.VideoComAsync(p.Login); }
    private void ReunirAgora_Click(object sender, RoutedEventArgs e) => _ = App.Chat.NovaReuniaoAsync(this);
    private void PessoaLigar_Click(object sender, RoutedEventArgs e) { if (PessoaDe(sender) is { TemRamal: true } p) _ = LigarAsync(p.Ramal!); }
    private void MenuPessoaConversar_Click(object sender, RoutedEventArgs e) { if (ItemSelecionado(sender) is PessoaVM p) _ = App.Chat.AbrirComAsync(p.Login); }
    private void MenuPessoaLigar_Click(object sender, RoutedEventArgs e) { if (ItemSelecionado(sender) is PessoaVM { TemRamal: true } p) _ = LigarAsync(p.Ramal!); }
    private void MenuPessoaCopiar_Click(object sender, RoutedEventArgs e) { if (ItemSelecionado(sender) is PessoaVM { TemRamal: true } p) Clipboard.SetText(p.Ramal!); }

    private void MenuPessoaSalvar_Click(object sender, RoutedEventArgs e)
    {
        if (ItemSelecionado(sender) is not PessoaVM p) return;
        if (!p.TemRamal) { MostrarAvisoTemporario($"{p.Nome} ainda não tem ramal no diretório."); return; }
        if (App.Contatos.Any(c => Armazenamento.MesmoNumero(c.Numero, p.Ramal!))) { MostrarAvisoTemporario("Esse ramal já está nos seus contatos."); return; }
        App.Contatos.Add(new Contato { Nome = p.Nome, Numero = p.Ramal!, Setor = string.Join(" · ", new[] { p.Setor, p.Unidade }.Where(x => !string.IsNullOrWhiteSpace(x))) });
        App.SalvarContatos();
        AtualizarContatos();
        MostrarAvisoTemporario($"{p.Nome} salvo nos seus contatos.");
    }

    // ---------- minha presença ----------

    private void Presenca_Click(object sender, RoutedEventArgs e)
    {
        MenuLimparRecado.IsEnabled = !string.IsNullOrWhiteSpace(App.Config.Recado);
        foreach (var mi in MenuPresenca.Items.OfType<MenuItem>().Where(m => m.Tag is string))
            mi.IsChecked = (string)mi.Tag == (Tel.NaoPerturbe ? "NaoPerturbe" : App.Config.PresencaEscolhida.ToString());
        MenuPresenca.PlacementTarget = BtnPresenca;
        MenuPresenca.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        MenuPresenca.IsOpen = true;
    }

    private async void MenuPresenca_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.Tag is not string tag || !Enum.TryParse<Softphone.Contratos.EstadoPresenca>(tag, out var estado)) return;
        await DefinirMinhaPresencaAsync(estado);
    }

    /// <summary>Usado pelo menu de presença e pelos atalhos "Mudar meu status" (1.2.4).</summary>
    private async Task DefinirMinhaPresencaAsync(Softphone.Contratos.EstadoPresenca estado)
    {
        // "Não perturbe" na presença é o mesmo não perturbe do telefone (recusa chamadas e silencia avisos do chat)
        if (Tel.NaoPerturbe != (estado == Softphone.Contratos.EstadoPresenca.NaoPerturbe)) DefinirNaoPerturbe(estado == Softphone.Contratos.EstadoPresenca.NaoPerturbe);
        await App.Chat.EscolherPresencaAsync(estado == Softphone.Contratos.EstadoPresenca.NaoPerturbe ? Softphone.Contratos.EstadoPresenca.Disponivel : estado, null);
    }

    private async void MenuRecado_Click(object sender, RoutedEventArgs e)
    {
        var recado = PerguntarTexto("Recado", "Um recado curto que aparece para os colegas (ex.: \"em reunião até 15h\", \"home office\"):", App.Config.Recado ?? "", 120);
        if (recado != null) await App.Chat.EscolherPresencaAsync(App.Config.PresencaEscolhida, recado.Trim());
    }

    private async void MenuLimparRecado_Click(object sender, RoutedEventArgs e) => await App.Chat.EscolherPresencaAsync(App.Config.PresencaEscolhida, "");

    /// <summary>Caixa simples de texto (sem XAML próprio).</summary>
    private string? PerguntarTexto(string titulo, string pergunta, string valor, int max)
    {
        var caixa = new TextBox { Text = valor, MaxLength = max, Margin = new Thickness(0, 8, 0, 0) };
        var ok = new Button { Content = "OK", IsDefault = true, Style = (Style)FindResource("BtnPrimario"), Margin = new Thickness(8, 0, 0, 0) };
        var cancelar = new Button { Content = "Cancelar", IsCancel = true, Style = (Style)FindResource("BtnContorno") };
        var botoes = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0), Children = { cancelar, ok } };
        var j = new Window
        {
            Title = titulo, Width = 380, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize, Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false, FontFamily = FontFamily, FontSize = 13,
            Content = new StackPanel { Margin = new Thickness(18, 14, 18, 16), Children = { new TextBlock { Text = pergunta, TextWrapping = TextWrapping.Wrap }, caixa, botoes } },
        };
        j.SetResourceReference(BackgroundProperty, "BgWindow");
        j.SetResourceReference(ForegroundProperty, "TextPrimary");
        Theme.AplicarBarraTitulo(j);
        ok.Click += (_, _) => j.DialogResult = true;
        j.Loaded += (_, _) => { caixa.Focus(); caixa.SelectAll(); };
        return j.ShowDialog() == true ? caixa.Text : null;
    }

    public void PiscarSePreciso() => PiscarBarraDeTarefas();

    // ---------- atalhos de teclado em qualquer janela ----------
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    private const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_NOREPEAT = 0x4000;
    private HwndSource? _fonteJanela;

    private void RegistrarAtalhos()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        for (int id = 1; id <= 16; id++) UnregisterHotKey(hwnd, id);
        if (!App.Config.AtalhosGlobais) return;
        // 1.3: atalhos configuráveis por pessoa; o que o Windows recusar (outro programa já usa) vai para o log com o nome da ação
        var recusados = new List<string>();
        foreach (var a in AtalhosApp.Acoes)
        {
            var texto = AtalhosApp.Efetivo(App.Config, a);
            if (!AtalhosApp.Tentar(texto, out var mods, out var vk)) continue;
            if (a.Id >= 8 && string.IsNullOrWhiteSpace(App.Config.Favoritos?.ElementAtOrDefault(a.Id - 8))) continue;   // favorito sem número: não ocupa o atalho
            if (!RegisterHotKey(hwnd, a.Id, mods | MOD_NOREPEAT, vk)) recusados.Add($"{a.Nome} ({texto})");
        }
        if (recusados.Count > 0) Log.Escrever("Atalhos já usados por outro programa: " + string.Join("; ", recusados));
        if (_fonteJanela == null)
        {
            _fonteJanela = HwndSource.FromHwnd(hwnd);
            _fonteJanela?.AddHook(GanchoJanela);
        }
    }
    private IntPtr GanchoJanela(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_HOTKEY = 0x0312;
        if (msg != WM_HOTKEY) return IntPtr.Zero;
        handled = true;
        switch (wParam.ToInt32())
        {
            case 1: // atender
                var tocando = _chamadas.FirstOrDefault(v => v.Tocando);
                if (tocando != null) _ = AtenderAsync(tocando);
                break;
            case 2: // desligar a da vez
                var c = Tel.Chamadas.FirstOrDefault(x => x.Estado is EstadoChamada.EmAndamento or EstadoChamada.Chamando or EstadoChamada.Discando)
                        ?? Tel.Chamadas.FirstOrDefault(x => x.Estado == EstadoChamada.Recebendo);
                if (c != null) Tel.Desligar(c);
                break;
            case 3: // mudo
                var emConversa = Tel.Chamadas.FirstOrDefault(x => x.Estado == EstadoChamada.EmAndamento);
                if (emConversa != null) Tel.AlternarMudo(emConversa);
                break;
            case 4: // espera
                var paraEspera = Tel.Chamadas.FirstOrDefault(x => x.Conectada || x.Estado == EstadoChamada.EmEspera);
                if (paraEspera != null) _ = Tel.AlternarEsperaAsync(paraEspera);
                break;
            case 5: // transferir a chamada em conversa
                if (Vm(Tel.Chamadas.FirstOrDefault(x => x.Estado == EstadoChamada.EmAndamento)!) is { } vmT) { App.MostrarJanela(); _ = TransferirChamadaAsync(vmT); }
                break;
            case 6: // gravar
                var gravavel = Tel.Chamadas.FirstOrDefault(x => x.Estado == EstadoChamada.EmAndamento);
                if (gravavel != null) { App.Gravacoes.Alternar(gravavel); Vm(gravavel)?.Atualizar(); }
                break;
            case 7: // discagem rápida: traz o discador
                App.MostrarJanela(); AbaDiscador.IsChecked = true; CaixaNumero.Focus();
                break;
            case 8: case 9: case 10: // favoritos
                var fav = App.Config.Favoritos?.ElementAtOrDefault(wParam.ToInt32() - 8);
                if (!string.IsNullOrWhiteSpace(fav) && Tel.Chamadas.All(x => !x.Ativa || x.Estado == EstadoChamada.Recebendo)) _ = LigarAsync(fav);
                break;
        }
        return IntPtr.Zero;
    }

    private static string? NumeroDe(object? item) => item switch
    {
        HistoricoVM h => h.Numero,
        ContatoVM c => c.Numero,
        _ => null,
    };

    private object? ItemSelecionado(object sender)
    {
        if (sender is MenuItem mi && mi.Parent is ContextMenu cm && cm.PlacementTarget is ListBox lb) return lb.SelectedItem;
        if (sender is ListBox l) return l.SelectedItem;
        return (sender as FrameworkElement)?.DataContext;
    }

    private void LigarItem_Click(object sender, RoutedEventArgs e) { if (NumeroDe((sender as FrameworkElement)?.DataContext) is { } n) _ = LigarAsync(n); }
    private void Lista_DoubleClick(object sender, MouseButtonEventArgs e) { if (NumeroDe(ItemSelecionado(sender)) is { } n) _ = LigarAsync(n); }
    private void Lista_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter && NumeroDe(ItemSelecionado(sender)) is { } n) { _ = LigarAsync(n); e.Handled = true; } }
    private void MenuLigar_Click(object sender, RoutedEventArgs e) { if (NumeroDe(ItemSelecionado(sender)) is { } n) _ = LigarAsync(n); }
    private void MenuCopiar_Click(object sender, RoutedEventArgs e) { if (NumeroDe(ItemSelecionado(sender)) is { } n) Clipboard.SetText(n); }

    private void MenuSalvarContato_Click(object sender, RoutedEventArgs e)
    {
        if (ItemSelecionado(sender) is not HistoricoVM h) return;
        var existente = App.Contatos.FirstOrDefault(c => Armazenamento.MesmoNumero(c.Numero, h.Numero));
        var c = ContatoWindow.Editar(this, existente ?? new Contato { Nome = h.Registro.Nome ?? "", Numero = h.Numero });
        if (c == null) return;
        if (existente == null) App.Contatos.Add(c);
        App.SalvarContatos();
        AtualizarContatos();
        AtualizarRecentes();
    }

    private void MenuBloquear_Click(object sender, RoutedEventArgs e)
    {
        if (NumeroDe(ItemSelecionado(sender)) is not { } n) return;
        if (MessageBox.Show(this, $"Bloquear o número {n}? As chamadas dele serão recusadas automaticamente.\n(Para desbloquear: Configurações > Números bloqueados.)",
                "Bloquear número", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        if (!App.Config.Bloqueados.Any(b => Armazenamento.MesmoNumero(b, n))) App.Config.Bloqueados.Add(n);
        App.SalvarConfig();
    }

    private void MenuApagarHistorico_Click(object sender, RoutedEventArgs e)
    {
        if (ItemSelecionado(sender) is not HistoricoVM h) return;
        App.Historico.Remove(h.Registro);
        App.SalvarHistorico();
        AtualizarRecentes();
    }

    private void LimparHistorico_Click(object sender, RoutedEventArgs e)
    {
        if (App.Historico.Count == 0) return;
        if (MessageBox.Show(this, "Apagar todo o histórico de chamadas?", "Limpar histórico", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        App.Historico.Clear();
        App.SalvarHistorico();
        AtualizarRecentes();
    }

    private void NovoContato_Click(object sender, RoutedEventArgs e)
    {
        var c = ContatoWindow.Editar(this, new Contato());
        if (c == null) return;
        App.Contatos.Add(c);
        App.SalvarContatos();
        AtualizarContatos();
    }

    private void MenuEditarContato_Click(object sender, RoutedEventArgs e)
    {
        if (ItemSelecionado(sender) is not ContatoVM vm) return;
        if (ContatoWindow.Editar(this, vm.Contato) == null) return;
        App.SalvarContatos();
        AtualizarContatos();
        AtualizarRecentes();
    }

    private void MenuExcluirContato_Click(object sender, RoutedEventArgs e)
    {
        if (ItemSelecionado(sender) is not ContatoVM vm) return;
        if (MessageBox.Show(this, $"Excluir o contato {vm.Nome}?", "Excluir contato", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        App.Contatos.Remove(vm.Contato);
        App.SalvarContatos();
        AtualizarContatos();
    }

    // =================================================================
    // Não perturbe, configurações, janela
    // =================================================================

    private void NaoPerturbe_Click(object sender, RoutedEventArgs e) => DefinirNaoPerturbe(!Tel.NaoPerturbe);

    public void DefinirNaoPerturbe(bool ligado)
    {
        Tel.NaoPerturbe = ligado;
        App.Config.NaoPerturbe = ligado;
        App.SalvarConfig();
        AtualizarNaoPerturbe();
        AtualizarStatus(Tel.EstadoRegistro, Tel.MotivoRegistro);
        _ = App.Chat.AtualizarMinhaPresencaAsync();
    }

    private void AtualizarNaoPerturbe()
    {
        if (Tel.NaoPerturbe) BtnNaoPerturbe.SetResourceReference(BackgroundProperty, "HeaderButtonOn");
        else BtnNaoPerturbe.Background = Brushes.Transparent;
        BtnNaoPerturbe.ToolTip = Tel.NaoPerturbe ? "Não perturbe LIGADO: clique para voltar a receber chamadas" : "Não perturbe: recusa as chamadas recebidas";
    }

    private void Config_Click(object sender, RoutedEventArgs e) => AbrirConfiguracoes(primeiraVez: false);

    public void AbrirConfiguracoes(bool primeiraVez)
    {
        if (!IsVisible) App.MostrarJanela();
        var janela = new ConfigWindow(App.Config, primeiraVez) { Owner = this };
        if (janela.ShowDialog() != true) return;
        App.SalvarConfig();
        App.AplicarAudio();
        Armazenamento.AplicarInicioComWindows(App.Config.IniciarComWindows);
        if (janela.ContaMudou) _ = App.ConectarAsync();
        if (janela.HubMudou) _ = App.Chat.IniciarAsync();
        RegistrarAtalhos();
        AtualizarStatus(Tel.EstadoRegistro, Tel.MotivoRegistro);
        AtualizarGravarAuto();
    }

    // ---------- gravação automática (botão embaixo do teclado) ----------

    private void GravarAuto_Click(object sender, RoutedEventArgs e)
    {
        bool ligar = !App.Config.GravarAutomatico;
        if (ligar && !App.Gravacoes.Disponivel)
        {
            MessageBox.Show(this, "A gravação precisa do servidor do chat (servidor do Softphone). Configure em Configurações > Conta.", "Softphone",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        App.Config.GravarAutomatico = ligar;
        App.SalvarConfig();
        App.AplicarAudio();
        Log.Escrever("Gravação automática " + (ligar ? "ligada" : "desligada") + " pelo botão da tela principal");
        AtualizarGravarAuto();
    }

    public void AtualizarGravarAuto()
    {
        bool on = App.Config.GravarAutomatico;
        TxtGravarAuto.Text = on ? "Gravação automática: ligada" : "Gravação automática: desligada";
        if (on) { IconeGravarAuto.Foreground = new SolidColorBrush(Color.FromRgb(0xD1, 0x34, 0x38)); TxtGravarAuto.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary"); }
        else { IconeGravarAuto.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted"); TxtGravarAuto.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary"); }
        BtnGravarAuto.ToolTip = on
            ? "Toda ligação atendida começa a gravar sozinha, com o aviso. Clique para desligar."
            : "Clique para gravar sozinho toda ligação atendida (com o aviso de gravação para o outro lado).";
    }

    private void Janela_Closing(object? sender, CancelEventArgs e)
    {
        if (App.Saindo) return;
        // Fechar a janela só esconde: o telefone continua recebendo chamadas (sai pela bandeja).
        e.Cancel = true;
        SalvarPosicao();
        Hide();
        if (!App.Config.AvisoBandejaMostrado)
        {
            App.Notificar("O Softphone continua ligado", "Ele fica aqui na bandeja para receber chamadas. Para sair, clique com o botão direito no ícone > Sair.");
            App.Config.AvisoBandejaMostrado = true;
            App.SalvarConfig();
        }
    }

    private void PosicionarJanela()
    {
        var area = SystemParameters.WorkArea;
        double? x = App.Config.JanelaEsquerda, y = App.Config.JanelaTopo;
        if (x is double px && y is double py && px >= area.Left - 50 && px < area.Right - 100 && py >= area.Top - 10 && py < area.Bottom - 100)
        {
            Left = px; Top = py;
        }
        else
        {
            Left = area.Right - Width - 24;
            Top = Math.Max(area.Top + 24, area.Bottom - Height - 24);
        }
    }

    private DispatcherTimer? _salvarPosicao;
    private void SalvarPosicaoDepois()
    {
        _salvarPosicao ??= new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, (_, _) => { _salvarPosicao!.Stop(); SalvarPosicao(); App.SalvarConfig(); }, Dispatcher);
        _salvarPosicao.Stop();
        _salvarPosicao.Start();
    }

    public void SalvarPosicao()
    {
        if (WindowState != WindowState.Normal) return;
        App.Config.JanelaEsquerda = Left;
        App.Config.JanelaTopo = Top;
    }
}
