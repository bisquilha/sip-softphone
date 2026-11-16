using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Softphone.Contratos;
using Softphone.Core;

namespace Softphone.App;

/// <summary>
/// Gravação de ligações no app: liga/desliga a gravação (com o aviso falado), guarda o WAV numa pasta de pendentes
/// e envia ao Hub assim que der (se o servidor estiver fora, tenta de novo ao reconectar). O Hub transcreve e resume;
/// o andamento volta pelo evento do Hub e vira notificação.
/// </summary>
public sealed class GravacoesServico
{
    private readonly App _app;
    private readonly short[]? _aviso;
    private readonly SemaphoreSlim _enviando = new(1, 1);

    public GravacoesServico(App app)
    {
        _app = app;
        // 10/11/2026 (pedido do Ana): o aviso FALADO da gravação não toca mais. Quem gravar vê o botão "gravando"; o aviso ao cliente
        // deve vir da URA do PABX ou de outro meio validado pelo jurídico (docs\AVISO-GRAVACAO-2026-11-10.md). Para voltar a tocar:
        // "AvisoFaladoGravacao": true no config.json do usuário.
        if (app.Config.AvisoFaladoGravacao)
        {
            try
            {
                var res = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/aviso-gravacao.wav"));
                if (res != null) using (var s = res.Stream) _aviso = AvisoGravacao.CarregarWav(s);
            }
            catch (Exception ex) { Log.Escrever("Aviso de gravação indisponível: " + ex.Message); }
        }
        Directory.CreateDirectory(PastaPendentes);
        app.Telefone.GravacaoConcluida += AoConcluir;
        // 1.1.3: gravação automática (a opção liga/desliga em AplicarAudio); grava como o botão Gravar, se o chat estiver configurado
        app.Telefone.PastaGravacaoAutomatica = PastaPendentes;
        app.Telefone.AvisoGravacaoAutomatica = _aviso;
        app.Telefone.PodeGravarAutomaticamente = () => Disponivel;
    }

    /// <summary>WAVs que ainda não chegaram ao servidor (cada um com um .json do lado).</summary>
    public string PastaPendentes => Path.Combine(Armazenamento.Pasta, "gravacoes");

    public EstadoIaDto? Estado { get; private set; }
    /// <summary>Precisa do servidor do chat (é ele que guarda, transcreve e resume).</summary>
    public bool Disponivel => _app.Chat.Ativo;

    /// <summary>Uma gravação minha mudou de estado (a tela de gravações atualiza).</summary>
    public event Action<GravacaoDto>? Mudou;

    public bool Alternar(Chamada c)
    {
        if (c.Gravando) { _app.Telefone.PararGravacao(c); return false; }
        if (!Disponivel)
        {
            MessageBox.Show("A gravação precisa do servidor do chat (servidor do Softphone). Configure em Configurações > Conta.", "Softphone",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }
        return _app.Telefone.IniciarGravacao(c, PastaPendentes, _aviso);
    }

    private sealed record Metadados(string Numero, string? Nome, bool Entrada, DateTime Inicio, double Segundos);

    private void AoConcluir(Chamada c, string wav, DateTime inicio, TimeSpan duracao)
    {
        try
        {
            var nome = _app.NomeDoContato(c.Numero) ?? c.Nome;
            File.WriteAllText(Path.ChangeExtension(wav, ".json"),
                JsonSerializer.Serialize(new Metadados(c.Numero, nome, c.Direcao == DirecaoChamada.Entrada, inicio, duracao.TotalSeconds)));
        }
        catch (Exception ex) { Log.Escrever("Gravação: falha ao salvar dados: " + ex.Message); }
        _app.Dispatcher.BeginInvoke(() => _ = EnviarPendentesAsync());
    }

    /// <summary>Manda ao Hub tudo o que está pendente (chamado ao terminar uma gravação e ao reconectar).</summary>
    public async Task EnviarPendentesAsync()
    {
        var cli = _app.Chat.Cliente;
        if (cli is null || cli.Estado != EstadoHub.Conectado) return;
        if (!await _enviando.WaitAsync(0)) return;
        try
        {
            foreach (var wav in Directory.EnumerateFiles(PastaPendentes, "*.wav").OrderBy(f => f).ToList())
            {
                var meta = Path.ChangeExtension(wav, ".json");
                if (!File.Exists(meta)) continue; // ainda gravando, ou sobra de gravação que falhou
                Metadados? m = null;
                try
                {
                    m = JsonSerializer.Deserialize<Metadados>(await File.ReadAllTextAsync(meta))!;
                    long maxBytes = (Estado?.GravacaoMaxMb ?? 400) * 1024L * 1024;
                    if (new FileInfo(wav).Length > maxBytes)
                        throw new ErroHubException(System.Net.HttpStatusCode.RequestEntityTooLarge, $"Gravação maior que {maxBytes / 1024 / 1024} MB.");
                    bool transcrever = _app.Config.TranscreverAutomatico;
                    var g = await cli.EnviarGravacaoAsync(wav, m.Numero, m.Nome, m.Entrada, m.Inicio, TimeSpan.FromSeconds(m.Segundos), transcrever: transcrever);
                    File.Delete(wav); File.Delete(meta);
                    Log.Escrever($"Gravação enviada ao servidor: {m.Numero} ({g.DuracaoSegundos} s), id {g.Id}, {(transcrever ? "com" : "sem")} transcrição");
                    Mudou?.Invoke(g);
                    if (g.Status == StatusGravacao.Guardada)
                        _app.NotificarGravacao("Gravação guardada", $"A ligação com {m.Nome ?? m.Numero} foi guardada. Para transcrever, abra Gravações e clique em Transcrever.", g.Id);
                    else if (Estado is { Transcricao: true })
                        _app.Notificar("Gravação enviada", $"A ligação com {m.Nome ?? m.Numero} está sendo transcrita. Aviso quando o resumo ficar pronto.");
                }
                catch (ErroHubException ex) when (ex.Definitivo)
                {
                    // R06: o servidor recusou ESTE arquivo (grande demais, inválido). Tentar de novo não adianta e ele não
                    // pode travar os que vêm depois: vai para "recusadas" (o arquivo é mantido) e a pessoa é avisada.
                    var pasta = Path.Combine(PastaPendentes, "recusadas");
                    try
                    {
                        Directory.CreateDirectory(pasta);
                        File.Move(wav, Path.Combine(pasta, Path.GetFileName(wav)), true);
                        File.Move(meta, Path.Combine(pasta, Path.GetFileName(meta)), true);
                    }
                    catch (Exception mv) { Log.Escrever("Gravação recusada: não deu para mover: " + mv.Message); break; }
                    Log.Escrever($"Gravação {Path.GetFileName(wav)} recusada pelo servidor ({ex.Message}); guardada em {pasta}");
                    _app.Notificar("Gravação não enviada", $"A gravação com {m?.Nome ?? m?.Numero ?? "o contato"} foi recusada pelo servidor: {ex.Message} O arquivo ficou guardado no seu PC; avise a TI.");
                }
                catch (Exception ex)
                {
                    Log.Escrever($"Gravação {Path.GetFileName(wav)} não foi enviada (tento de novo depois): {ex.Message}");
                    break;
                }
            }
        }
        finally { _enviando.Release(); }
    }

    public async Task AoConectarAsync()
    {
        try { Estado = await _app.Chat.Cliente!.EstadoIaAsync(); AplicarLimite(); }
        catch (Exception ex) { Log.Escrever("IA: estado indisponível: " + ex.Message); Estado = null; }
        await EnviarPendentesAsync();
    }

    /// <summary>A gravação para de crescer antes de passar do limite que o Hub aceita (R06).</summary>
    private void AplicarLimite()
    {
        if (Estado is { GravacaoMaxMb: > 0 } e) _app.Telefone.LimiteGravacaoBytes = e.GravacaoMaxMb * 1024L * 1024;
    }

    /// <summary>A TI ligou/desligou a IA pelo painel: o botão "Sugerir resposta" e o contexto passam a valer na hora.</summary>
    public void AoEstadoIa(EstadoIaDto e)
    {
        Estado = e;
        AplicarLimite();
        Log.Escrever($"IA mudou no servidor: transcrição {e.Transcricao}, resumo {e.Resumo}, retenção {e.RetencaoDias} dias");
    }

    public void AoMudar(GravacaoDto g)
    {
        Mudou?.Invoke(g);
        var quem = g.Nome ?? g.Numero;
        if (g.Status == StatusGravacao.Pronta)
            _app.NotificarGravacao(string.IsNullOrEmpty(g.Erro) ? "Resumo da ligação pronto" : "Transcrição pronta",
                $"{quem}: {g.Assunto ?? "clique para ver"}", g.Id);
        else if (g.Status == StatusGravacao.Erro)
            _app.NotificarGravacao("Gravação com problema", $"{quem}: {g.Erro}", g.Id);
    }

    /// <summary>Uma linha para o cartão da chamada: o que já se falou com esse número (só nas MINHAS gravações).</summary>
    public async Task<string?> ContextoAsync(string numero)
    {
        var cli = _app.Chat.Cliente;
        if (cli is null || cli.Estado != EstadoHub.Conectado || Estado is not { Resumo: true }) return null;
        var digitos = new string(numero.Where(char.IsDigit).ToArray());
        if (digitos.Length <= 5) return null; // ramal interno: o diretório já diz quem é
        try
        {
            var ctx = await cli.ContextoAsync(numero);
            var ult = ctx.Anteriores.FirstOrDefault();
            if (ult is null && ctx.QuemE is null) return null;
            var partes = new List<string>();
            if (ctx.QuemE != null) partes.Add(ctx.QuemE);
            if (ult != null) partes.Add($"última: {ult.Assunto ?? "ligação"} ({ult.Quando.ToLocalTime():dd/MM})");
            return string.Join(" · ", partes);
        }
        catch { return null; }
    }
}
