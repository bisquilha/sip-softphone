// PABX simulado (127.0.0.1:5090) + roteiro de testes do motor do Softphone.
// Não usa placa de som (AudioTeste gera um tom de 440 Hz). Rodar: dotnet run --project src\Softphone.PabxTeste
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Softphone.Core;
using SIPSorcery.Media;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;
using SIPSorceryMedia.Abstractions;

const string Realm = "pabx-teste";
const string Ramal = "1001";
const string Senha = "s3nha-teste";

// Testes do Hub de chat/presença: --hub <url> [--windows]  (o Hub precisa estar rodando com Hub:Teste=true)
var iBot = Array.IndexOf(args, "--hub-bot");
if (iBot >= 0) return await TesteHub.BotAsync(args[iBot + 1], args[iBot + 2], int.Parse(args.ElementAtOrDefault(iBot + 3) ?? "90"));
if (args.Contains("--ia-falsa"))
{
    // só o whisper/Anthropic de mentira (para testar a tela de gravações do app com um Hub de teste)
    ServidorFalsoIa.Iniciar();
    Console.WriteLine("IA falsa em " + ServidorFalsoIa.Url);
    await Task.Delay(Timeout.Infinite);
}
var iHub = Array.IndexOf(args, "--hub");
if (iHub >= 0) return await TesteHub.RodarAsync(args.ElementAtOrDefault(iHub + 1) ?? "http://localhost:5280", args.Contains("--windows"));

if (args.Contains("--voz"))
{
    // só o tratamento de voz (eco e ruído), sem PABX
    int f0 = 0;
    TesteVoz.Rodar((c, n) => { Console.ForegroundColor = c ? ConsoleColor.Green : ConsoleColor.Red; Console.WriteLine($"  [{(c ? "OK" : "FALHOU")}] {n}"); Console.ResetColor(); if (!c) f0++; });
    return f0 == 0 ? 0 : 1;
}

PabxSimulado.Silencioso = args.Contains("--silencioso");
var pabx = new PabxSimulado(Realm, Ramal, Senha);
pabx.Iniciar(5090);

if (args.Contains("--microfone"))
{
    // Diagnóstico: captura 3 s de cada microfone pelo mesmo caminho do app (WinMM 8 kHz) e mostra quadros/pico. Nada é gravado.
    var mics = DispositivosAudio.Microfones();
    Console.WriteLine("  .NET8 struct proprio: " + MMTeste.Abrir(false) + " | NAudio WaveFormat: " + MMTeste.Abrir(true) + " | Marshal.SizeOf(WaveFormat)=" + System.Runtime.InteropServices.Marshal.SizeOf(typeof(NAudio.Wave.WaveFormat)));
    foreach (var (taxa, canais) in new[] { (16000, 1), (44100, 1), (48000, 1), (48000, 2), (44100, 2) })
    {
        string? e2 = null; int q2 = 0;
        try
        {
            using var m = new NAudio.Wave.WaveInEvent { DeviceNumber = 0, WaveFormat = new NAudio.Wave.WaveFormat(taxa, 16, canais), BufferMilliseconds = 20 };
            m.DataAvailable += (_, e) => q2++;
            m.StartRecording(); await Task.Delay(600); m.StopRecording();
        }
        catch (Exception ex) { e2 = ex.Message; }
        Console.WriteLine($"  WinMM disp 0 {taxa} Hz {canais} canal(is): {q2} quadros {e2}");
    }
    try
    {
        var en = new NAudio.CoreAudioApi.MMDeviceEnumerator();
        foreach (var d in en.EnumerateAudioEndPoints(NAudio.CoreAudioApi.DataFlow.Capture, NAudio.CoreAudioApi.DeviceState.Active))
        {
            int q3 = 0, p3 = 0; string? e3 = null;
            try
            {
                using var w = new NAudio.CoreAudioApi.WasapiCapture(d, false, 20);
                var fmt = w.WaveFormat;
                w.DataAvailable += (_, e) => { q3++; if (fmt.Encoding == NAudio.Wave.WaveFormatEncoding.IeeeFloat || fmt.BitsPerSample == 32) for (int k = 0; k + 3 < e.BytesRecorded; k += 4) p3 = Math.Max(p3, (int)(Math.Abs(BitConverter.ToSingle(e.Buffer, k)) * 32767)); };
                w.StartRecording(); await Task.Delay(2000); w.StopRecording();
                Console.WriteLine($"  WASAPI {d.FriendlyName} [{fmt}]: {q3} quadros, pico {p3}");
            }
            catch (Exception ex) { e3 = ex.Message; Console.WriteLine($"  WASAPI {d.FriendlyName}: ERRO {e3}"); }
        }
    }
    catch (Exception ex) { Console.WriteLine("  WASAPI indisponível: " + ex.Message); }
    for (int i = -1; i < mics.Count; i++)
    {
        int quadros = 0, pico = 0; string? erro = null;
        try
        {
            using var mic = new NAudio.Wave.WaveInEvent { DeviceNumber = i, WaveFormat = new NAudio.Wave.WaveFormat(8000, 16, 1), BufferMilliseconds = 20, NumberOfBuffers = 4 };
            mic.DataAvailable += (_, e) => { quadros++; for (int k = 0; k + 1 < e.BytesRecorded; k += 2) pico = Math.Max(pico, Math.Abs((int)BitConverter.ToInt16(e.Buffer, k))); };
            mic.RecordingStopped += (_, e) => { if (e.Exception != null) erro = e.Exception.Message; };
            mic.StartRecording();
            await Task.Delay(3000);
            mic.StopRecording();
        }
        catch (Exception ex) { erro = ex.Message; }
        Console.WriteLine($"[{i}] {(i < 0 ? "(padrão do Windows)" : mics[i])}: {quadros} quadros, pico {pico}{(erro != null ? " ERRO: " + erro : "")}");
    }
    return 0;
}

if (args.Contains("--servidor"))
{
    // Modo servidor: só o PABX, para testar o app de verdade. Ramais: 600 atende (tom), 601 ocupado, 602 só toca.
    // Para o PABX ligar para o softphone: crie o arquivo %TEMP%\pabx-ligar.txt com "numero;nome".
    Console.WriteLine($"PABX simulado em 127.0.0.1:5090 (ramal {Ramal}). Ctrl+C para sair.");
    var gatilho = Path.Combine(Path.GetTempPath(), "pabx-ligar.txt");
    while (true)
    {
        await Task.Delay(300);
        if (!File.Exists(gatilho) || pabx.ContatoRegistrado == null) continue;
        var partes = File.ReadAllText(gatilho).Trim().Split(';');
        File.Delete(gatilho);
        Console.WriteLine($"Ligando para o softphone como {partes[0]}...");
        _ = Task.Run(async () => Console.WriteLine("Resultado: " + await pabx.LigarParaSoftphoneAsync(partes[0], partes.ElementAtOrDefault(1) ?? "")));
    }
}

int falhas = 0;
void Ok(bool cond, string nome)
{
    Console.ForegroundColor = cond ? ConsoleColor.Green : ConsoleColor.Red;
    Console.WriteLine($"  [{(cond ? "OK" : "FALHOU")}] {nome}");
    Console.ResetColor();
    if (!cond) falhas++;
}
async Task<bool> Esperar(Func<bool> cond, int ms = 5000)
{
    var fim = DateTime.Now.AddMilliseconds(ms);
    while (DateTime.Now < fim) { if (cond()) return true; await Task.Delay(50); }
    return cond();
}

var conta = new ContaSip { Servidor = "127.0.0.1:5090", Dominio = "127.0.0.1", Usuario = Ramal, Senha = "errada", NomeExibicao = "Teste Softphone", RegistroSegundos = 120, ManterAtivoSegundos = 2 };
var toques = new Toques { Silencioso = true };
await using var tel = new Telefone(new FabricaAudioTeste { Frequencia = 2000 }, toques); // o softphone "fala" 2 kHz
var log = new ConcurrentQueue<string>();
tel.Log += m => { log.Enqueue(m); if (args.Contains("--ao-vivo")) Console.WriteLine("    · " + m); };
var encerradas = new ConcurrentQueue<Chamada>();
tel.ChamadaEncerrada += c => encerradas.Enqueue(c);
Chamada? recebida = null;
tel.ChamadaNova += c => { if (c.Direcao == DirecaoChamada.Entrada) recebida = c; };

Console.WriteLine("1) Registro com senha errada");
await tel.IniciarAsync(conta);
Ok(await Esperar(() => tel.EstadoRegistro == EstadoRegistro.Falha), $"falha de registro detectada ({tel.MotivoRegistro})");

Console.WriteLine("2) Registro com senha certa");
conta.Senha = Senha;
await tel.IniciarAsync(conta);
Ok(await Esperar(() => tel.EstadoRegistro == EstadoRegistro.Registrado), "registrado no PABX");
Ok(pabx.ContatoRegistrado != null, $"PABX guardou o contato ({pabx.ContatoRegistrado})");

Console.WriteLine("3) Keep-alive e OPTIONS (qualify do PABX)");
Ok(await Esperar(() => pabx.KeepAlives > 0, 4000), $"keep-alive CRLF recebido pelo PABX ({pabx.KeepAlives})");
var opt = await pabx.EnviarOptionsAsync();
Ok(opt == 200, $"softphone respondeu OPTIONS com 200 (recebeu {opt})");

Console.WriteLine("4) Chamada de saída para o 600 (atende) com autenticação 407");
var c1 = await tel.LigarAsync("600");
Ok(await Esperar(() => c1.Estado == EstadoChamada.EmAndamento), $"chamada atendida (estado {c1.Estado})");
Ok(pabx.InvitesAutenticados > 0, "INVITE reenviado com credenciais após 407");
await Task.Delay(1500);
Ok(c1.PacotesRecebidos > 30, $"áudio PABX -> softphone ({c1.PacotesRecebidos} pacotes)");
Ok(pabx.UltimaChamada?.PacotesRecebidos > 30, $"áudio softphone -> PABX ({pabx.UltimaChamada?.PacotesRecebidos} pacotes)");
Ok(pabx.CodecNegociado == "PCMA", $"codec negociado G.711 A-law ({pabx.CodecNegociado})");
int qualidadesRecebidas = 0;
tel.QualidadeMudou += c => { if (c == c1) Interlocked.Increment(ref qualidadesRecebidas); };
Ok(await Esperar(() => c1.Qualidade.Nivel == NivelQualidade.Boa && qualidadesRecebidas > 0, 4000),
    $"qualidade medida na chamada: {c1.Qualidade.Texto} ({c1.Qualidade.Recebidos} pacotes, {c1.Qualidade.Perdidos} perdidos)");

Console.WriteLine("5) DTMF");
await tel.EnviarDtmfAsync(c1, '5');
await tel.EnviarDtmfAsync(c1, '#');
Ok(await Esperar(() => pabx.Dtmf.Contains("5#")), $"PABX recebeu DTMF \"{pabx.Dtmf}\"");

Console.WriteLine("6) Espera e retomada");
await tel.AlternarEsperaAsync(c1);
Ok(await Esperar(() => pabx.EmEspera), "PABX viu a chamada em espera (re-INVITE sendonly)");
Ok(c1.Estado == EstadoChamada.EmEspera, "estado EmEspera");
await tel.AlternarEsperaAsync(c1);
Ok(await Esperar(() => !pabx.EmEspera), "PABX viu a retomada");
Ok(c1.Estado == EstadoChamada.EmAndamento, "estado EmAndamento");

Console.WriteLine("7) Mudo");
tel.AlternarMudo(c1);
Ok(c1.Mudo, "mudo ligado");
tel.AlternarMudo(c1);
Ok(!c1.Mudo, "mudo desligado");

Console.WriteLine("7b) Gravação com aviso (fecha sozinha ao desligar)");
string? wavGravado = null; TimeSpan duracaoGravada = default;
tel.GravacaoConcluida += (_, wav, _, dur) => { wavGravado = wav; duracaoGravada = dur; };
var pastaGrav = Path.Combine(Path.GetTempPath(), "softphone-teste-grav");
var aviso = new short[AvisoGravacao.Taxa / 2];                 // 0,5 s de 1 kHz como "aviso"
for (int i = 0; i < aviso.Length; i++) aviso[i] = (short)(Math.Sin(2 * Math.PI * 1000 * i / AvisoGravacao.Taxa) * 6000);
Ok(tel.IniciarGravacao(c1, pastaGrav, aviso), "gravação iniciada");
Ok(c1.Gravando && !tel.IniciarGravacao(c1, pastaGrav, aviso), "não inicia duas vezes");
await Task.Delay(2000);

Console.WriteLine("8) Desligar");
tel.Desligar(c1);
Ok(await Esperar(() => pabx.Desligadas > 0), "PABX recebeu BYE");
Ok(c1.Estado == EstadoChamada.Encerrada && c1.Resultado == ResultadoChamada.Atendida, $"resultado {c1.Resultado}");
// R05 (revisão do Codex): sair/atualizar espera a gravação terminar de salvar (WAV + dados), em vez de cortar no meio
await tel.AguardarGravacoesAsync(TimeSpan.FromSeconds(10));
Ok(tel.GravacoesSendoSalvas == 0 && wavGravado != null, $"esperar as gravações devolve só depois de salvas ({tel.GravacoesSendoSalvas} salvando, wav={wavGravado != null})");
Ok(await Esperar(() => wavGravado != null), $"gravação concluída ao desligar ({wavGravado})");
if (wavGravado != null)
{
    var (canais, taxa, seg, rmsEu, rmsOutro, rmsEuIni) = AnalisarWav(wavGravado);
    Ok(canais == 2 && taxa == 8000, $"WAV estéreo 8 kHz (codec G.711) ({canais} canais, {taxa} Hz)");
    Ok(seg is > 1.6 and < 3.0, $"duração ~2 s ({seg:N2} s; relógio {duracaoGravada.TotalSeconds:N2} s)");
    Ok(rmsEu > 1000 && rmsOutro > 1000, $"os dois lados têm áudio (eu {rmsEu:N0}, outro {rmsOutro:N0})");
    Ok(rmsEuIni > rmsEu, $"aviso misturado no meu lado no começo ({rmsEuIni:N0} > {rmsEu:N0})");
    Ok(!Directory.EnumerateFiles(pastaGrav, "*.raw").Any(), "temporários .raw apagados");
    File.Delete(wavGravado);
}

static (int canais, int taxa, double seg, double rmsEu, double rmsOutro, double rmsEuIni) AnalisarWav(string caminho)
{
    var b = File.ReadAllBytes(caminho);
    int canais = BitConverter.ToInt16(b, 22), taxa = BitConverter.ToInt32(b, 24);
    int n = (b.Length - 44) / 4;
    double se = 0, so = 0, si = 0; int ni = 0;
    // "começo" = trecho do aviso (0,1 s a 0,4 s); resto = depois de 1 s
    int ini0 = taxa / 10, ini1 = taxa * 4 / 10, resto0 = taxa;
    int nr = 0;
    for (int i = 0; i < n; i++)
    {
        double e = BitConverter.ToInt16(b, 44 + i * 4), o = BitConverter.ToInt16(b, 46 + i * 4);
        if (i >= resto0) { se += e * e; so += o * o; nr++; }
        if (i >= ini0 && i < ini1) { si += e * e; ni++; }
    }
    return (canais, taxa, (double)n / Math.Max(1, taxa), Math.Sqrt(se / Math.Max(1, nr)), Math.Sqrt(so / Math.Max(1, nr)), Math.Sqrt(si / Math.Max(1, ni)));
}

Console.WriteLine("9) Ocupado (601) e inexistente (404)");
var c2 = await tel.LigarAsync("601");
Ok(await Esperar(() => c2.Estado == EstadoChamada.Encerrada), "chamada encerrada");
Ok(c2.Resultado == ResultadoChamada.Ocupado, $"resultado Ocupado ({c2.Resultado} - {c2.Motivo})");
var c3 = await tel.LigarAsync("999");
Ok(await Esperar(() => c3.Estado == EstadoChamada.Encerrada), "chamada encerrada");
Ok(c3.Motivo == "Número inexistente", $"motivo \"{c3.Motivo}\"");

Console.WriteLine("10) Cancelar enquanto chama (602 só toca)");
var c4 = await tel.LigarAsync("602");
Ok(await Esperar(() => c4.Estado == EstadoChamada.Chamando), "destino tocando (180)");
tel.Desligar(c4);
Ok(await Esperar(() => pabx.Cancelamentos > 0), "PABX recebeu CANCEL");
Ok(c4.Resultado == ResultadoChamada.Cancelada, $"resultado {c4.Resultado}");

Console.WriteLine("11) Chamada recebida e perdida (PABX cancela)");
recebida = null;
var tarefa = pabx.LigarParaSoftphoneAsync("4000", "Recepção");
Ok(await Esperar(() => recebida?.Estado == EstadoChamada.Recebendo), "softphone tocando");
Ok(recebida?.Numero == "4000" && recebida?.Nome == "Recepção", $"identificação: {recebida?.Nome} / {recebida?.Numero}");
pabx.CancelarChamadaSaindo();
Ok(await Esperar(() => recebida?.Estado == EstadoChamada.Encerrada), "softphone parou de tocar");
Ok(recebida?.Resultado == ResultadoChamada.Perdida, $"resultado {recebida?.Resultado}");

Console.WriteLine("12) Chamada recebida, atendida e transferida (cega) para o 700");
recebida = null;
tarefa = pabx.LigarParaSoftphoneAsync("4001", "Cliente");
Ok(await Esperar(() => recebida?.Estado == EstadoChamada.Recebendo), "softphone tocando");
await tel.AtenderAsync(recebida!);
Ok(await Esperar(() => recebida!.Estado == EstadoChamada.EmAndamento), "atendida");
await Task.Delay(1000);
Ok(recebida!.PacotesRecebidos > 20, $"áudio na chamada recebida ({recebida.PacotesRecebidos} pacotes)");
// 1.1.3: transferir para o próprio número de quem ligou é recusado antes de ir ao PABX (era o caso do log real: 0379 -> 0379)
pabx.UltimoReferTo = null;
var proprio = await tel.TransferirAsync(recebida, "4001");
Ok(!proprio.Ok && pabx.UltimoReferTo == null && recebida.Estado == EstadoChamada.EmAndamento, $"transferir para o próprio número é recusado sem ir ao PABX ({proprio.Motivo})");
// 1.1.3: o PABX aceita (202) e depois avisa que o destino está ocupado: a ligação continua comigo (antes caía)
pabx.FalharTransferencia = "486 Busy Here";
var ocupado = await tel.TransferirAsync(recebida, "701");
pabx.FalharTransferencia = null;
Ok(!ocupado.Ok && recebida.Estado == EstadoChamada.EmAndamento && recebida.Resultado != ResultadoChamada.Transferida,
    $"destino ocupado: a ligação continua comigo ({recebida.Estado}; {ocupado.Motivo})");
var transferiu = await tel.TransferirAsync(recebida, "700");
Ok(pabx.UltimoReferTo?.Contains("700@") == true, $"PABX recebeu REFER para {pabx.UltimoReferTo}");
Ok(transferiu.Ok && recebida.Resultado == ResultadoChamada.Transferida, $"transferência concluída (ok={transferiu.Ok}, {recebida.Resultado})");

Console.WriteLine("13) Chamada em espera (segunda chamada durante a primeira) + transferência assistida");
var a = await tel.LigarAsync("600");
Ok(await Esperar(() => a.Estado == EstadoChamada.EmAndamento), "chamada A em andamento");
recebida = null;
tarefa = pabx.LigarParaSoftphoneAsync("4002", "Segunda");
Ok(await Esperar(() => recebida?.Estado == EstadoChamada.Recebendo), "segunda chamada tocando durante a primeira");
await tel.AtenderAsync(recebida!);
Ok(await Esperar(() => recebida!.Estado == EstadoChamada.EmAndamento), "segunda atendida");
Ok(await Esperar(() => a.Estado == EstadoChamada.EmEspera), "primeira foi para espera automaticamente");
tel.Desligar(recebida!);
await Task.Delay(300);
await tel.AlternarEsperaAsync(a);
Ok(await Esperar(() => a.Estado == EstadoChamada.EmAndamento), "primeira retomada");
var consulta = await tel.IniciarConsultaAsync(a, "600");
Ok(await Esperar(() => consulta.Estado == EstadoChamada.EmAndamento), "consulta atendida");
Ok(a.Estado == EstadoChamada.EmEspera, "original em espera durante a consulta");
var assistida = await tel.ConcluirConsultaAsync(consulta);
Ok(pabx.UltimoReferTo?.Contains("Replaces", StringComparison.OrdinalIgnoreCase) == true, $"REFER com Replaces ({pabx.UltimoReferTo})");
Ok(assistida.Ok && a.Estado == EstadoChamada.Encerrada && consulta.Estado == EstadoChamada.Encerrada, $"assistida concluída (ok={assistida.Ok})");

Console.WriteLine("13b) Conferência a três (600 fala 440 Hz, 603 fala 1300 Hz, o softphone fala 2 kHz)");
var pa = await tel.LigarAsync("600");
Ok(await Esperar(() => pa.Estado == EstadoChamada.EmAndamento), "perna A (600) em andamento");
var pb = await tel.LigarAsync("603");
Ok(await Esperar(() => pb.Estado == EstadoChamada.EmAndamento) && pa.Estado == EstadoChamada.EmEspera, "perna B (603) em andamento, A em espera");
Ok(await tel.ConferenciaAsync(pb, pa), "conferência iniciada");
Ok(pa.Estado == EstadoChamada.EmAndamento && pa.EmConferencia && pb.EmConferencia, "as duas pernas fora da espera e ligadas");
await Task.Delay(1500);
var r600 = pabx.Atendidas["600"]; var r603 = pabx.Atendidas["603"];
Ok(r600.NivelRecebido(1300) > 1500 && r600.NivelRecebido(2000) > 1500, $"600 ouve o 603 ({r600.NivelRecebido(1300):N0}) e o softphone ({r600.NivelRecebido(2000):N0})");
Ok(r603.NivelRecebido(440) > 1500 && r603.NivelRecebido(2000) > 1500, $"603 ouve o 600 ({r603.NivelRecebido(440):N0}) e o softphone ({r603.NivelRecebido(2000):N0})");
Ok(((AudioTeste)pb.Audio!).AmostrasExtras > 4000, $"o softphone toca as duas pernas ({((AudioTeste)pb.Audio!).AmostrasExtras} amostras da perna A)");
tel.AlternarMudo(pa);
await Task.Delay(1000);
Ok(pa.Mudo && pb.Mudo && r600.NivelRecebido(2000) < 300 && r603.NivelRecebido(2000) < 300 && r603.NivelRecebido(440) > 1500,
    $"mudo vale para as duas pernas e eles continuam se ouvindo (2 kHz no 600: {r600.NivelRecebido(2000):N0})");
tel.AlternarMudo(pa);
tel.Desligar(pb);
Ok(await Esperar(() => pb.Estado == EstadoChamada.Encerrada && !pa.EmConferencia), "principal desligou: conferência desfeita");
await Task.Delay(1200);
Ok(pa.Estado == EstadoChamada.EmAndamento && r600.NivelRecebido(2000) > 1500 && r600.NivelRecebido(1300) < 300,
    $"a perna que ficou volta ao próprio áudio (2 kHz {r600.NivelRecebido(2000):N0}, 1300 Hz {r600.NivelRecebido(1300):N0})");
tel.Desligar(pa);
Ok(await Esperar(() => pa.Estado == EstadoChamada.Encerrada), "perna A encerrada");

Console.WriteLine("13c) Medidor de qualidade (perda, variação, ida e volta, sem áudio)");
TesteQualidade.Rodar(Ok);

Console.WriteLine("13d) Aviso de gravação na conferência (1.1.2)");
TesteAvisoConferencia.Rodar(Ok);

// R06 (revisão do Codex): em G.722 (16 kHz estéreo) 3 h dariam ~659 MB e o Hub recusa acima de 400 MB
// 1.1.3: gravar todas as ligações sozinho (opção da pessoa); antes era preciso clicar em Gravar em cada ligação
Console.WriteLine("13f) Gravação automática (1.1.3)");
{
    var pastaAuto = Path.Combine(Path.GetTempPath(), "softphone-teste-grav-auto");
    tel.GravarAutomaticamente = true;
    tel.PastaGravacaoAutomatica = pastaAuto;
    tel.AvisoGravacaoAutomatica = aviso;
    string? wavAuto = null;
    void AoGravar(Chamada ch, string w, DateTime i, TimeSpan d) { if (ch.Numero == "600") wavAuto = w; }
    tel.GravacaoConcluida += AoGravar;
    var auto = await tel.LigarAsync("600");
    Ok(await Esperar(() => auto.Estado == EstadoChamada.EmAndamento) && await Esperar(() => auto.Gravando, 2000), $"ligação atendida já começa gravando ({auto.Estado}, gravando={auto.Gravando})");
    await Task.Delay(800);
    tel.Desligar(auto);
    await tel.AguardarGravacoesAsync(TimeSpan.FromSeconds(10));
    Ok(wavAuto != null && File.Exists(wavAuto), $"gravação automática salva ao desligar ({wavAuto})");
    tel.GravacaoConcluida -= AoGravar;
    tel.GravarAutomaticamente = false;
    var semAuto = await tel.LigarAsync("600");
    Ok(await Esperar(() => semAuto.Estado == EstadoChamada.EmAndamento) && !semAuto.Gravando, "com a opção desligada, não grava sozinho");
    tel.Desligar(semAuto);
    await Esperar(() => semAuto.Estado == EstadoChamada.Encerrada);
}

Console.WriteLine("13e) Tamanho máximo da gravação (1.1.2)");
{
    var pastaLim = Path.Combine(Path.GetTempPath(), "softphone-teste-limite");
    const long limite = 400L * 1024 * 1024;
    using var g16 = new GravadorChamada(pastaLim, 16000, limite);
    using var g8 = new GravadorChamada(pastaLim, 8000, limite);
    long bytes16 = 44 + (long)(g16.DuracaoMaxima.TotalSeconds * 16000 * 4);
    Ok(bytes16 <= limite && g16.DuracaoMaxima > TimeSpan.FromHours(1.7), $"G.722: para em {g16.DuracaoMaxima:h\\:mm\\:ss} (~{bytes16 / 1024 / 1024} MB, cabe nos 400 MB do Hub)");
    Ok(g8.DuracaoMaxima == GravadorChamada.Maximo, $"G.711: continua até {g8.DuracaoMaxima:h\\:mm} (3 h cabem: ~{44 + 3 * 3600 * 8000 * 4L >> 20} MB)");
}

Console.WriteLine("14) Não perturbe e bloqueio");
tel.NaoPerturbe = true;
var r = await pabx.LigarParaSoftphoneAsync("4003", "DND");
Ok(r == 486, $"não perturbe responde 486 (recebeu {r})");
tel.NaoPerturbe = false;
tel.Bloqueado = n => n == "4004";
r = await pabx.LigarParaSoftphoneAsync("4004", "Spam");
Ok(r == 486, $"número bloqueado recusado (recebeu {r})");
tel.Bloqueado = null;

Console.WriteLine("14b) Encaminhamento");
tel.EncaminharSempre = "700";
r = await pabx.LigarParaSoftphoneAsync("4005", "Encaminhar");
Ok(r == 302, $"encaminhar sempre responde 302 (recebeu {r})");
tel.EncaminharSempre = null;
tel.EncaminharSeNaoAtender = "701"; tel.EncaminharSegundos = 5;
recebida = null;
tarefa = pabx.LigarParaSoftphoneAsync("4006", "Sem resposta");
Ok(await Esperar(() => recebida?.Estado == EstadoChamada.Recebendo), "tocou antes de encaminhar");
Ok(await Esperar(() => recebida?.Estado == EstadoChamada.Encerrada, 9000) && recebida?.Resultado == ResultadoChamada.Encaminhada, $"encaminhou depois de 5 s sem atender ({recebida?.Resultado})");
var rNa = await tarefa;
Ok(rNa == 302, $"PABX recebeu 302 (recebeu {rNa})");
tel.EncaminharSeNaoAtender = null;

Console.WriteLine("14d) Clicar para ligar: número do link");
foreach (var (link, prefixo, esperado) in new[]
{
    ("tel:+55 (11) 3333-4444", "", "1133334444"),
    ("tel:+5511987654321", "0", "011987654321"),
    ("tel:011987654321", "0", "011987654321"),     // já tem o 0: não duplica
    ("sip:1001@pbx.example.com", "0", "1001"),      // ramal: sem prefixo
    ("callto://600", "", "600"),
    ("tel:+1-202-555-0100", "", "0012025550100"),  // outro país
    ("tel:3333-4444;phone-context=+55", "", "33334444"),
    ("tel:%2B55%2011%203333-4444", "", "1133334444"), // codificado (%2B = +)
    ("sip:fulano@empresa.com", "", ""),
    ("TEL:*99#", "", "*99#"),
})
{
    var n = Discagem.NumeroDoLink(link, prefixo);
    Ok(n == esperado, $"{link} (prefixo '{prefixo}') -> {n}");
}
Ok(Discagem.EhLink("tel:600") && Discagem.EhLink("\"sip:1001@x\"") && !Discagem.EhLink("--minimizado"), "reconhece link na linha de comando");

Console.WriteLine("14e) Atalhos da tela inicial (1.2.4): validação, segurança e ordem");
{
    AtalhoTela A(string nome, string tipo, string valor, string cor = "verde") => new() { Nome = nome, Tipo = tipo, Valor = valor, Cor = cor };
    Ok(AtalhosTela.Validar(A("Recepção", "ligar", "1001")) == null && AtalhosTela.Validar(A("Externo", "ligar", "+55 (11) 3333-4444")) == null, "ligar aceita ramal e número com formatação");
    Ok(AtalhosTela.Validar(A("", "ligar", "1001")) != null && AtalhosTela.Validar(A("X", "ligar", "")) != null && AtalhosTela.Validar(A("X", "ligar", "abc")) != null, "nome, valor vazio e letras no número são recusados");
    Ok(AtalhosTela.Validar(A(new string('a', 25), "ligar", "1")) != null && AtalhosTela.Validar(A("X", "nada", "1")) != null, "nome grande e tipo desconhecido são recusados");
    Ok(AtalhosTela.Validar(A("Site", "link", "https://glpi.corp.example.com")) == null && AtalhosTela.Validar(A("Site", "link", "http://a.com/x?y=1")) == null, "link http/https é aceito");
    Ok(AtalhosTela.Validar(A("Ruim", "link", "file:///C:/Windows/System32/cmd.exe")) != null && AtalhosTela.Validar(A("Ruim", "link", "calc.exe")) != null
        && AtalhosTela.Validar(A("Ruim", "link", "javascript:alert(1)")) != null && AtalhosTela.Validar(A("Ruim", "link", "ms-msdt:/id x")) != null, "link que abre programa/arquivo/script é recusado");
    Ok(AtalhosTela.Validar(A("Senha", "dtmf", "123#")) == null && AtalhosTela.Validar(A("Ruim", "dtmf", "12a")) != null && AtalhosTela.Validar(A("Ruim", "dtmf", new string('1', 33))) != null, "DTMF só dígitos, * e # (até 32)");
    Ok(AtalhosTela.Validar(A("Ana", "conversa", "ana.silva")) == null && AtalhosTela.Validar(A("Ruim", "conversa", "a b;c")) != null, "conversa exige login de rede");
    Ok(AtalhosTela.Validar(A("Almoço", "status", "Ausente")) == null && AtalhosTela.Validar(A("Ruim", "status", "Invisivel")) != null, "status só aceita os do app");
    var cfg = new List<AtalhoTela> { A("  Recepção ", "ligar", " 1001 ", "rosa"), A("Ruim", "ligar", ""), A("TI", "ligar", "2001", "azul") };
    cfg[2].Id = cfg[0].Id;                                                                  // id repetido
    var norm = AtalhosTela.Normalizar(cfg);
    Ok(norm.Count == 2 && norm[0].Nome == "Recepção" && norm[0].Valor == "1001" && norm[0].Cor == "verde" && norm[0].Id != norm[1].Id, "normalizar: tira inválido, apara espaços, cor desconhecida vira verde, id repetido ganha outro");
    Ok(AtalhosTela.Normalizar(Enumerable.Range(1, 60).Select(i => A("A" + i, "ligar", i.ToString()))).Count == AtalhosTela.Maximo, "no máximo 40 atalhos");
    Ok(AtalhosTela.Normalizar(null).Count == 0, "lista vazia ou ausente não quebra");
    var ord = new List<AtalhoTela> { A("1", "ligar", "1"), A("2", "ligar", "2"), A("3", "ligar", "3") };
    var id2 = ord[1].Id;
    Ok(AtalhosTela.Mover(ord, id2, -1) && ord[0].Nome == "2" && !AtalhosTela.Mover(ord, id2, -1) && AtalhosTela.Mover(ord, id2, 2) && ord[2].Nome == "2", "mover para antes/depois e parar na ponta");
    Ok(AtalhosTela.Iniciais("Maria da Silva") == "MS" && AtalhosTela.Iniciais("Recepção") == "RE" && AtalhosTela.Iniciais("") == "?", "iniciais do cartão");
}

Console.WriteLine("14f) Toques de chamada e sons das teclas para escolher (1.2.7)");
{
    Ok(Toques.Presets.Length >= 12 && Toques.Presets.Select(p => p.Id).Distinct().Count() == Toques.Presets.Length && Toques.Presets.All(p => p.Nome.Length > 0), $"{Toques.Presets.Length} toques de chamada, ids únicos e com nome");
    Ok(Toques.Presets.All(p => Toques.DuracaoCicloMs(p.Id) is >= 1500 and <= 6000), "todo toque tem um ciclo de 1,5 a 6 s (com pausa antes de repetir)");
    var soTons = Toques.Presets.Select(p => Toques.DuracaoCicloMs(p.Id)).Distinct().Count();
    Ok(soTons >= 6, $"os toques são diferentes entre si ({soTons} durações distintas)");
    Ok(Toques.SonsTecla.Length >= 8 && Toques.SonsTecla.Select(p => p.Id).Distinct().Count() == Toques.SonsTecla.Length && Toques.SonsTecla.Any(s => s.Id == "dtmf") && Toques.SonsTecla.Any(s => s.Id == "nenhum"), "lista de sons de tecla: ids únicos, com o padrão (dtmf) e 'Sem som'");
    Ok("123456789*0#".All(t => Toques.SonsTecla.Where(s => s.Id != "nenhum").All(s => Toques.DuracaoTeclaMs(s.Id, t) > 0)), "toda tecla do teclado faz som em todos os sons, menos 'Sem som'");
    Ok("123456789*0#".All(t => Toques.DuracaoTeclaMs("nenhum", t) == 0) && Toques.DuracaoTeclaMs("clique", 'x') == 0, "'Sem som' e tecla que não existe não fazem som");
    Ok(Toques.DuracaoTeclaMs(null, '5') == 120 && Toques.DuracaoTeclaMs("id-que-nao-existe", '5') == 120, "sem escolha (ou id desconhecido) volta aos tons de telefone de sempre");
    Ok(Toques.DuracaoTeclaMs("clique", '5') < Toques.DuracaoTeclaMs("bip", '5') && Toques.DuracaoTeclaMs("bip", '5') < Toques.DuracaoTeclaMs("notas", '5'), "clique é mais curto que bip, que é mais curto que nota");
    var t1 = new Toques { Silencioso = true, PresetTecla = "notas" };
    t1.Dtmf('5'); t1.Dtmf('x');                                                            // silencioso: não pode lançar exceção nem tocar
    Ok(true, "Dtmf com Silencioso e tecla inválida não lança erro");
}

Console.WriteLine("14b2) Endereço do painel web de administração (1.1.1)");
foreach (var (hub, esperado) in new[]
{
    ("https://softphone.corp.example.com", "https://softphone.corp.example.com/admin/"),
    ("https://softphone.corp.example.com/", "https://softphone.corp.example.com/admin/"),
    ("http://192.0.2.5:5280//", "http://192.0.2.5:5280/admin/"),
})
{
    await using var hc = new HubCliente(hub);
    Ok(hc.UrlPainel == esperado, $"{hub} -> {hc.UrlPainel}");
}

Console.WriteLine("14b3) Cliente do Hub sem servidor: reconexão, token salvo, renovação, download (1.1.2)");
await TesteHubCliente.RodarAsync(Ok);

Console.WriteLine("14c) Tratamento de voz (eco e ruído, WebRTC)");
TesteVoz.Rodar(Ok);

Console.WriteLine("15) Desregistrar ao sair");
await tel.PararAsync();
Ok(await Esperar(() => pabx.ContatoRegistrado == null), "PABX recebeu REGISTER Expires: 0");

Console.WriteLine();
Console.ForegroundColor = falhas == 0 ? ConsoleColor.Green : ConsoleColor.Red;
Console.WriteLine(falhas == 0 ? "TODOS OS TESTES PASSARAM" : $"{falhas} TESTE(S) FALHARAM");
Console.ResetColor();
if (falhas > 0 || args.Contains("--log"))
{
    Console.WriteLine("--- log do motor ---");
    foreach (var l in log) Console.WriteLine(l);
}
pabx.Parar();
return falhas == 0 ? 0 : 1;

/// <summary>PABX mínimo: registrar com digest, extensões de teste e chamadas para o softphone.</summary>
sealed class PabxSimulado
{
    private readonly string _realm, _ramal, _senha;
    private readonly string _nonce = Guid.NewGuid().ToString("N");
    private SIPTransport _t = null!;
    private SIPUserAgent? _saindo;

    public PabxSimulado(string realm, string ramal, string senha) { _realm = realm; _ramal = ramal; _senha = senha; }

    public SIPURI? ContatoRegistrado { get; private set; }
    public int KeepAlives;
    public int InvitesAutenticados;
    public int Desligadas;
    public int Cancelamentos;
    public volatile bool EmEspera;
    public string Dtmf = "";
    public string? CodecNegociado;
    public string? UltimoReferTo;
    public IAudioChamada? UltimaChamada;
    /// <summary>Áudio de cada ramal de teste atendido (600 fala 440 Hz, 603 fala 1300 Hz).</summary>
    public readonly ConcurrentDictionary<string, AudioTeste> Atendidas = new();
    /// <summary>--silencioso: o PABX simulado manda silêncio (testar a tela de madrugada sem apitar no alto-falante).</summary>
    public static bool Silencioso;

    public void Iniciar(int porta)
    {
        _t = new SIPTransport();
        var canal = new SIPUDPChannel(new IPEndPoint(IPAddress.Loopback, porta));
        _t.AddSIPChannel(canal);
        _t.SIPTransportRequestReceived += Requisicao;
        // CRLF puro (keep-alive) não vira requisição: conta pelo evento bruto do canal
        canal.SIPMessageReceived += (ch, l, r, buf) => { if (buf.All(b => b == 13 || b == 10)) Interlocked.Increment(ref KeepAlives); return Task.CompletedTask; };
    }

    public void Parar() => _t.Shutdown();

    private bool Autenticado(SIPRequest req, SIPAuthorisationHeadersEnum tipo)
    {
        var h = req.Header.AuthenticationHeaders.FirstOrDefault(x => x.SIPDigest.AuthorisationType == tipo);
        if (h == null) return false;
        var d = h.SIPDigest;
        string ha1 = Md5($"{d.Username}:{_realm}:{_senha}");
        string ha2 = Md5($"{req.Method}:{d.URI}");
        return d.Username == _ramal && d.Response == Md5($"{ha1}:{_nonce}:{ha2}");
    }

    private void Desafiar(SIPRequest req, SIPResponseStatusCodesEnum codigo, SIPAuthorisationHeadersEnum tipo)
    {
        var resp = SIPResponse.GetResponse(req, codigo, null);
        resp.Header.AuthenticationHeaders.Add(new SIPAuthenticationHeader(tipo, _realm, _nonce));
        _ = _t.SendResponseAsync(resp);
    }

    private static string Md5(string s) => Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();

    private async Task Requisicao(SIPEndPoint local, SIPEndPoint remoto, SIPRequest req)
    {
        if (req.Method == SIPMethodsEnum.REGISTER)
        {
            if (!Autenticado(req, SIPAuthorisationHeadersEnum.Authorize)) { Desafiar(req, SIPResponseStatusCodesEnum.Unauthorised, SIPAuthorisationHeadersEnum.WWWAuthenticate); return; }
            var ok = SIPResponse.GetResponse(req, SIPResponseStatusCodesEnum.Ok, null);
            var expires = req.Header.Expires >= 0 ? req.Header.Expires : req.Header.Contact.FirstOrDefault()?.Expires ?? 3600;
            if (req.Header.Contact.FirstOrDefault()?.Expires is long ce && ce >= 0) expires = ce;
            if (expires == 0) ContatoRegistrado = null;
            else
            {
                var uri = req.Header.Contact[0].ContactURI.CopyOf();
                // como um PABX com nat=force_rport: usa o endereço de onde o REGISTER veio
                uri.Host = remoto.GetIPEndPoint().ToString();
                ContatoRegistrado = uri;
            }
            ok.Header.Contact = req.Header.Contact;
            ok.Header.Expires = (int)expires;
            await _t.SendResponseAsync(ok);
            return;
        }

        if (req.Header.To?.ToTag != null) return;

        if (req.Method == SIPMethodsEnum.INVITE)
        {
            if (!Autenticado(req, SIPAuthorisationHeadersEnum.ProxyAuthorization)) { Desafiar(req, SIPResponseStatusCodesEnum.ProxyAuthenticationRequired, SIPAuthorisationHeadersEnum.ProxyAuthenticate); return; }
            Interlocked.Increment(ref InvitesAutenticados);
            var ua = new SIPUserAgent(_t, null);
            var uas = ua.AcceptCall(req);
            switch (req.URI.User)
            {
                case "600":
                case "603":
                    uas.Progress(SIPResponseStatusCodesEnum.Ringing, null, null, null, null);
                    await Task.Delay(200);
                    var midia = NovaMidia(req.URI.User == "603" ? 1300 : 440, req.URI.User);
                    LigarEventos(ua);
                    await ua.Answer(uas, midia);
                    break;
                case "601":
                    uas.Reject(SIPResponseStatusCodesEnum.BusyHere, null, null);
                    break;
                case "602":
                    uas.Progress(SIPResponseStatusCodesEnum.Ringing, null, null, null, null);
                    uas.CallCancelled += (_, _) => Interlocked.Increment(ref Cancelamentos);
                    break;
                default:
                    uas.Reject(SIPResponseStatusCodesEnum.NotFound, null, null);
                    break;
            }
        }
    }

    private VoIPMediaSession NovaMidia(double frequencia = 440, string? ramal = null)
    {
        var audio = new AudioTeste(frequencia) { Amplitude = Silencioso ? 0 : 8000 };
        UltimaChamada = audio;
        if (ramal != null) Atendidas[ramal] = audio;
        var m = new VoIPMediaSession(new MediaEndPoints { AudioSource = audio, AudioSink = audio }) { AcceptRtpFromAny = true };
        m.OnAudioFormatsNegotiated += f => CodecNegociado = f.FirstOrDefault().FormatName;
        return m;
    }

    private void LigarEventos(SIPUserAgent ua)
    {
        ua.OnDtmfTone += (tecla, _) => Dtmf += tecla switch { 10 => "*", 11 => "#", _ => tecla.ToString() };
        ua.RemotePutOnHold += () => EmEspera = true;
        ua.RemoteTookOffHold += () => EmEspera = false;
        ua.OnCallHungup += _ => Interlocked.Increment(ref Desligadas);
        ua.OnTransferRequested += (referTo, referidoPor) =>
        {
            UltimoReferTo = referTo.ToString();
            // como o PABX real: depois do 202 avisa o andamento (NOTIFY com sipfrag); com FalharTransferencia, o final é erro
            if (FalharTransferencia is { } falha) _ = NotificarTransferenciaAsync(ua, falha);
            return true; // aceita (202); a perna nova para o 700 não importa para o teste
        };
        ua.OnAttendedTransferRequested += req => UltimoReferTo = req.Header.ReferTo;
    }

    public async Task<OptionsResultado> EnviarOptionsAsync()
    {
        var tcs = new TaskCompletionSource<int>();
        var req = SIPRequest.GetRequest(SIPMethodsEnum.OPTIONS, ContatoRegistrado!);
        var tx = new SIPNonInviteTransaction(_t, req, null);
        tx.NonInviteTransactionFinalResponseReceived += (l, r, t, resp) => { tcs.TrySetResult(resp.StatusCode); return Task.FromResult(SocketError.Success); };
        tx.NonInviteTransactionFailed += (t, reason) => tcs.TrySetResult(-1);
        tx.SendRequest();
        var fim = await Task.WhenAny(tcs.Task, Task.Delay(3000));
        return new OptionsResultado(fim == tcs.Task ? tcs.Task.Result : -1);
    }

    /// <summary>O PABX liga para o softphone. Retorna o código final (200, 486, 487...).</summary>
    public async Task<int> LigarParaSoftphoneAsync(string de, string nome)
    {
        var ua = new SIPUserAgent(_t, null);
        _saindo = ua;
        int codigo = 0;
        ua.ClientCallFailed += (_, _, resp) => codigo = resp?.StatusCode ?? -1;
        LigarEventos(ua);
        var destino = ContatoRegistrado!.ToString();
        var descritor = new SIPCallDescriptor(null, null, destino, $"\"{nome}\" <sip:{de}@127.0.0.1>", destino, null, null, null,
            SIPCallDirection.Out, SIPSorcery.Net.SDP.SDP_MIME_CONTENTTYPE, null, null);
        bool ok = await ua.Call(descritor, NovaMidia(), 15);
        return ok ? 200 : codigo;
    }

    public void CancelarChamadaSaindo() => _saindo?.Cancel();

    /// <summary>Com valor (ex.: "486 Busy Here"), a próxima transferência é aceita (202) e depois falha, como quando o
    /// ramal de destino está ocupado ou não existe.</summary>
    public string? FalharTransferencia;

    private async Task NotificarTransferenciaAsync(SIPUserAgent ua, string final)
    {
        await Task.Delay(150);
        foreach (var (frag, fim) in new[] { ("100 Trying", false), ("183 Session Progress", false), (final, true) })
        {
            var d = ua.Dialogue;
            if (d == null) return;
            var req = d.GetInDialogRequest(SIPMethodsEnum.NOTIFY);
            req.Header.Event = "refer";
            req.Header.SubscriptionState = fim ? "terminated;reason=noresource" : "active;expires=60";
            req.Header.ContentType = "message/sipfrag;version=2.0";
            req.Body = "SIP/2.0 " + frag;
            new SIPNonInviteTransaction(_t, req, null).SendRequest();
            await Task.Delay(100);
        }
    }
}

readonly record struct OptionsResultado(int Codigo)
{
    public static bool operator ==(OptionsResultado a, int b) => a.Codigo == b;
    public static bool operator !=(OptionsResultado a, int b) => a.Codigo != b;
    public override string ToString() => Codigo.ToString();
}


static class MMTeste
{
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, Pack = 2)]
    struct WFX { public short tag, ch; public int rate, avg; public short align, bits, cb; }
    [System.Runtime.InteropServices.DllImport("winmm.dll")] static extern int waveInOpen(out IntPtr h, IntPtr dev, ref WFX f, IntPtr cb, IntPtr inst, int flags);
    [System.Runtime.InteropServices.DllImport("winmm.dll", EntryPoint = "waveInOpen")] static extern int waveInOpenNA(out IntPtr h, IntPtr dev, NAudio.Wave.WaveFormat f, IntPtr cb, IntPtr inst, int flags);
    [System.Runtime.InteropServices.DllImport("winmm.dll")] static extern int waveInClose(IntPtr h);
    public static int Abrir(bool naudio)
    {
        IntPtr h; int r;
        if (naudio) r = waveInOpenNA(out h, (IntPtr)(-1), new NAudio.Wave.WaveFormat(8000, 16, 1), IntPtr.Zero, IntPtr.Zero, 0);
        else { var w = new WFX { tag = 1, ch = 1, rate = 8000, bits = 16, align = 2, avg = 16000 }; r = waveInOpen(out h, (IntPtr)(-1), ref w, IntPtr.Zero, IntPtr.Zero, 0); }
        if (r == 0) waveInClose(h);
        return r;
    }
}