using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Softphone.Contratos;
using Microsoft.Win32;
using NAudio.Wave;

namespace Softphone.App;

/// <summary>Linha da lista de gravações.</summary>
public sealed class GravacaoVM : Observavel
{
    public GravacaoVM(GravacaoDto g) => Dto = g;
    public GravacaoDto Dto { get; private set; }
    public void Trocar(GravacaoDto g) { Dto = g; AvisarTudo(); }

    public long Id => Dto.Id;
    public string Titulo => Dto.Nome ?? Dto.Numero;
    public string Icone => Dto.Direcao == "entrada" ? "↙" : "↗";
    public string Linha2
    {
        get
        {
            var d = Dto.Inicio.ToLocalTime();
            string quando = d.Date == DateTime.Today ? "hoje " + d.ToString("HH:mm") : d.Date == DateTime.Today.AddDays(-1) ? "ontem " + d.ToString("HH:mm") : d.ToString("dd/MM HH:mm");
            return $"{quando} · {TimeSpan.FromSeconds(Dto.DuracaoSegundos):m\\:ss}" + (Dto.Nome != null ? " · " + Dto.Numero : "");
        }
    }
    public string Linha3 => Dto.Status switch
    {
        StatusGravacao.Fila => "na fila para transcrever...",
        StatusGravacao.Transcrevendo => "transcrevendo...",
        StatusGravacao.Resumindo => "a IA está resumindo...",
        StatusGravacao.SoAudio => "só áudio (transcrição desligada no servidor)",
        StatusGravacao.Guardada => "só áudio · clique para transcrever",
        StatusGravacao.Erro => "erro: " + Dto.Erro,
        _ => Dto.Assunto ?? (string.IsNullOrEmpty(Dto.Erro) ? "transcrição pronta" : "transcrição pronta (sem resumo)"),
    };
    public bool ComErro => Dto.Status == StatusGravacao.Erro;
}

/// <summary>Um trecho falado na transcrição.</summary>
public sealed class FalaVM
{
    public FalaVM(SegmentoDto s, string quemEu, string quemOutro)
    {
        Eu = s.Lado == "eu";
        Quem = Eu ? quemEu : quemOutro;
        Quando = TimeSpan.FromSeconds(s.Inicio).ToString(s.Inicio >= 3600 ? @"h\:mm\:ss" : @"m\:ss");
        Texto = s.Texto;
    }
    public bool Eu { get; }
    public string Quem { get; }
    public string Quando { get; }
    public string Texto { get; }
}

/// <summary>Ligações gravadas: lista + resumo, próximos passos, sugestão de retorno, transcrição e áudio. Só as do próprio usuário.</summary>
public partial class GravacoesWindow : Window
{
    private static GravacoesWindow? _aberta;
    private readonly ObservableCollection<GravacaoVM> _itens = new();
    private GravacaoDetalheDto? _atual;
    private long? _selecionarDepois;
    private WaveOutEvent? _player;
    private WaveFileReader? _leitor;
    private readonly string _pastaTemp = Path.Combine(Path.GetTempPath(), "Softphone-audio");

    public static void Abrir(long? gravacao)
    {
        if (_aberta == null) { _aberta = new GravacoesWindow(); _aberta.Show(); }
        if (_aberta.WindowState == WindowState.Minimized) _aberta.WindowState = WindowState.Normal;
        _aberta.Activate();
        if (gravacao is long id) _aberta.Selecionar(id);
    }

    private App App => App.Atual;

    // só para o modo de teste
    internal static void TesteSelecionar(int indice) { if (_aberta != null && indice < _aberta._itens.Count) _aberta.Lista.SelectedIndex = indice; }
    internal static void TesteReprocessar() => _aberta?.Reprocessar_Click(_aberta, new RoutedEventArgs());

    public GravacoesWindow()
    {
        InitializeComponent();
        Lista.ItemsSource = _itens;
        SourceInitialized += (_, _) => Theme.AplicarBarraTitulo(this);
        Loaded += async (_, _) => await CarregarAsync();
        App.Gravacoes.Mudou += AoMudar;
        Closed += (_, _) =>
        {
            App.Gravacoes.Mudou -= AoMudar;
            PararAudio();
            try { foreach (var f in Directory.EnumerateFiles(_pastaTemp)) File.Delete(f); } catch { }
            _aberta = null;
        };
        var dias = App.Gravacoes.Estado?.RetencaoDias ?? 90;
        TxtRetencao.Text = $"Só você vê as suas gravações. Elas são apagadas sozinhas depois de {dias} dias.";
    }

    private async Task CarregarAsync()
    {
        var cli = App.Chat.Cliente;
        if (cli is null || cli.Estado != Core.EstadoHub.Conectado)
        {
            TxtVazio.Text = "Sem conexão com o servidor do chat. As gravações aparecem aqui quando ele voltar.";
            TxtVazio.Visibility = Visibility.Visible;
            return;
        }
        try
        {
            var lista = await cli.ListarGravacoesAsync();
            var selecionada = (Lista.SelectedItem as GravacaoVM)?.Id ?? _selecionarDepois;
            _itens.Clear();
            foreach (var g in lista) _itens.Add(new GravacaoVM(g));
            TxtVazio.Text = "Nenhuma ligação gravada. Durante uma chamada, use o botão Gravar.";
            TxtVazio.Visibility = _itens.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (selecionada is long id) Selecionar(id);
        }
        catch (Exception ex)
        {
            TxtVazio.Text = "Não consegui carregar as gravações: " + ex.Message;
            TxtVazio.Visibility = Visibility.Visible;
        }
    }

    private void Selecionar(long id)
    {
        var vm = _itens.FirstOrDefault(x => x.Id == id);
        if (vm == null) { _selecionarDepois = id; return; }
        _selecionarDepois = null;
        Lista.SelectedItem = vm;
        Lista.ScrollIntoView(vm);
    }

    private void AoMudar(GravacaoDto g) => Dispatcher.BeginInvoke(async () =>
    {
        var vm = _itens.FirstOrDefault(x => x.Id == g.Id);
        if (vm == null) { _itens.Insert(0, new GravacaoVM(g)); TxtVazio.Visibility = Visibility.Collapsed; }
        else vm.Trocar(g);
        if (_atual?.Gravacao.Id == g.Id) await MostrarAsync(g.Id);
    });

    private async void Lista_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        PararAudio();
        if (Lista.SelectedItem is GravacaoVM vm) await MostrarAsync(vm.Id);
        else { Detalhe.Visibility = Visibility.Collapsed; TxtEscolha.Visibility = Visibility.Visible; _atual = null; }
    }

    private async Task MostrarAsync(long id)
    {
        var cli = App.Chat.Cliente;
        if (cli is null) return;
        GravacaoDetalheDto d;
        try { d = await cli.GravacaoAsync(id); }
        catch (Exception ex) { MessageBox.Show(this, "Não consegui abrir a gravação: " + ex.Message, "Softphone"); return; }
        if ((Lista.SelectedItem as GravacaoVM)?.Id != id) return; // trocou de item enquanto carregava
        _atual = d;
        var g = d.Gravacao;
        var eu = App.Chat.Cliente?.Eu?.Nome ?? "Eu";
        var outro = g.Nome ?? g.Numero;
        Detalhe.Visibility = Visibility.Visible;
        TxtEscolha.Visibility = Visibility.Collapsed;
        TxtTitulo.Text = (g.Direcao == "entrada" ? "Recebida de " : "Feita para ") + outro;
        TxtInfo.Text = $"{g.Inicio.ToLocalTime():dd/MM/yyyy HH:mm} · {TimeSpan.FromSeconds(g.DuracaoSegundos):m\\:ss} · {g.Numero} · apagada em {g.ExpiraEm.ToLocalTime():dd/MM/yyyy}";

        string? status = g.Status switch
        {
            StatusGravacao.Fila => "Na fila do servidor. A transcrição começa em instantes.",
            StatusGravacao.Transcrevendo => "Transcrevendo no servidor da empresa... (leva mais ou menos o tempo da ligação)",
            StatusGravacao.Resumindo => "Transcrição pronta. A IA está escrevendo o resumo...",
            StatusGravacao.SoAudio => "A transcrição não está ligada no servidor: por enquanto fica só o áudio.",
            StatusGravacao.Guardada => "Só o áudio foi guardado. Clique em Transcrever para ter a transcrição e o resumo desta ligação.",
            StatusGravacao.Erro => "Deu erro: " + g.Erro + " Use o botão ⟳ para tentar de novo.",
            _ => string.IsNullOrEmpty(g.Erro) ? null : g.Erro,
        };
        CaixaStatus.Visibility = status is null ? Visibility.Collapsed : Visibility.Visible;
        TxtStatus.Text = status ?? "";
        bool soGuardada = g.Status is StatusGravacao.Guardada or StatusGravacao.SoAudio;
        BtnTranscrever.Visibility = soGuardada ? Visibility.Visible : Visibility.Collapsed;
        BtnReprocessar.Visibility = g.Status is StatusGravacao.Erro or StatusGravacao.Pronta ? Visibility.Visible : Visibility.Collapsed;

        var r = d.Resumo;
        PainelResumo.Visibility = r is null ? Visibility.Collapsed : Visibility.Visible;
        if (r != null)
        {
            TxtAssunto.Text = r.Assunto;
            TxtQuem.Text = string.Join(" · ", new[] { r.QuemLigou is null ? null : "Quem é: " + r.QuemLigou, "Clima: " + r.Sentimento }.Where(x => x != null));
            TxtResumo.Text = r.Resumo;
            ListaPassos.ItemsSource = r.ProximosPassos;
            TituloPassos.Visibility = ListaPassos.Visibility = r.ProximosPassos.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            CaixaSugestao.Visibility = string.IsNullOrWhiteSpace(r.SugestaoResposta) ? Visibility.Collapsed : Visibility.Visible;
            TxtSugestao.Text = r.SugestaoResposta ?? "";
        }

        ListaFalas.ItemsSource = d.Segmentos.Select(s => new FalaVM(s, eu, outro)).ToList();
        bool pronta = g.Status == StatusGravacao.Pronta;
        TituloTranscricao.Visibility = pronta || d.Segmentos.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        TxtSemFala.Visibility = pronta && d.Segmentos.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------- áudio ----------

    private async Task<string?> BaixarAsync()
    {
        if (_atual is null || App.Chat.Cliente is null) return null;
        Directory.CreateDirectory(_pastaTemp);
        var arq = Path.Combine(_pastaTemp, $"gravacao-{_atual.Gravacao.Id}.wav");
        if (!File.Exists(arq)) await App.Chat.Cliente.BaixarAudioGravacaoAsync(_atual.Gravacao.Id, arq);
        return arq;
    }

    private async void Ouvir_Click(object sender, RoutedEventArgs e)
    {
        if (_player != null) { PararAudio(); return; }
        try
        {
            TxtOuvir.Text = "Baixando...";
            var arq = await BaixarAsync();
            if (arq is null) return;
            _leitor = new WaveFileReader(arq);
            var saida = Core.DispositivosAudio.Indice(Core.DispositivosAudio.AltoFalantes(), App.Config.AltoFalante);
            _player = new WaveOutEvent { DeviceNumber = saida };
            _player.Init(_leitor);
            _player.PlaybackStopped += (_, _) => Dispatcher.BeginInvoke(PararAudio);
            _player.Play();
            TxtOuvir.Text = "Parar"; IconeOuvir.Text = "";
        }
        catch (Exception ex)
        {
            PararAudio();
            MessageBox.Show(this, "Não consegui tocar o áudio: " + ex.Message, "Softphone");
        }
    }

    private void PararAudio()
    {
        try { _player?.Stop(); } catch { }
        _player?.Dispose(); _player = null;
        _leitor?.Dispose(); _leitor = null;
        TxtOuvir.Text = "Ouvir"; IconeOuvir.Text = "";
    }

    private async void SalvarAudio_Click(object sender, RoutedEventArgs e)
    {
        if (_atual is null) return;
        var g = _atual.Gravacao;
        var dlg = new SaveFileDialog
        {
            Filter = "Áudio WAV|*.wav",
            FileName = $"ligacao-{g.Inicio.ToLocalTime():yyyyMMdd-HHmm}-{new string(g.Numero.Where(char.IsDigit).ToArray())}.wav",
        };
        if (dlg.ShowDialog(this) != true) return;
        try { var arq = await BaixarAsync(); if (arq != null) File.Copy(arq, dlg.FileName, true); }
        catch (Exception ex) { MessageBox.Show(this, "Não consegui salvar: " + ex.Message, "Softphone"); }
    }

    // ---------- ações ----------

    private void CopiarSugestao_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(TxtSugestao.Text); } catch { }
    }

    private void CopiarTudo_Click(object sender, RoutedEventArgs e)
    {
        if (_atual is null) return;
        var g = _atual.Gravacao; var r = _atual.Resumo;
        var sb = new StringBuilder();
        sb.AppendLine($"Ligação {(g.Direcao == "entrada" ? "recebida de" : "feita para")} {g.Nome ?? g.Numero} ({g.Numero}) em {g.Inicio.ToLocalTime():dd/MM/yyyy HH:mm}, {TimeSpan.FromSeconds(g.DuracaoSegundos):m\\:ss}");
        if (r != null)
        {
            sb.AppendLine().AppendLine("Assunto: " + r.Assunto).AppendLine(r.Resumo);
            if (r.ProximosPassos.Length > 0) { sb.AppendLine().AppendLine("Próximos passos:"); foreach (var p in r.ProximosPassos) sb.AppendLine("- " + p); }
        }
        if (_atual.Segmentos.Length > 0)
        {
            sb.AppendLine().AppendLine("Transcrição:");
            foreach (FalaVM f in ListaFalas.ItemsSource) sb.AppendLine($"[{f.Quando}] {f.Quem}: {f.Texto}");
        }
        try { Clipboard.SetText(sb.ToString()); } catch { }
    }

    private async void Reprocessar_Click(object sender, RoutedEventArgs e)
    {
        if (_atual is null || App.Chat.Cliente is null) return;
        try { AoMudar(await App.Chat.Cliente.ReprocessarGravacaoAsync(_atual.Gravacao.Id)); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Softphone"); }
    }

    private async void Excluir_Click(object sender, RoutedEventArgs e)
    {
        if (_atual is null || App.Chat.Cliente is null) return;
        var g = _atual.Gravacao;
        if (MessageBox.Show(this, $"Excluir a gravação da ligação com {g.Nome ?? g.Numero}? O áudio, a transcrição e o resumo somem do servidor.",
                "Excluir gravação", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        try
        {
            PararAudio();
            await App.Chat.Cliente.ExcluirGravacaoAsync(g.Id);
            var vm = _itens.FirstOrDefault(x => x.Id == g.Id);
            if (vm != null) _itens.Remove(vm);
            try { File.Delete(Path.Combine(_pastaTemp, $"gravacao-{g.Id}.wav")); } catch { }
            TxtVazio.Visibility = _itens.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex) { MessageBox.Show(this, "Não consegui excluir: " + ex.Message, "Softphone"); }
    }

    private void Lista_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete) { Excluir_Click(sender, e); e.Handled = true; }
    }

    private async void Atualizar_Click(object sender, RoutedEventArgs e) => await CarregarAsync();
}
