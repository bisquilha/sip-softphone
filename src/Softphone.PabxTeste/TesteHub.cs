using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Softphone.Contratos;
using Softphone.Core;

/// <summary>Testes do Hub (servidor rodando com Hub:Teste=true): dois usuários de teste conversando.</summary>
static class TesteHub
{
    public static async Task<int> RodarAsync(string url, bool testarWindows)
    {
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

        ServidorFalsoIa.Iniciar();
        var sufixo = DateTime.Now.ToString("HHmmss");
        string la = "ana.teste" + sufixo, lb = "beto.teste" + sufixo;
        await using var a = new HubCliente(url) { LoginTeste = la };
        await using var b = new HubCliente(url) { LoginTeste = lb };
        var msgsB = new ConcurrentQueue<MensagemDto>();
        var presA = new ConcurrentQueue<(string, EstadoPresenca)>();
        var lidasA = new ConcurrentQueue<(long, string, long)>();
        var digitA = new ConcurrentQueue<(long, string)>();
        var convB = new ConcurrentQueue<ConversaDto>();
        b.Mensagem += m => msgsB.Enqueue(m);
        b.Conversa += c => convB.Enqueue(c);
        a.Presenca += (l, e, r) => presA.Enqueue((l, e));
        a.Lida += (c, l, u) => lidasA.Enqueue((c, l, u));
        a.Digitando += (c, l) => digitA.Enqueue((c, l));

        Console.WriteLine("H1) Login e conexão");
        await a.IniciarAsync();
        Ok(a.Estado == EstadoHub.Conectado, $"A conectado ({a.Estado} {a.Motivo})");
        await b.IniciarAsync();
        Ok(b.Estado == EstadoHub.Conectado, $"B conectado ({b.Estado} {b.Motivo})");
        Ok(await Esperar(() => presA.Any(p => p.Item1 == lb && p.Item2 == EstadoPresenca.Disponivel)), "A viu B ficar online");

        Console.WriteLine("H2) Diretório e ramal");
        await b.InformarRamalAsync("4321");
        var pessoas = await a.ListarPessoasAsync();
        var pb = pessoas.FirstOrDefault(p => p.Login == lb);
        Ok(pb != null && pb.Ramal == "4321" && pb.Presenca == EstadoPresenca.Disponivel, $"B no diretório com ramal e online ({pb?.Ramal}, {pb?.Presenca})");
        Ok(pessoas.Count >= 2, $"diretório tem {pessoas.Count} pessoas");

        Console.WriteLine("H3) Presença");
        await b.DefinirPresencaAsync(EstadoPresenca.EmLigacao, "em reunião");
        Ok(await Esperar(() => presA.Any(p => p.Item1 == lb && p.Item2 == EstadoPresenca.EmLigacao)), "A viu B 'em ligação'");

        Console.WriteLine("H4) Conversa direta");
        var conv = await a.AbrirDiretaAsync(lb);
        Ok(!conv.Grupo && conv.Membros.Length == 2, "conversa direta criada com 2 membros");
        var conv2 = await a.AbrirDiretaAsync(lb);
        Ok(conv2.Id == conv.Id, "abrir de novo reaproveita a mesma conversa");
        var enviada = await a.EnviarAsync(conv.Id, "Olá, Beto! Tudo certo com o Softphone?");
        Ok(await Esperar(() => msgsB.Any(m => m.Id == enviada.Id)), "B recebeu a mensagem em tempo real");
        var convsB = await b.ListarConversasAsync();
        Ok(convsB.FirstOrDefault(c => c.Id == conv.Id)?.NaoLidas == 1, "B tem 1 não lida");
        await b.DigitarAsync(conv.Id);
        Ok(await Esperar(() => digitA.Any(d => d.Item1 == conv.Id && d.Item2 == lb)), "A viu 'B está digitando'");
        await b.MarcarLidaAsync(conv.Id, enviada.Id);
        Ok(await Esperar(() => lidasA.Any(l => l.Item1 == conv.Id && l.Item2 == lb && l.Item3 == enviada.Id)), "A recebeu a confirmação de leitura");
        convsB = await b.ListarConversasAsync();
        Ok(convsB.First(c => c.Id == conv.Id).NaoLidas == 0, "não lidas zerou");
        var resp = await b.EnviarAsync(conv.Id, "Tudo certo! 👍");
        var hist = await a.HistoricoAsync(conv.Id);
        Ok(hist.Count == 2 && hist[0].Id == enviada.Id && hist[1].Id == resp.Id, "histórico em ordem (2 mensagens)");

        Console.WriteLine("H5) Arquivo");
        var tmp = Path.Combine(Path.GetTempPath(), $"softphone-teste-{sufixo}.txt");
        await File.WriteAllTextAsync(tmp, "conteúdo de teste do Softphone");
        var arq = await a.EnviarArquivoAsync(tmp);
        var comArq = await a.EnviarAsync(conv.Id, "segue o arquivo", arq.Id);
        Ok(comArq.Arquivo?.Nome == Path.GetFileName(tmp), "mensagem com arquivo anexado");
        var baixado = tmp + ".baixado";
        await b.BaixarArquivoAsync(arq.Id, baixado);
        Ok(await File.ReadAllTextAsync(baixado) == "conteúdo de teste do Softphone", "B baixou o arquivo íntegro");
        File.Delete(tmp); File.Delete(baixado);

        Console.WriteLine("H6) Grupo e segurança");
        await using var c = new HubCliente(url) { LoginTeste = "caio.teste" + sufixo };
        await c.IniciarAsync();
        var grupo = await a.CriarGrupoAsync("TI - teste " + sufixo, new[] { lb, "caio.teste" + sufixo });
        Ok(grupo.Grupo && grupo.Membros.Length == 3, "grupo criado com 3 membros");
        Ok(await Esperar(() => convB.Any(x => x.Id == grupo.Id)), "B foi avisado do grupo novo");
        await c.EnviarAsync(grupo.Id, "oi grupo");
        Ok(await Esperar(() => msgsB.Any(m => m.Conversa == grupo.Id && m.Texto == "oi grupo")), "B recebeu mensagem do grupo");
        string? erro = null;
        try { await c.HistoricoAsync(conv.Id); } catch (Exception ex) { erro = ex.Message; }
        Ok(erro != null && erro.Contains("não participa"), $"C NÃO lê a conversa direta de A e B ({erro})");
        erro = null;
        try { await c.BaixarArquivoAsync(arq.Id, Path.GetTempFileName()); } catch (Exception ex) { erro = ex.Message; }
        Ok(erro != null, "C NÃO baixa o arquivo da conversa de A e B");

        Console.WriteLine("H6b) Reunião / chamada de vídeo");
        var convitesB = new ConcurrentQueue<ConviteDto>();
        b.Convite += cv => convitesB.Enqueue(cv);
        var reu = await a.IniciarReuniaoAsync(conv.Id, null, true);
        Ok(reu.Sala.StartsWith($"softphone-c{conv.Id}-") && reu.Url.Contains("?jwt="), $"sala criada ({reu.Sala}) com passe");
        Ok(await Esperar(() => convitesB.Any(x => x.Sala == reu.Sala && x.De == la)), "B recebeu a chamada de vídeo tocando");
        Ok(await Esperar(() => msgsB.Any(m => m.Texto.StartsWith(HubNomes.PrefixoConvite) && m.Texto.Contains(reu.Sala))), "convite apareceu no chat de B");
        var jwt = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().ReadJwtToken(reu.Url.Split("?jwt=")[1]);
        Ok(jwt.Issuer == "softphone" && jwt.Audiences.Contains("jitsi") && jwt.Payload["room"]?.ToString() == reu.Sala && jwt.Payload["context"]?.ToString()?.Contains(la) == true,
            "passe do Jitsi com emissor, sala e usuário certos");
        var reuB = await b.EntrarReuniaoAsync(reu.Sala, reu.Titulo);
        Ok(reuB.Sala == reu.Sala && reuB.Url != reu.Url, "B pegou o próprio passe para a mesma sala");
        erro = null;
        try { await c.EntrarReuniaoAsync(reu.Sala); } catch (Exception ex) { erro = ex.Message; }
        Ok(erro != null && erro.Contains("não participa"), "C NÃO entra na sala da conversa de A e B");
        var avulsa = await c.IniciarReuniaoAsync(null, null, false);
        // sala de conversa = "softphone-c<id>-xxxx"; a avulsa tem só o sufixo aleatório (que pode começar com "c")
        Ok(avulsa.Sala.StartsWith("softphone-") && !System.Text.RegularExpressions.Regex.IsMatch(avulsa.Sala, @"^softphone-c\d+-"), $"reunião avulsa ({avulsa.Sala})");

        // Claude Code, 05/11/2026 (Hub 1.2.2): chamar pessoas da lista ao criar a reunião e "Adicionar pessoas" com ela aberta
        Console.WriteLine("H6e) Reunião: chamar pela lista, adicionar pessoas, link para o Outlook");
        var convitesC = new ConcurrentQueue<ConviteDto>();
        c.Convite += cv => convitesC.Enqueue(cv);
        var reuNova = await a.IniciarReuniaoAsync(null, new[] { lb }, true);
        Ok(await Esperar(() => convitesB.Any(x => x.Sala == reuNova.Sala && x.De == la)), "nova reunião com pessoas escolhidas: o Softphone de B toca");
        var offline = await a.ConvidarParaReuniaoAsync(reu.Sala, reu.Titulo, new[] { "caio.teste" + sufixo, "ninguem.offline" + sufixo });
        Ok(await Esperar(() => convitesC.Any(x => x.Sala == reu.Sala && x.De == la)), "adicionar pessoa com a reunião aberta: o Softphone de C toca");
        Ok(offline.Contains("ninguem.offline" + sufixo), $"quem está offline volta na resposta ({string.Join(", ", offline)})");
        var reuC = await c.EntrarReuniaoAsync(reu.Sala, reu.Titulo);
        Ok(reuC.Sala == reu.Sala, "C, convidado, entra na sala da conversa de A e B (antes era barrado)");
        using (var web = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = new Uri(url.TrimEnd('/') + "/") })
        {
            // sem login: o Hub pede o Windows antes de olhar a sala (não revela nada a quem não entrou)
            var semLogin = await web.GetAsync($"reuniao/{reu.Sala}");
            Ok((int)semLogin.StatusCode == 401, $"link /reuniao/ pede o login do Windows ({(int)semLogin.StatusCode})");
        }
        if (testarWindows)
        {
            using var web = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseDefaultCredentials = true }) { BaseAddress = new Uri(url.TrimEnd('/') + "/") };
            var r = await web.GetAsync($"reuniao/{avulsa.Sala}");
            Ok((int)r.StatusCode == 302 && r.Headers.Location?.ToString().Contains($"/{avulsa.Sala}?jwt=") == true, $"link /reuniao/ com o Windows leva para a sala com passe ({(int)r.StatusCode})");
            var invalida = await web.GetAsync("reuniao/sala_invalida!");
            var deConversa = await web.GetAsync($"reuniao/{reu.Sala}");
            Ok((int)invalida.StatusCode == 404 && (int)deConversa.StatusCode == 403, $"sala inválida = 404; sala de conversa de outros = 403 ({(int)invalida.StatusCode}, {(int)deConversa.StatusCode})");
        }

        Console.WriteLine("H6c) Ocultar, sair e excluir");
        var removidasB = new ConcurrentQueue<long>();
        b.ConversaRemovida += id => removidasB.Enqueue(id);
        await a.OcultarConversaAsync(conv.Id);
        Ok(!(await a.ListarConversasAsync()).Any(x => x.Id == conv.Id), "A ocultou a conversa com B");
        await b.EnviarAsync(conv.Id, "voltei");
        Ok((await a.ListarConversasAsync()).Any(x => x.Id == conv.Id), "mensagem nova fez a conversa reaparecer para A");
        erro = null;
        try { await b.ExcluirGrupoAsync(grupo.Id); } catch (Exception ex) { erro = ex.Message; }
        Ok(erro != null && erro.Contains("criou"), "B (não criador) NÃO exclui o grupo");
        await c.SairDoGrupoAsync(grupo.Id);
        Ok(!(await c.ListarConversasAsync()).Any(x => x.Id == grupo.Id), "C saiu do grupo");
        Ok(await Esperar(() => msgsB.Any(m => m.Conversa == grupo.Id && m.Texto.Contains("saiu do grupo"))), "B viu o aviso de saída");
        await a.ExcluirGrupoAsync(grupo.Id);
        Ok(await Esperar(() => removidasB.Contains(grupo.Id)), "B foi avisado da exclusão do grupo");
        erro = null;
        try { await b.HistoricoAsync(grupo.Id); } catch (Exception ex) { erro = ex.Message; }
        Ok(erro != null, "histórico do grupo excluído não existe mais");

        Console.WriteLine("H6d) Ramal vindo do AD (ipPhone)");
        string ld = "dani.teste" + sufixo;
        await using (var d = new HubCliente(url) { LoginTeste = ld, RamalAdTeste = "7" + sufixo[^3..] })
        {
            await d.IniciarAsync();
            var ramalAd = "7" + sufixo[^3..];
            Ok(d.Eu?.Ramal == ramalAd, $"sem ramal aprendido, vale o do AD ({d.Eu?.Ramal})");
            await d.InformarRamalAsync("8" + sufixo[^3..]);
            var pd = (await a.ListarPessoasAsync()).FirstOrDefault(p => p.Login == ld);
            Ok(pd?.Ramal == ramalAd, $"1.3.1: o ramal de USO do AD manda; o aprendido (apps antigos informam o de login) não troca o que o chat disca ({pd?.Ramal})");
            await d.InformarRamalAsync(null);
            await a.InformarRamalAsync(ramalAd);   // outra pessoa agora usa o ramal que o AD diz ser da D
            pd = (await b.ListarPessoasAsync()).FirstOrDefault(p => p.Login == ld);
            Ok(pd != null && pd.Ramal == null, $"ramal do AD em uso por outra pessoa não aparece duplicado ({pd?.Ramal ?? "vazio"})");
            await a.InformarRamalAsync(null);
        }

        Console.WriteLine("H10) IA: gravação -> transcrição -> resumo -> contexto -> sugestão (servidor falso)");
        var estadoIa = await a.EstadoIaAsync();
        Ok(estadoIa.Transcricao && estadoIa.Resumo && estadoIa.RetencaoDias == 90, $"IA ligada no Hub de teste (transcrição {estadoIa.Transcricao}, resumo {estadoIa.Resumo}, {estadoIa.RetencaoDias} dias)");
        var eventosGrav = new ConcurrentQueue<GravacaoDto>();
        a.Gravacao += g => eventosGrav.Enqueue(g);
        var wavTeste = ServidorFalsoIa.GerarWavLigacao(Path.Combine(Path.GetTempPath(), $"softphone-ligacao-{sufixo}.wav"));
        var grav = await a.EnviarGravacaoAsync(wavTeste, "(11) 98765-4321", "Cliente Teste", entrada: true, DateTime.Now.AddMinutes(-1), TimeSpan.FromSeconds(3));
        Ok(grav.Id > 0 && grav.Status == StatusGravacao.Fila, $"gravação recebida ({grav.Status}, expira {grav.ExpiraEm:dd/MM})");
        Ok(await Esperar(() => eventosGrav.Any(e => e.Id == grav.Id && e.Status == StatusGravacao.Pronta), 20000),
            $"andamento chegou por evento: {string.Join(" > ", eventosGrav.Where(e => e.Id == grav.Id).Select(e => e.Status))}");
        var det = await a.GravacaoAsync(grav.Id);
        Ok(det.Segmentos.Length == 2 && det.Segmentos[0].Lado == "eu" && det.Segmentos[1].Lado == "outro",
            $"transcrição separada por lado e em ordem: {string.Join(" | ", det.Segmentos.Select(s => $"{s.Lado} {s.Inicio}s {s.Texto}"))}");
        Ok(!det.Segmentos.Any(s => s.Texto.Contains("Amara")), "frase inventada em silêncio descartada");
        Ok(det.Resumo?.Assunto == "Assunto de teste" && det.Resumo.ProximosPassos.Length == 2 && det.Resumo.QuemLigou == "Cliente Teste Ltda",
            $"resumo: {det.Resumo?.Assunto} / {det.Resumo?.ProximosPassos.Length} passos / {det.Resumo?.QuemLigou}");
        var pedido = ServidorFalsoIa.UltimoPedido ?? "";
        Ok(pedido.Contains("\"claude-opus-5-5\"") && pedido.Contains("cache_control") && pedido.Contains("fala de teste"),
            "pedido à IA: modelo claude-opus-5-5, prompt com cache e a transcrição");
        Ok(pedido.Contains("\"fallbacks\":\"default\"") && (ServidorFalsoIa.UltimoBeta ?? "").Contains("server-side-fallback-2026-07-01"),
            $"modelo reserva ligado (beta {ServidorFalsoIa.UltimoBeta})");
        Ok(ServidorFalsoIa.UltimaChave == "sk-ant-api03-chave-teste", "chave da API vai no cabeçalho (o SDK 13.x usa Authorization: Bearer)");
        var ctxNum = await a.ContextoAsync("+55 11 98765-4321");
        Ok(ctxNum.Anteriores.Length == 1 && ctxNum.QuemE == "Cliente Teste Ltda" && ctxNum.Anteriores[0].Assunto == "Assunto de teste",
            $"contexto do número (com +55 e sem máscara): {ctxNum.QuemE} / {ctxNum.Anteriores.Length} ligação(ões)");
        erro = null;
        try { await b.GravacaoAsync(grav.Id); } catch (Exception ex) { erro = ex.Message; }
        Ok(erro != null, "outra pessoa NÃO vê a minha gravação");
        Ok((await b.ContextoAsync("11987654321")).Anteriores.Length == 0, "nem o contexto dela");
        var audioBaixado = Path.Combine(Path.GetTempPath(), $"softphone-baixado-{sufixo}.wav");
        await a.BaixarAudioGravacaoAsync(grav.Id, audioBaixado);
        Ok(new FileInfo(audioBaixado).Length == new FileInfo(wavTeste).Length, "áudio baixado igual ao enviado");
        await a.ExcluirGravacaoAsync(grav.Id);
        Ok(!(await a.ListarGravacoesAsync()).Any(x => x.Id == grav.Id), "gravação excluída");

        // 1.2.4: transcrição só quando a pessoa pede (gravar não transcreve sozinho)
        var guardada = await a.EnviarGravacaoAsync(wavTeste, "(11) 98765-4321", "Cliente Teste", entrada: true, DateTime.Now.AddMinutes(-1), TimeSpan.FromSeconds(3), transcrever: false);
        Ok(guardada.Status == StatusGravacao.Guardada, $"gravação sem transcrever fica só guardada ({guardada.Status})");
        await Task.Delay(1500);
        var detG = await a.GravacaoAsync(guardada.Id);
        Ok(detG.Gravacao.Status == StatusGravacao.Guardada && detG.Segmentos.Length == 0, $"o servidor não transcreveu sozinho ({detG.Gravacao.Status}, {detG.Segmentos.Length} trechos)");
        var pedida = await a.ReprocessarGravacaoAsync(guardada.Id);
        Ok(pedida.Status == StatusGravacao.Fila, $"botão Transcrever coloca na fila ({pedida.Status})");
        Ok(await Esperar(() => eventosGrav.Any(e => e.Id == guardada.Id && e.Status == StatusGravacao.Pronta), 20000), "transcrição pedida ficou pronta");
        await a.ExcluirGravacaoAsync(guardada.Id);
        File.Delete(wavTeste); File.Delete(audioBaixado);

        Console.WriteLine("H11) Chat 1.1: citar, @menção, editar, apagar, buscar");
        Ok(a.ServidorV11, $"Hub {a.VersaoServidor} tem os recursos da 1.1");
        var alteradasB = new ConcurrentQueue<MensagemDto>();
        b.MensagemAlterada += m => alteradasB.Enqueue(m);
        var citada = await b.EnviarAsync(conv.Id, "Pode revisar o pedido 4521 hoje?");
        var resposta = await a.EnviarMensagemAsync(conv.Id, "@Beto sim, reviso agora", null, citada.Id, new[] { lb, "intruso.teste" });
        Ok(resposta.RespostaA == citada.Id && resposta.RespostaAutor == lb && resposta.RespostaTrecho == citada.Texto,
            $"resposta citando a mensagem ({resposta.RespostaAutor}: \"{resposta.RespostaTrecho}\")");
        Ok(resposta.Mencoes is { Length: 1 } && resposta.Mencoes[0] == lb, $"menção só de quem está na conversa ({string.Join(",", resposta.Mencoes ?? [])})");
        Ok(await Esperar(() => msgsB.Any(m => m.Id == resposta.Id && m.Mencoes?.Contains(lb) == true)), "B recebeu a mensagem com a menção dele");
        erro = null;
        try { await a.EnviarMensagemAsync(conv.Id, "citação de outra conversa", null, comArq.Id + 999999, null); } catch (Exception ex) { erro = ex.Message; }
        Ok(erro != null, "não cita mensagem que não é da conversa");
        var editada = await a.EditarMensagemAsync(resposta.Id, "@Beto sim, reviso agora mesmo");
        Ok(editada.EditadaEm != null && editada.Texto.EndsWith("agora mesmo"), "autor editou a mensagem");
        Ok(await Esperar(() => alteradasB.Any(m => m.Id == resposta.Id && m.EditadaEm != null)), "B viu a edição em tempo real");
        erro = null;
        try { await b.EditarMensagemAsync(resposta.Id, "hackeado"); } catch (Exception ex) { erro = ex.Message; }
        Ok(erro != null && erro.Contains("Só quem escreveu"), "outra pessoa NÃO edita a mensagem");
        var apagada = await a.ApagarMensagemAsync(comArq.Id);
        Ok(apagada.Apagada && apagada.Arquivo == null && apagada.Texto.Contains("apagada"), "autor apagou a mensagem com arquivo");
        erro = null;
        try { await b.BaixarArquivoAsync(arq.Id, Path.GetTempFileName()); } catch (Exception ex) { erro = ex.Message; }
        Ok(erro != null, "o arquivo da mensagem apagada não baixa mais");
        var histApos = await b.HistoricoAsync(conv.Id);
        Ok(histApos.Any(m => m.Id == comArq.Id && m.Apagada) && histApos.Any(m => m.Id == resposta.Id && m.RespostaA == citada.Id),
            "histórico mostra 'apagada' e a citação");
        var achados = await b.BuscarAsync("pedido 4521");
        Ok(achados.Count == 1 && achados[0].Mensagem.Id == citada.Id, $"busca achou a mensagem ({achados.Count})");
        Ok((await c.BuscarAsync("pedido 4521")).Count == 0, "C não acha mensagem de conversa da qual não participa");
        Ok((await b.BuscarAsync("segue o arquivo")).Count == 0, "mensagem apagada não aparece na busca");

        Console.WriteLine("H12) Comunicados da TI e painel de administração");
        await using var adm = new HubCliente(url) { LoginTeste = "admin.teste" };
        await adm.IniciarAsync();
        Ok(adm.Admin && !a.Admin, $"admin.teste é administrador ({adm.Admin}); A não é ({a.Admin})");
        erro = null;
        try { await a.PainelAsync(); } catch (Exception ex) { erro = ex.Message; }
        Ok(erro != null && erro.Contains("TI"), "quem não é admin NÃO abre o painel");
        await a.InformarClienteAsync("1.1.0", "PC-ANA");
        var painel = await adm.PainelAsync();
        Ok(painel.VersaoHub == a.VersaoServidor && painel.Online >= 3 && painel.Clientes.Any(x => x.Login == la && x.Versao == "1.1.0" && x.Pc == "PC-ANA" && x.Online),
            $"painel: Hub {painel.VersaoHub}, {painel.Online} online, versão/PC de A registrados");
        Ok(painel.Resumo && painel.ChaveIa.Contains("Anthropic") && painel.Modelo == "claude-opus-5-5", $"IA: {painel.ChaveIa} · {painel.Modelo}");
        var comunicadosB = new ConcurrentQueue<ComunicadoDto>();
        var canceladosB = new ConcurrentQueue<long>();
        b.Comunicado += x => comunicadosB.Enqueue(x);
        b.ComunicadoCancelado += id => canceladosB.Enqueue(id);
        var com = await adm.CriarComunicadoAsync(new NovoComunicadoDto("Protheus fora do ar", "Manutenção até as 18h.", NivelComunicado.Urgente, null, 4));
        Ok(await Esperar(() => comunicadosB.Any(x => x.Id == com.Id && x.Nivel == NivelComunicado.Urgente)), "B recebeu o comunicado urgente na hora");
        Ok((await b.ComunicadosPendentesAsync()).Any(x => x.Id == com.Id), "comunicado pendente para B (ao conectar depois, aparece)");
        await b.ConfirmarComunicadoAsync(com.Id);
        Ok(!(await b.ComunicadosPendentesAsync()).Any(x => x.Id == com.Id), "B clicou 'Ciente': não aparece de novo");
        var lista = await adm.ComunicadosAdminAsync();
        Ok(lista.FirstOrDefault(x => x.Comunicado.Id == com.Id) is { Confirmaram: 1 } ca && ca.Destinatarios >= 3, $"painel mostra quem confirmou ({lista.FirstOrDefault(x => x.Comunicado.Id == com.Id)?.Confirmaram})");
        var comUnidade = await adm.CriarComunicadoAsync(new NovoComunicadoDto("Só para a unidade X", "teste", NivelComunicado.Info, "UNIDADE-INEXISTENTE", 1));
        await Task.Delay(500);
        Ok(!comunicadosB.Any(x => x.Id == comUnidade.Id) && !(await b.ComunicadosPendentesAsync()).Any(x => x.Id == comUnidade.Id), "comunicado de outra unidade não chega em B");
        await adm.CancelarComunicadoAsync(com.Id);
        Ok(await Esperar(() => canceladosB.Contains(com.Id)), "cancelamento avisado em tempo real");

        Console.WriteLine("H13) Painel: chave da IA, teste, base de conhecimento, admins, ramais");
        var estadoIas = new ConcurrentQueue<EstadoIaDto>();
        a.EstadoIa += e => estadoIas.Enqueue(e);
        // 1.2.5: chave/modelo agora são provedores (H18); aqui fica a retenção
        var e2 = await adm.ConfigurarIaAsync(new ConfigIaDto(null, false, null, 60));
        Ok(e2.Resumo && e2.RetencaoDias == 60, $"retenção trocada pelo painel ({e2.RetencaoDias} dias)");
        Ok(await Esperar(() => estadoIas.Any(x => x.RetencaoDias == 60)), "apps avisados da mudança da IA");
        var teste = await adm.TestarIaAsync();
        Ok(teste.Ok && ServidorFalsoIa.UltimaChave == "sk-ant-api03-chave-teste", $"teste da IA usa a chave do hub.env, que virou o 1º provedor ({teste.Mensagem})");
        Ok((await adm.PainelAsync()).ChaveIa is var ci && ci.Contains("Anthropic") && !ci.Contains("chave-teste"), $"painel resume as IAs sem mostrar a chave ({(await adm.PainelAsync()).ChaveIa})");
        erro = null;
        try { await adm.ConfigurarIaAsync(new ConfigIaDto("sk-ant-api03-chave-antiga-0000000", false, null, null)); } catch (Exception ex) { erro = ex.Message; }
        Ok(erro?.Contains("provedor", StringComparison.OrdinalIgnoreCase) == true, $"chave pelo jeito antigo recusada, aponta para os provedores ({erro})");
        await adm.ConfigurarIaAsync(new ConfigIaDto(null, false, null, 90));
        var admins = await adm.DefinirAdminsAsync(new[] { lb, "nao.existe.teste" });
        Ok(admins.Contains(lb) && !admins.Contains("nao.existe.teste"), $"admin extra pelo painel ({string.Join(", ", admins)})");
        await adm.DefinirAdminsAsync(Array.Empty<string>());
        Ok((await adm.RamaisAdminAsync()).Count >= 1, "ramais aprendidos x AD no painel");

        Console.WriteLine("H14) Qualidade das ligações");
        await a.EnviarQualidadeAsync(new QualidadeLigacaoDto("1001", "saida", DateTime.Now.AddMinutes(-3), 120, "PCMA", 4.35, 4.2, 0.3, 8, 40, false, "1.1.0"));
        await b.EnviarQualidadeAsync(new QualidadeLigacaoDto("+55 11 3333-4444", "entrada", DateTime.Now.AddMinutes(-2), 300, "PCMA", 3.1, 2.4, 12.5, 45, null, true, "1.1.0"));
        erro = null;
        try { await a.EnviarQualidadeAsync(new QualidadeLigacaoDto("1001", "saida", DateTime.Now, 10, "PCMA", 9, 9, 0, 0, null, false, "1.1.0")); } catch (Exception ex) { erro = ex.Message; }
        Ok(erro != null, "medição fora da faixa recusada");
        var qp = await adm.QualidadePainelAsync(7);
        Ok(qp.PorUnidade.Sum(x => x.Ligacoes) >= 2 && qp.Piores.Any(x => x.Login == lb && x.Numero == "+551133334444" && x.Mos < 3.6),
            $"painel de qualidade: {qp.PorUnidade.Sum(x => x.Ligacoes)} ligações, {qp.Piores.Length} ruim(ns)");

        Console.WriteLine("H15) Painel web (/admin) servido pelo Hub, com cabeçalhos de segurança");
        using (var web = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = new Uri(url.TrimEnd('/') + "/") })
        {
            var r1 = await web.GetAsync("admin");
            Ok((int)r1.StatusCode == 302 && r1.Headers.Location?.ToString().EndsWith("/admin/") == true, $"/admin redireciona para /admin/ ({(int)r1.StatusCode} {r1.Headers.Location})");
            var r2 = await web.GetAsync("admin/");
            var html = await r2.Content.ReadAsStringAsync();
            Ok((int)r2.StatusCode == 200 && r2.Content.Headers.ContentType?.MediaType == "text/html" && html.Contains("Administração"),
                $"/admin/ devolve a página ({(int)r2.StatusCode} {r2.Content.Headers.ContentType?.MediaType})");
            string Cab(HttpResponseMessage r, string n) => r.Headers.TryGetValues(n, out var v) ? string.Join(",", v) : r.Content.Headers.TryGetValues(n, out var c) ? string.Join(",", c) : "";
            var csp = Cab(r2, "Content-Security-Policy");
            Ok(csp.Contains("default-src 'self'") && csp.Contains("frame-ancestors 'none'") && csp.Contains("script-src 'self'") && !csp.Contains("unsafe"),
                $"CSP restritiva ({csp})");
            Ok(Cab(r2, "X-Frame-Options") == "DENY" && Cab(r2, "X-Content-Type-Options") == "nosniff" && Cab(r2, "Referrer-Policy") == "no-referrer"
               && Cab(r2, "Cache-Control").Contains("no-store"), "X-Frame-Options, nosniff, no-referrer e no-store");
            var js = await web.GetAsync("admin/painel.js");
            var css = await web.GetAsync("admin/painel.css");
            Ok((int)js.StatusCode == 200 && js.Content.Headers.ContentType?.MediaType == "text/javascript"
               && (int)css.StatusCode == 200 && css.Content.Headers.ContentType?.MediaType == "text/css"
               && Cab(js, "Content-Security-Policy").Length > 0, "painel.js e painel.css com o tipo certo e os mesmos cabeçalhos");
            var trav = await web.GetAsync("admin/..%2Fappsettings.json");
            var outro = await web.GetAsync("admin/outro.html");
            Ok((int)trav.StatusCode == 404 && (int)outro.StatusCode == 404, $"nada além dos 3 arquivos ({(int)trav.StatusCode}, {(int)outro.StatusCode})");
            var saude = await web.GetStringAsync("saude");
            Ok(saude.Contains("\"versao\":\"1.3.3\""), "Hub na versão 1.3.3");
        }

        // Claude Code, 05/11/2026: achados R03/R08 (token salvo sem identidade, renovação) e R07 (conta desativada no AD)
        Console.WriteLine("H16) Renovar o token e cortar conta desativada no AD");
        using (var web = new HttpClient { BaseAddress = new Uri(url.TrimEnd('/') + "/") })
        {
            async Task<HttpResponseMessage> ComToken(HttpMethod m, string caminho, string token)
            {
                var req = new HttpRequestMessage(m, caminho);
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                return await web.SendAsync(req);
            }
            string lc = "caio.teste" + sufixo;
            var ec = await (await web.PostAsync($"api/entrar-teste?login={lc}", null)).Content.ReadFromJsonAsync<EntrarResposta>();
            var rn = await ComToken(HttpMethod.Post, "api/renovar", ec!.Token);
            var nova = rn.IsSuccessStatusCode ? await rn.Content.ReadFromJsonAsync<EntrarResposta>() : null;
            Ok(nova?.Eu.Login == lc && nova.VersaoServidor == "1.3.3" && !string.IsNullOrEmpty(nova.Token),
                $"/api/renovar devolve token novo, quem sou eu e a versão ({(int)rn.StatusCode} {nova?.Eu.Login} {nova?.VersaoServidor})");
            var lixo = await ComToken(HttpMethod.Post, "api/renovar", "nao-e-um-token");
            Ok((int)lixo.StatusCode == 401, $"token inválido não renova ({(int)lixo.StatusCode})");

            var tk = nova?.Token ?? ec.Token;
            await web.PostAsync($"api/teste/conta?login={lc}&estado=desativada", null);
            var eu = await ComToken(HttpMethod.Get, "api/eu", tk);
            var rn2 = await ComToken(HttpMethod.Post, "api/renovar", tk);
            var entrar = await web.PostAsync($"api/entrar-teste?login={lc}", null);
            Ok((int)eu.StatusCode == 401 && (int)rn2.StatusCode == 401 && (int)entrar.StatusCode == 403,
                $"conta desativada no AD: token emitido antes deixa de valer e não entra de novo ({(int)eu.StatusCode}, {(int)rn2.StatusCode}, {(int)entrar.StatusCode})");
            await web.PostAsync($"api/teste/conta?login={lc}&estado=ativa", null);
            var eu2 = await ComToken(HttpMethod.Get, "api/eu", tk);
            Ok((int)eu2.StatusCode == 200, $"conta reativada volta a entrar ({(int)eu2.StatusCode})");
        }

        // Claude Code, 05/11/2026 (Hub 1.2.3): "Sincronizar agora" no painel e administradores por grupo do AD
        Console.WriteLine("H17) Diretório: sincronizar agora e administradores por grupo do AD");
        using (var web = new HttpClient { BaseAddress = new Uri(url.TrimEnd('/') + "/") })
        {
            string? erroSinc = null;
            try { await adm.SincronizarDiretorioAsync(); } catch (Exception ex) { erroSinc = ex.Message; }
            Ok(erroSinc?.Contains("não está configurado") == true, $"sincronizar sem AD configurado explica o motivo ({erroSinc})");
            erroSinc = null;
            try { await b.SincronizarDiretorioAsync(); } catch (Exception ex) { erroSinc = ex.Message; }
            Ok(erroSinc != null, $"quem não é admin não sincroniza ({erroSinc})");

            async Task<EntrarResposta> EntrarComo(string login) =>
                (await (await web.PostAsync($"api/entrar-teste?login={login}", null)).Content.ReadFromJsonAsync<EntrarResposta>())!;
            await web.PostAsync($"api/teste/grupo?nome=GG-TI-TESTE&membros={lb}", null);
            Ok(!(await EntrarComo(lb)).Admin, "B ainda não é admin");
            // 1.2.4: o painel lista/pesquisa os grupos do AD em vez de exigir o nome exato
            var gruposAchados = await adm.BuscarGruposAdAsync("ti-te");
            Ok(gruposAchados.Any(x => x.Nome == "GG-TI-TESTE"), $"busca de grupos acha pelo pedaço do nome ({string.Join(", ", gruposAchados.Select(x => x.Nome))})");
            Ok((await adm.BuscarGruposAdAsync("")).Any(x => x.Nome == "GG-TI-TESTE"), "busca vazia lista os grupos");
            Ok((await adm.BuscarGruposAdAsync("nao-existe-xyz")).Count == 0, "busca sem resultado volta vazia");
            erro = null;
            try { await b.BuscarGruposAdAsync("ti"); } catch (Exception ex) { erro = ex.Message; }
            Ok(erro != null, "quem não é admin não pesquisa grupos");
            var grupos = await adm.DefinirGruposAdminAsync(new[] { "GG-TI-TESTE" });
            Ok(grupos.Any(g => g.Nome == "GG-TI-TESTE" && g.Membros == 1 && g.Erro == null), $"grupo salvo com 1 membro ({string.Join(", ", grupos.Select(g => $"{g.Nome}:{g.Membros}"))})");
            var eb = await EntrarComo(lb);
            var reqPainel = new HttpRequestMessage(HttpMethod.Get, "api/admin/painel");
            reqPainel.Headers.Authorization = new AuthenticationHeaderValue("Bearer", eb.Token);
            var painelB = await web.SendAsync(reqPainel);
            Ok(eb.Admin && (int)painelB.StatusCode == 200, $"membro do grupo vira admin: escudo no app e acesso ao painel (admin={eb.Admin}, painel {(int)painelB.StatusCode})");
            Ok((await adm.PainelAsync()).GruposAdmin?.Any(g => g.Nome == "GG-TI-TESTE" && g.Membros == 1) == true, "painel mostra o grupo e os membros");
            string? erroGrupo = null;
            try { await adm.DefinirGruposAdminAsync(new[] { "GG-TI-TESTE", "NAO-EXISTE-TESTE" }); } catch (Exception ex) { erroGrupo = ex.Message; }
            Ok(erroGrupo?.Contains("NAO-EXISTE-TESTE") == true && (await adm.PainelAsync()).GruposAdmin?.Length == 1,
                $"grupo que não existe é recusado e a lista fica como estava ({erroGrupo})");
            await adm.DefinirGruposAdminAsync(Array.Empty<string>());
            Ok(!(await EntrarComo(lb)).Admin, "tirou o grupo da lista: B deixa de ser admin");
        }

        // Claude Code, 05/11/2026 (Hub 1.2.5): várias IAs no painel, em ordem (reserva) ou revezando
        Console.WriteLine("H18) Várias IAs: provedores, reserva, revezar, buscar modelos");
        {
            var pv = await adm.ProvedoresIaAsync();
            var antiga = pv.Lista.FirstOrDefault();
            Ok(pv.Modo == "ordem" && pv.Lista.Length == 1 && antiga?.Tipo == TipoProvedorIa.Anthropic && antiga!.ChaveFim.EndsWith("este") && antiga.Ligado,
                $"a chave Anthropic que já existia virou o 1º provedor ({pv.Modo}; {string.Join(", ", pv.Lista.Select(x => $"{x.Nome}/{x.Tipo}/{x.Modelo}/…{x.ChaveFim}"))})");
            const string chaveBoa = "sk-boa-1234567890abcdef", chaveFalha = "sk-falha-123456789";
            var modelos = await adm.BuscarModelosIaAsync(new BuscarModelosIaDto(null, TipoProvedorIa.Outra, ServidorFalsoIa.Url + "/oa1", chaveBoa));
            Ok(modelos.Contains("modelo-a") && modelos.Contains("modelo-b"), $"buscar modelos pergunta à própria IA ({string.Join(", ", modelos)})");
            erro = null;
            try { await adm.SalvarProvedorIaAsync(new SalvarProvedorIaDto(null, "Sem endereço", TipoProvedorIa.Outra, null, "x", chaveBoa, true)); } catch (Exception ex) { erro = ex.Message; }
            Ok(erro != null, $"\"Outra\" sem endereço é recusada ({erro})");
            erro = null;
            try { await adm.SalvarProvedorIaAsync(new SalvarProvedorIaDto(null, "Sem chave", TipoProvedorIa.Gemini, null, "x", null, true)); } catch (Exception ex) { erro = ex.Message; }
            Ok(erro != null, $"provedor novo sem chave é recusado ({erro})");
            erro = null;
            try { await b.ProvedoresIaAsync(); } catch (Exception ex) { erro = ex.Message; }
            Ok(erro != null, "quem não é admin não vê os provedores");

            pv = await adm.SalvarProvedorIaAsync(new SalvarProvedorIaDto(null, "Falha", TipoProvedorIa.Outra, ServidorFalsoIa.Url + "/oafalha/", "modelo-a", chaveFalha, true));
            pv = await adm.SalvarProvedorIaAsync(new SalvarProvedorIaDto(null, "Boa", TipoProvedorIa.Outra, ServidorFalsoIa.Url + "/oa1", "modelo-a", chaveBoa, true));
            var idFalha = pv.Lista.First(x => x.Nome == "Falha").Id; var idBoa = pv.Lista.First(x => x.Nome == "Boa").Id;
            Ok(pv.Lista.Length == 3 && pv.Lista[0].Id == antiga!.Id, "novos provedores entram no fim da lista");
            var json = System.Text.Json.JsonSerializer.Serialize(pv);
            Ok(!json.Contains(chaveBoa) && !json.Contains(chaveFalha) && pv.Lista.First(x => x.Id == idBoa).ChaveFim == "cdef", "a lista nunca devolve a chave, só o fim");
            pv = await adm.OrdenarProvedoresIaAsync(new OrdemProvedoresIaDto("ordem", new[] { idFalha, idBoa, antiga!.Id }));
            Ok(string.Join(",", pv.Lista.Select(x => x.Nome)) == "Falha,Boa,Anthropic", $"ordem trocada ({string.Join(",", pv.Lista.Select(x => x.Nome))})");

            var tBoa = await adm.TestarProvedorIaAsync(idBoa);
            var tFalha = await adm.TestarProvedorIaAsync(idFalha);
            Ok(tBoa.Ok && !tFalha.Ok, $"testar cada IA ({tBoa.Mensagem} / {tFalha.Mensagem})");

            pv = await adm.ProvedoresIaAsync();
            var pf = pv.Lista.First(x => x.Id == idFalha); var pBoa = pv.Lista.First(x => x.Id == idBoa);
            Ok(pf.UltimaFalha != null && pf.UltimoErro is { Length: > 0 } && !pf.UltimoErro.Contains(chaveFalha) && pBoa.UltimoOk != null,
                $"painel mostra a falha (sem a chave) e o sucesso ({pf.UltimoErro})");

            // resumo de ligação pela cadeia: guarda qual IA fez
            int falhasAntes = ServidorFalsoIa.Chamadas("oafalha");
            var wavIa = ServidorFalsoIa.GerarWavLigacao(Path.Combine(Path.GetTempPath(), $"softphone-ia-{sufixo}.wav"));
            var gIa = await a.EnviarGravacaoAsync(wavIa, "(11) 91234-5678", "Cliente IA", entrada: false, DateTime.Now.AddMinutes(-1), TimeSpan.FromSeconds(3));
            Ok(await Esperar(() => eventosGrav.Any(e => e.Id == gIa.Id && e.Status == StatusGravacao.Pronta), 20000), "resumo pronto pela reserva");
            var detIa = await a.GravacaoAsync(gIa.Id);
            Ok(detIa.Resumo?.Ia == "Boa" && detIa.Resumo.Assunto == "Assunto de teste", $"o resumo guarda qual IA respondeu ({detIa.Resumo?.Ia})");
            var pedidoOa = ServidorFalsoIa.UltimoPedidoOpenAi ?? "";
            Ok(ServidorFalsoIa.UltimaChaveOpenAi == chaveBoa && ServidorFalsoIa.Chamadas("oafalha") > falhasAntes, "a 1ª IA falhou e a 2ª respondeu (reserva)");
            Ok(pedidoOa.Contains("\"modelo-a\"") && pedidoOa.Contains("json_object") && pedidoOa.Contains("fala de teste"), "pedido no formato OpenAI: modelo, JSON e a transcrição");
            await a.ExcluirGravacaoAsync(gIa.Id); File.Delete(wavIa);

            // IA que não aceita response_format: tenta de novo sem ele
            pv = await adm.SalvarProvedorIaAsync(new SalvarProvedorIaDto(null, "SemFormato", TipoProvedorIa.Outra, ServidorFalsoIa.Url + "/oasemformato", "modelo-a", chaveBoa, true));
            var idSem = pv.Lista.First(x => x.Nome == "SemFormato").Id;
            var tSem = await adm.TestarProvedorIaAsync(idSem);
            Ok(tSem.Ok, $"IA sem suporte a response_format funciona mesmo assim ({tSem.Mensagem})");

            // revezar: Boa e SemFormato ligadas, as outras desligadas
            await adm.SalvarProvedorIaAsync(new SalvarProvedorIaDto(idFalha, "Falha", TipoProvedorIa.Outra, ServidorFalsoIa.Url + "/oafalha", "modelo-a", null, false));
            await adm.SalvarProvedorIaAsync(new SalvarProvedorIaDto(antiga.Id, antiga.Nome, antiga.Tipo, antiga.Url, antiga.Modelo, null, false));
            pv = await adm.OrdenarProvedoresIaAsync(new OrdemProvedoresIaDto("revezar", new[] { idBoa, idSem, idFalha, antiga.Id }));
            Ok(pv.Modo == "revezar" && !pv.Lista.First(x => x.Id == idFalha).Ligado, "modo revezar e provedor desligado gravados");
            int boaAntes = ServidorFalsoIa.Chamadas("oa1"), semAntes = ServidorFalsoIa.Chamadas("oasemformato"), falhaAntes2 = ServidorFalsoIa.Chamadas("oafalha");
            for (int i = 0; i < 4; i++)
            {
                var wavRev = ServidorFalsoIa.GerarWavLigacao(Path.Combine(Path.GetTempPath(), $"softphone-rev{i}-{sufixo}.wav"));
                var gRev = await a.EnviarGravacaoAsync(wavRev, "(11) 91234-5678", "Cliente Rev", entrada: false, DateTime.Now.AddMinutes(-1), TimeSpan.FromSeconds(3));
                await Esperar(() => eventosGrav.Any(e => e.Id == gRev.Id && e.Status == StatusGravacao.Pronta), 20000);
                await a.ExcluirGravacaoAsync(gRev.Id); File.Delete(wavRev);
            }
            int dBoa = ServidorFalsoIa.Chamadas("oa1") - boaAntes, dSem = ServidorFalsoIa.Chamadas("oasemformato") - semAntes;
            Ok(dBoa >= 2 && dSem >= 2 && ServidorFalsoIa.Chamadas("oafalha") == falhaAntes2, $"revezando entre as ligadas, sem usar a desligada (Boa {dBoa}, SemFormato {dSem} pedidos)");

            // volta ao estado inicial: só a Anthropic, ligada, em ordem
            foreach (var id in new[] { idFalha, idBoa, idSem }) pv = await adm.ExcluirProvedorIaAsync(id);
            await adm.SalvarProvedorIaAsync(new SalvarProvedorIaDto(antiga.Id, antiga.Nome, antiga.Tipo, antiga.Url, antiga.Modelo, null, true));
            pv = await adm.OrdenarProvedoresIaAsync(new OrdemProvedoresIaDto("ordem", new[] { antiga.Id }));
            Ok(pv.Lista.Length == 1 && pv.Lista[0].Ligado && (await adm.TestarIaAsync()).Ok && ServidorFalsoIa.UltimaChave == "sk-ant-api03-chave-teste",
                "excluir provedores e voltar para a Anthropic (formato Anthropic continua funcionando)");
        }

        // Claude Code, 10/11/2026 (Hub 1.2.8/1.2.9): ramal, login e senha do SIP vindos do AD, entregues só à própria pessoa
        Console.WriteLine("H20) Conta SIP pelo AD: ramal + login + senha só para a própria pessoa");
        using (var web = new HttpClient { BaseAddress = new Uri(url.TrimEnd('/') + "/") })
        {
            const string senhaSecreta = "Zq9-senha-secreta-777";
            string Sim(string quem, string? ramal, string? loginSip, string? senha) =>
                $"api/teste/conta-sip?login={quem}&ramal={ramal}&loginSip={loginSip}&senha={Uri.EscapeDataString(senha ?? "")}";
            var vazio = await b.ContaSipAdAsync();
            Ok(vazio.Ramal is null && vazio.Senha is null && vazio.Login is null, "sem dados no AD: tudo vem vazio");
            var sem = await web.GetAsync("api/conta-sip");
            Ok((int)sem.StatusCode == 401, $"sem login não entrega nada ({(int)sem.StatusCode})");

            await web.PostAsync(Sim(lb, "2001", "1001", senhaSecreta), null);
            var contaB = await b.ContaSipAdAsync();
            Ok(contaB.Ramal == "2001" && contaB.Login == "1001" && contaB.Senha == senhaSecreta, $"a própria pessoa recebe ramal de uso, login e senha ({contaB.Ramal} / {contaB.Login})");
            var contaA = await a.ContaSipAdAsync();
            Ok(contaA.Ramal is null && contaA.Login is null && contaA.Senha is null, "outra pessoa não recebe os dados do B");

            var pessoasTexto = System.Text.Json.JsonSerializer.Serialize(await a.ListarPessoasAsync());
            Ok(!pessoasTexto.Contains(senhaSecreta), "a senha não aparece no diretório de pessoas");
            Ok(!System.Text.Json.JsonSerializer.Serialize(await adm.PainelAsync()).Contains(senhaSecreta), "nem no painel da TI");

            await web.PostAsync(Sim(lb, "4455", "", senhaSecreta), null);
            var soRamal = await b.ContaSipAdAsync();
            Ok(soRamal.Ramal == "4455" && soRamal.Login == "4455" && soRamal.Senha == senhaSecreta, "sem login no AD: o login é o próprio ramal (como no 1.2.8)");
            await web.PostAsync(Sim(lb, "", "1001", senhaSecreta), null);
            var soLogin = await b.ContaSipAdAsync();
            Ok(soLogin.Ramal is null && soLogin.Login == "1001" && soLogin.Senha == senhaSecreta, "só o login no AD: registra com ele (ramal de uso fica vazio)");
            await web.PostAsync(Sim(lb, "2001", "abc", senhaSecreta), null);
            var loginRuim = await b.ContaSipAdAsync();
            Ok(loginRuim.Login == "2001" && loginRuim.Ramal == "2001", "login inválido (não é número curto) é ignorado: vale o ramal");
            await web.PostAsync(Sim(lb, "abc", "", senhaSecreta), null);
            var ruim = await b.ContaSipAdAsync();
            Ok(ruim.Ramal is null && ruim.Login is null && ruim.Senha is null, "ramal inválido e sem login: não entrega nada");
            await web.PostAsync(Sim(lb, "2001", "1001", new string('x', 129)), null);
            var longa = await b.ContaSipAdAsync();
            Ok(longa.Ramal is null && longa.Login is null && longa.Senha is null, "senha com mais de 128 caracteres: não entrega nada");
            await web.PostAsync(Sim(lb, "2001", "1001", ""), null);
            var semSenha = await b.ContaSipAdAsync();
            Ok(semSenha.Ramal is null && semSenha.Login is null && semSenha.Senha is null, "sem senha: não entrega nada (o app não troca a conta pela metade)");
            await web.PostAsync(Sim(lb, "2001", "1001", senhaSecreta), null);
            Ok((await b.ContaSipAdAsync()).Senha == senhaSecreta, "volta a entregar quando o AD tem os dados");
            await web.PostAsync($"api/teste/conta-sip?login={lb}", null);   // tira
            Ok((await b.ContaSipAdAsync()).Senha is null, "sem dados de novo");
        }

        // Claude Code, 10/11/2026 (Hub 1.3.0 / app 1.2.0): artigos com permissão por setor, copiloto, anotações, auditoria, chamadas, qualidade
        Console.WriteLine("H21) Busca no histórico (acentos, número formatado, data, zeros)");
        {
            var dia = new DateTime(2026, 11, 10, 14, 30, 0);
            bool Casa(string? nome, string num, string? setor, string q) => BuscaHistorico.Combina(nome, num, setor, dia, q, dia);
            Ok(Casa("José da Silva", "1001", "Comercial", "jose"), "acento: 'jose' acha 'José'");
            Ok(Casa("Ana", "+55 (11) 3333-4444", null, "11 3333-4444") && Casa("Ana", "+55 (11) 3333-4444", null, "33334444"), "número com e sem formatação");
            Ok(Casa("Beto", "2001", null, "2001") && !Casa("Beto", "21", null, "2001"), "ramal com zeros à esquerda não casa com número sem zeros");
            Ok(Casa("Carla", "4455", "Financeiro", "financeiro") && !Casa("Carla", "4455", "Financeiro", "comercial"), "setor");
            Ok(Casa("Dino", "1234", null, "10/11") && Casa("Dino", "1234", null, "hoje") && !Casa("Dino", "1234", null, "ontem") && !Casa("Dino", "1234", null, "11/11"), "data: dd/MM, hoje, ontem");
            Ok(Casa("Eva Souza", "7000", null, "eva 7000") && !Casa("Eva Souza", "7000", null, "eva 9999") && Casa("Eva", "1", null, ""), "vários termos precisam casar; busca vazia mostra tudo");
        }

        Console.WriteLine("H21a) Identificação do chamador: +55, 0, operadora, DDD e ramais");
        {
            Ok(NumeroTelefone.Igual("(11) 3333-4444", "+55 11 3333-4444") && NumeroTelefone.Igual("11 3333-4444", "0 11 33334444") && NumeroTelefone.Igual("011 3333 4444", "551133334444"),
                "mesmo telefone com +55, 0 de tronco e formatação");
            Ok(NumeroTelefone.Igual("0 15 11 98888-7777", "11988887777") && NumeroTelefone.Igual("00 55 11 98888 7777", "(11) 98888-7777"), "código de operadora (015) e prefixo internacional");
            Ok(NumeroTelefone.Igual("33334444", "(11) 3333-4444"), "número local sem DDD casa pelos 8 últimos dígitos");
            Ok(!NumeroTelefone.Igual("(11) 3333-4444", "(21) 3333-4444") && NumeroTelefone.Nacional("11988887777") == "11988887777", "DDD diferente não casa");
            Ok(NumeroTelefone.Igual("1001", "1001") && !NumeroTelefone.Igual("1001", "258") && !NumeroTelefone.Igual("2001", "21") && NumeroTelefone.Nacional("1001") == "1001", "ramal preserva zeros à esquerda e não casa com número sem zeros");
            Ok(!NumeroTelefone.Igual("", "1001") && !NumeroTelefone.Igual(null, null), "número vazio não casa com nada");
        }

        Console.WriteLine("H21b) Legenda: recorte de fala por lado e detecção de pergunta");
        {
            short[] Tom(int ms, int amp) { var p = new short[8 * ms]; for (int i = 0; i < p.Length; i++) p[i] = (short)(amp * Math.Sin(i * 2 * Math.PI * 440 / 8000)); return p; }
            var eventos = new List<CapturaLegenda.Trecho>();
            var cap = new CapturaLegenda();
            cap.Pronto += t => { lock (eventos) eventos.Add(t); };
            void Quadros(bool eu, int ms, int amp) { for (int i = 0; i < ms / 20; i++) { var q = Tom(20, amp); if (eu) cap.Eu(q, 8000); else cap.Outro(q, 8000); } }
            Quadros(false, 1500, 4000); Quadros(false, 900, 0);                     // interlocutor: 1,5 s de fala e pausa
            Ok(eventos.Count == 1 && eventos[0].Papel == "interlocutor" && eventos[0].Final && eventos[0].Wav.Length > 44, "uma fala curta vira um trecho definitivo do interlocutor");
            Quadros(true, 300, 4000); Quadros(true, 900, 0);                        // atendente: 0,3 s (estalo)
            Ok(eventos.Count == 1, "som curto demais (0,3 s) é descartado");
            eventos.Clear();
            Quadros(true, 7000, 4000); Quadros(true, 900, 0);                       // atendente: 7 s contínuos
            var prov = eventos.Where(e => !e.Final).ToList(); var fin = eventos.Where(e => e.Final).ToList();
            Ok(prov.Count >= 1 && fin.Count == 1 && prov.All(p => p.Id == fin[0].Id && p.Papel == "atendente") && prov[0].Wav.Length < fin[0].Wav.Length,
                $"fala longa: provisórios ({prov.Count}) crescem até o definitivo, com o mesmo id e papel 'atendente'");
            eventos.Clear();
            Quadros(false, 20000, 0);
            Ok(eventos.Count == 0, "silêncio não gera trecho (não vai ao servidor)");
            Quadros(false, 800, 4000); cap.Parar();
            Ok(eventos.Count == 1 && eventos[0].Final, "chamada que acaba no meio da fala fecha o que ficou");
            Ok(DeteccaoPergunta.Parece("Vocês fazem paletes sob medida") && DeteccaoPergunta.Parece("E qual é o pedido mínimo") && DeteccaoPergunta.Parece("isso é caro?")
                && !DeteccaoPergunta.Parece("bom dia") && !DeteccaoPergunta.Parece("ok obrigado pela informação") && !DeteccaoPergunta.Parece(""),
                "perguntas são reconhecidas mesmo sem '?'; cumprimentos e agradecimentos não");
        }

        Console.WriteLine("H22) Auditoria, chamadas, resumo operacional e qualidade das ligações");
        using (var web = new HttpClient { BaseAddress = new Uri(url.TrimEnd('/') + "/") })
        {
            await web.PostAsync($"api/entrar-teste?login={la}&nome=Ana%20Teste&setor=Comercial", null);
            await web.PostAsync($"api/entrar-teste?login={lb}&nome=Beto%20Teste&setor=Financeiro", null);
            // auditoria: quem alterou, tentativa negada, sem gravar o conteúdo enviado
            await adm.ConfigurarIaAsync(new ConfigIaDto(null, false, null, 90));
            string erroNeg = ""; try { await a.ConfigurarIaAsync(new ConfigIaDto("sk-ant-api03-segredo-da-auditoria-0000", false, null, null)); } catch (Exception ex) { erroNeg = ex.Message; }
            var aud = await adm.AuditoriaAsync("ALTERAR");
            Ok(aud.Any(x => x.Usuario == "admin.teste" && x.Alvo == "/api/admin/ia" && x.Status == 200), "auditoria registra quem alterou a retenção da IA");
            Ok(erroNeg.Length > 0 && aud.Any(x => x.Usuario == la && x.Status == 403), "auditoria registra a tentativa negada (403)");
            Ok(!System.Text.Json.JsonSerializer.Serialize(aud).Contains("segredo-da-auditoria"), "auditoria não grava o conteúdo enviado");
            string erroAud = ""; try { await b.AuditoriaAsync(); } catch (Exception ex) { erroAud = ex.Message; }
            Ok(erroAud.Length > 0, "só a TI vê a auditoria");


            // chamadas e resumo operacional
            var idCh = Guid.NewGuid().ToString("N");
            var vazioResumo = await adm.ResumoOperacionalAsync();
            await a.EnviarChamadaAsync(new ChamadaRegistroDto(idCh, "1001", "entrada", "Perdida", DateTime.Now, 0, false, "chamada perdida"));
            await a.EnviarChamadaAsync(new ChamadaRegistroDto(idCh, "1001", "entrada", "Perdida", DateTime.Now, 0, false, "chamada perdida"));
            await a.EnviarChamadaAsync(new ChamadaRegistroDto(Guid.NewGuid().ToString("N"), "0259", "saida", "Atendida", DateTime.Now, 125, false, null));
            var resumo = await adm.ResumoOperacionalAsync();
            Ok(resumo.HaDadosDeChamadas && resumo.Perdidas == vazioResumo.Perdidas + 1 && resumo.Recebidas == vazioResumo.Recebidas + 1 && resumo.Realizadas == vazioResumo.Realizadas + 1,
                "resumo: a mesma chamada enviada duas vezes conta uma só; perdidas/recebidas/realizadas batem");
            var listaChamadas = await adm.ChamadasAdminAsync("periodo=hoje&resultado=Perdida");
            Ok(listaChamadas.Count(x => x.Id == idCh) == 1, "o indicador abre a listaChamadas com a mesma chamada, uma vez");
            Ok(resumo.TempoMedioSegundos is > 0, $"duração média calculada ({resumo.TempoMedioSegundos:N0} s)");
            string erroRes = ""; try { await b.ResumoOperacionalAsync(); } catch (Exception ex) { erroRes = ex.Message; }
            Ok(erroRes.Length > 0, "só a TI vê o resumo operacional");

            // qualidade: sem áudio não vira "100% de perda"
            await a.EnviarQualidadeAsync(new QualidadeLigacaoDto("0300", "entrada", DateTime.Now.AddMinutes(-1), 60, "PCMA", 1, 1, 100, 0, null, false, "1.2.0", false, "recebido", idCh));
            var qualP = await adm.QualidadePainelAsync(1);
            Ok(qualP.Piores.Any(x => x.Numero == "0300" && !x.Medido) && qualP.PorUnidade.Sum(u => u.SemMedicao) >= 1, "ligação sem áudio aparece como 'Sem medição'");
            var semMedia = qualP.PorUnidade.Where(u => u.Ligacoes == 0 && u.SemMedicao > 0).All(u => u.PerdaMedia == 0);
            Ok(semMedia, "sem medição não entra na perda média (não vira 100%)");
        }

        Console.WriteLine("H23) Perfis de acesso (grupo do AD) e comunicado por setor/grupo");
        using (var web = new HttpClient { BaseAddress = new Uri(url.TrimEnd('/') + "/") })
        {
            await web.PostAsync($"api/entrar-teste?login={la}&nome=Ana%20Teste&setor=Comercial", null);
            await web.PostAsync($"api/entrar-teste?login={lb}&nome=Beto%20Teste&setor=Financeiro", null);
            var grupoSup = "GG-SUP-" + sufixo;
            await web.PostAsync($"api/teste/grupo?nome={grupoSup}&membros={la}", null);
            var antes = await a.MeuPerfilAcessoAsync();
            Ok(antes.Perfil == "atendente", "sem regra, todos são atendentes");
            string erroSemPerfil = ""; try { await a.AlcanceComunicadoAsync(new NovoComunicadoV13Dto("T", "x", "info", null, 4, new[] { "Comercial" }, null, true)); } catch (Exception ex) { erroSemPerfil = ex.Message; }
            Ok(erroSemPerfil.Length > 0, "atendente não envia comunicado");
            string erroRegra = ""; try { await adm.GravarRegrasAcessoAsync(new[] { new RegraAcessoDto("GRUPO-QUE-NAO-EXISTE-" + sufixo, "supervisor", null) }); } catch (Exception ex) { erroRegra = ex.Message; }
            Ok(erroRegra.Length > 0, "regra com grupo inexistente no AD é recusada (nada é gravado)");
            await adm.GravarRegrasAcessoAsync(new[] { new RegraAcessoDto(grupoSup, "supervisor", new[] { "Comercial" }) });
            var sup = await a.MeuPerfilAcessoAsync();
            Ok(sup.Perfil == "supervisor" && sup.Setores.Contains("Comercial") && sup.Grupos.Contains(grupoSup), "membro do grupo vira supervisor do setor Comercial");
            Ok((await b.MeuPerfilAcessoAsync()).Perfil == "atendente", "quem não está no grupo continua atendente");

            var alc = await a.AlcanceComunicadoAsync(new NovoComunicadoV13Dto("Aviso", "Texto", "info", null, 4, new[] { "Comercial" }, null, true));
            Ok(alc.Pessoas >= 1 && !alc.Todos, $"alcance antes de enviar: {alc.Pessoas} pessoa(s) do Comercial");
            string erroTodos = ""; try { await a.EnviarComunicadoAsync(new NovoComunicadoV13Dto("Aviso", "Texto", "info", null, 4, null, null, true)); } catch (Exception ex) { erroTodos = ex.Message; }
            string erroOutroSetor = ""; try { await a.EnviarComunicadoAsync(new NovoComunicadoV13Dto("Aviso", "Texto", "info", null, 4, new[] { "Financeiro" }, null, true)); } catch (Exception ex) { erroOutroSetor = ex.Message; }
            Ok(erroTodos.Length > 0 && erroOutroSetor.Length > 0, "supervisor não envia para todos nem para outro setor");
            var enviado = await a.EnviarComunicadoAsync(new NovoComunicadoV13Dto("Reunião do Comercial", "Hoje às 15h.", "importante", null, 4, new[] { "Comercial" }, null, true));
            var pendA = await a.ComunicadosPendentesAsync(); var pendB = await b.ComunicadosPendentesAsync();
            Ok(enviado.ExigirCiencia && pendA.Any(x => x.Id == enviado.Id) && pendB.All(x => x.Id != enviado.Id), "só o setor Comercial recebe; o Financeiro não");
            var semCiencia = await a.EnviarComunicadoAsync(new NovoComunicadoV13Dto("Só avisando", "Sem confirmação.", "info", null, 4, new[] { "Comercial" }, null, false));
            Ok(!semCiencia.ExigirCiencia, "comunicado sem exigir ciência");
            var pendSem = await a.ComunicadosPendentesAsync();
            Ok(pendSem.All(x => x.Id != semCiencia.Id), "sem exigir ciência: já entregue ao app conectado, não fica pendente nem volta a aparecer");
            var admList = await adm.ComunicadosAdminAsync();
            var linhaAdm = admList.First(x => x.Comunicado.Id == semCiencia.Id);
            Ok(linhaAdm.Destinatarios >= 1 && linhaAdm.Entregues >= 1 && linhaAdm.Confirmaram == 0, $"painel separa destinatários ({linhaAdm.Destinatarios}), entregues ({linhaAdm.Entregues}) e cientes ({linhaAdm.Confirmaram})");

            // grupo do AD como público (TI)
            var porGrupo = await adm.EnviarComunicadoAsync(new NovoComunicadoV13Dto("Só o grupo", "Texto", "info", null, 4, null, grupoSup, true));
            Ok((await a.ComunicadosPendentesAsync()).Any(x => x.Id == porGrupo.Id) && (await b.ComunicadosPendentesAsync()).All(x => x.Id != porGrupo.Id), "comunicado para grupo do AD chega só aos membros");

            // revogação: sai do grupo, perde o perfil
            await web.PostAsync($"api/teste/grupo?nome={grupoSup}&membros={lb}", null);
            await adm.GravarRegrasAcessoAsync(new[] { new RegraAcessoDto(grupoSup, "supervisor", new[] { "Comercial" }) });   // regravar relê os grupos (como a sincronização)
            Ok((await a.MeuPerfilAcessoAsync()).Perfil == "atendente" && (await adm.PerfilDeAsync(lb)).Perfil == "supervisor", "saiu do grupo: perde o perfil (a sessão aberta não protege); quem entrou ganha");
            string erroRevogado = ""; try { await a.EnviarComunicadoAsync(new NovoComunicadoV13Dto("Aviso", "Texto", "info", null, 4, new[] { "Comercial" }, null, true)); } catch (Exception ex) { erroRevogado = ex.Message; }
            Ok(erroRevogado.Length > 0, "revogado: o envio é negado na hora");
            await adm.GravarRegrasAcessoAsync(Array.Empty<RegraAcessoDto>());
            await adm.GravarRegrasAcessoAsync(Array.Empty<RegraAcessoDto>());
        }
        Console.WriteLine("H24) Automações do chat: gatilho e ação, sem laço e sem repetir");
        {
            var msgsA = new ConcurrentQueue<MensagemDto>();
            a.Mensagem += m => msgsA.Enqueue(m);
            var convAuto = await a.AbrirDiretaAsync(lb);
            var r1 = new RegraAutomacao("", "Orçamento", true, "palavra", new[] { "orçamento" }, Array.Empty<string>(), "responder", "Olá {nome}, o orçamento sai em 1 dia útil.", null, la);
            var r2 = new RegraAutomacao("", "Chamada perdida", true, "chamada_perdida", Array.Empty<string>(), Array.Empty<string>(), "notificar", "Você perdeu uma chamada de {numero}.", null, la);
            var r3 = new RegraAutomacao("", "Só Financeiro", true, "palavra", new[] { "boleto" }, new[] { "Financeiro" }, "responder", "Segunda via no portal.", null, null);
            string erroRegraRuim = ""; try { await adm.GravarAutomacoesAsync(new[] { new RegraAutomacao("", "Ruim", true, "palavra", Array.Empty<string>(), Array.Empty<string>(), "responder", "x", null, null) }); } catch (Exception ex) { erroRegraRuim = ex.Message; }
            Ok(erroRegraRuim.Length > 0, "regra de palavra sem palavra-chave é recusada");
            var salvas = await adm.GravarAutomacoesAsync(new[] { r1, r2, r3 });
            Ok(salvas.Count == 3 && salvas.All(x => x.Id.Length > 0), "regras salvas com identificador");
            await a.EnviarAsync(convAuto.Id, "Preciso de um ORÇAMENTO de 10 paletes");
            Ok(await Esperar(() => msgsA.Any(m => m.Autor == "softphone.assistente" && m.Texto.Contains(a.Eu!.Nome) && m.Texto.Contains("1 dia útil"))), $"palavra-chave: o assistente responde na conversa, com o nome da pessoa ({a.Eu!.Nome}; recebidas do assistente: {string.Join(" | ", msgsA.Where(m => m.Autor == "softphone.assistente").Select(m => m.Texto))})");
            await Task.Delay(1200);
            Ok(msgsA.Count(m => m.Autor == "softphone.assistente" && m.Texto.Contains("1 dia útil")) == 1, "a resposta do assistente (que repete 'orçamento') não dispara a regra de novo: sem laço");
            await a.EnviarAsync(convAuto.Id, "e o boleto?");
            await Task.Delay(1000);
            Ok(!msgsA.Any(m => m.Texto.Contains("Segunda via")), "regra do setor Financeiro não vale para quem é do Comercial");
            var idPerdida = Guid.NewGuid().ToString("N");
            await a.EnviarChamadaAsync(new ChamadaRegistroDto(idPerdida, "0300", "entrada", "Perdida", DateTime.Now, 0, false, "chamada perdida"));
            Ok(await Esperar(() => msgsA.Any(m => m.Autor == "softphone.assistente" && m.Texto.Contains("0300"))), "chamada perdida: aviso direto do assistente com o número");
            await a.EnviarChamadaAsync(new ChamadaRegistroDto(idPerdida, "0300", "entrada", "Perdida", DateTime.Now, 0, false, "chamada perdida"));
            await Task.Delay(1000);
            var qtdPerdida = msgsA.Count(m => m.Texto.Contains("perdeu uma chamada de 0300"));
            Ok(qtdPerdida == 1, $"o mesmo evento chegando duas vezes não executa a ação duas vezes (mensagens: {qtdPerdida})");
            var exec = await adm.ExecucoesAutomacoesAsync();
            Ok(exec.Any(x => x.Nome == "Orçamento") && exec.Any(x => x.Nome == "Chamada perdida"), "painel lista as execuções (regra, evento e hora)");
            await adm.GravarAutomacoesAsync(Array.Empty<RegraAutomacao>());
        }
        Console.WriteLine("H25) Senha de rede: bloqueio depois de 5 erros seguidos");
        using (var web = new HttpClient { BaseAddress = new Uri(url.TrimEnd('/') + "/") })
        {
            var alvo = "forca.bruta" + sufixo;
            var codigos = new List<int>();
            for (int i = 0; i < 7; i++)
                codigos.Add((int)(await web.PostAsJsonAsync("api/entrar-senha", new { login = alvo, senha = "errada" + i })).StatusCode);
            Ok(codigos.Take(5).All(c => c == 401) && codigos.Skip(5).All(c => c == 429), $"5 erros respondem 401 e depois o login fica bloqueado com 429 ({string.Join(",", codigos)})");
            var outro = (int)(await web.PostAsJsonAsync("api/entrar-senha", new { login = "outra.pessoa" + sufixo, senha = "x" })).StatusCode;
            Ok(outro == 401, "o bloqueio é só daquele login: outra pessoa continua recebendo 401 normal");
            var aud = await adm.AuditoriaAsync("ENTRAR");
            Ok(aud.Any(x => x.Usuario == alvo && x.Status == 429) && !System.Text.Json.JsonSerializer.Serialize(aud).Contains("errada0") && !System.Text.Json.JsonSerializer.Serialize(aud).Contains("errada4"), "o bloqueio entra na auditoria, sem a senha digitada");
        }
        Console.WriteLine("H7) Offline");
        await b.DisposeAsync();
        Ok(await Esperar(() => presA.Any(p => p.Item1 == lb && p.Item2 == EstadoPresenca.Offline)), "A viu B ficar offline");

        if (testarWindows)
        {
            Console.WriteLine("H8) Login automático pelo Windows (sem LoginTeste)");
            await using var w = new HubCliente(url);
            await w.IniciarAsync();
            Ok(w.Estado == EstadoHub.Conectado && !string.IsNullOrEmpty(w.Eu?.Login), $"entrou como '{w.Eu?.Login}' / '{w.Eu?.Nome}' ({w.Estado} {w.Motivo})");
        }

        Console.WriteLine();
        Console.ForegroundColor = falhas == 0 ? ConsoleColor.Green : ConsoleColor.Red;
        Console.WriteLine(falhas == 0 ? "TODOS OS TESTES DO HUB PASSARAM" : $"{falhas} TESTE(S) DO HUB FALHARAM");
        Console.ResetColor();
        return falhas == 0 ? 0 : 1;
    }

    /// <summary>Colega de teste: entra como "ana.teste", manda oi para o destino e responde o que receber (para testar a tela).</summary>
    public static async Task<int> BotAsync(string url, string destino, int segundos)
    {
        await using var bot = new HubCliente(url) { LoginTeste = "ana.teste" };
        bot.Mensagem += async m =>
        {
            if (!string.Equals(m.Autor, destino, StringComparison.OrdinalIgnoreCase)) return;
            try
            {
                await bot.MarcarLidaAsync(m.Conversa, m.Id);
                await Task.Delay(800);
                await bot.DigitarAsync(m.Conversa);
                await Task.Delay(1500);
                await bot.EnviarAsync(m.Conversa, "Recebi: " + m.Texto);
            }
            catch (Exception ex) { Console.WriteLine("bot: " + ex.Message); }
        };
        await bot.IniciarAsync();
        Console.WriteLine($"bot: {bot.Estado} {bot.Motivo}");
        await bot.InformarRamalAsync("600");
        var c = await bot.AbrirDiretaAsync(destino);
        await bot.EnviarAsync(c.Id, "Oi! Aqui é a Ana, teste do chat do Softphone 👋");
        await Task.Delay(1500);
        await bot.EnviarAsync(c.Id, "Me responde aqui que eu confirmo o recebimento.");
        var g = await bot.CriarGrupoAsync("TI - Infra", new[] { destino });
        await bot.EnviarAsync(g.Id, "Grupo da TI criado para avisos de infraestrutura.");
        await Task.Delay(TimeSpan.FromSeconds(segundos));
        return 0;
    }
}
