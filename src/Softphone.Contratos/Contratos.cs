namespace Softphone.Contratos;

/// <summary>Estado de presença de uma pessoa. O "EmLigacao" o próprio Softphone informa quando há chamada em andamento.</summary>
public enum EstadoPresenca
{
    Offline = 0,
    Disponivel = 1,
    Ausente = 2,
    EmLigacao = 3,
    NaoPerturbe = 4,
}

/// <summary>Pessoa do diretório (vem do AD; o ramal o Hub aprende quando a pessoa usa o Softphone).</summary>
public sealed record PessoaDto(
    string Login,
    string Nome,
    string? Setor,
    string? Unidade,
    string? Email,
    string? Ramal,
    EstadoPresenca Presenca,
    DateTime? VistoEm,
    string? Recado);

public sealed record ArquivoDto(string Id, string Nome, long Tamanho, string Tipo);

/// <summary>
/// Mensagem do chat. Os campos depois de EnviadaEm vieram na 1.1 (opcionais: Hub 1.0 não manda, app 1.0 ignora).
/// RespostaA/RespostaAutor/RespostaTrecho = mensagem citada; Mencoes = logins marcados com @.
/// </summary>
public sealed record MensagemDto(
    long Id,
    long Conversa,
    string Autor,
    string Texto,
    ArquivoDto? Arquivo,
    DateTime EnviadaEm,
    long? RespostaA = null,
    string? RespostaAutor = null,
    string? RespostaTrecho = null,
    DateTime? EditadaEm = null,
    bool Apagada = false,
    string[]? Mencoes = null,
    ReacaoDto[]? Reacoes = null);

/// <summary>1.3.4: uma reação (emoji) a uma mensagem e quem reagiu com ela. Cada pessoa tem no máximo uma reação por mensagem.</summary>
public sealed record ReacaoDto(string Emoji, string[] Logins);

/// <summary>Mensagem nova com os recursos da 1.1 (citar e marcar pessoas).</summary>
public sealed record NovaMensagemDto(long Conversa, string Texto, string? ArquivoId, long? RespostaA, string[]? Mencoes);

/// <summary>Resultado da busca nas minhas conversas.</summary>
public sealed record ResultadoBuscaDto(MensagemDto Mensagem, string? TituloConversa, bool Grupo);

// ======================= comunicados da TI =======================

public static class NivelComunicado
{
    public const string Info = "info";
    public const string Importante = "importante";
    public const string Urgente = "urgente";
}

/// <summary>Aviso da TI que aparece em destaque no Softphone de todos (ou de uma unidade) até a pessoa clicar em "Ciente".</summary>
public sealed record ComunicadoDto(long Id, string Titulo, string Texto, string Nivel, string? Unidade, string Autor, string NomeAutor,
    DateTime CriadoEm, DateTime ExpiraEm, bool ExigirCiencia = true);

/// <summary>Comunicado visto pela TI no painel: destinatários, entregues (enviado a um app conectado ou buscado pelo app) e cientes ("Ciente" clicado).</summary>
public sealed record ComunicadoAdminDto(ComunicadoDto Comunicado, int Confirmaram, int Destinatarios, bool Cancelado, int Entregues = 0, string? Publico = null);

/// <summary>1.3: comunicado para unidade, setores e/ou grupo do AD. ExigirCiencia = false: a pessoa só fecha (não conta como ciente).</summary>
public sealed record NovoComunicadoV13Dto(string Titulo, string Texto, string Nivel, string? Unidade, int Horas, string[]? Setores, string? Grupo, bool ExigirCiencia);
public sealed record AlcanceComunicadoDto(int Pessoas, string[] Exemplos, bool Todos, string? Erro);
/// <summary>Regra simples de gatilho e ação do chat.</summary>
/// <param name="Gatilho">"palavra" (mensagem com uma das palavras), "chamada_perdida" (o app avisou uma chamada perdida) ou "boas_vindas" (primeiro acesso da pessoa).</param>
/// <param name="Acao">"responder" (o assistente responde na mesma conversa), "notificar" (mensagem direta do assistente à pessoa do evento) ou "encaminhar" (cópia da mensagem para o Destino).</param>
/// <param name="Setores">Só vale para quem é destes setores (vazio = todos).</param>
/// <param name="Responsavel">Login de quem cuida da regra (aparece no painel e no registro).</param>
public sealed record RegraAutomacao(string Id, string Nome, bool Ligada, string Gatilho, string[] Palavras, string[] Setores, string Acao, string Texto, string? Destino, string? Responsavel);
public sealed record ExecucaoAutomacao(string Regra, string Nome, string Chave, DateTime Em);


public sealed record RegraAcessoDto(string Grupo, string Perfil, string[]? Setores);
public sealed record PerfilEfetivoDto(string Login, string Perfil, string[] Setores, string[] Grupos);

public sealed record NovoComunicadoDto(string Titulo, string Texto, string Nivel, string? Unidade, int Horas);

// ======================= qualidade das ligações =======================

/// <summary>O app manda ao fim de cada ligação atendida (perda/variação medidas no áudio que chegou).</summary>
public sealed record QualidadeLigacaoDto(string Numero, string Direcao, DateTime Inicio, int DuracaoSegundos, string? Codec,
    double Mos, double MosMinimo, double PerdaPct, double JitterMs, double? RttMs, bool Conferencia, string? VersaoApp,
    bool? Medido = null, string? Sentido = null, string? ChamadaId = null);

public sealed record QualidadeUnidadeDto(string Unidade, int Ligacoes, double MosMedio, int Ruins, double PerdaMedia, double JitterMedio, int SemMedicao = 0);
public sealed record QualidadeItemDto(string Login, string Nome, string? Unidade, string Numero, DateTime Inicio, int DuracaoSegundos,
    double Mos, double PerdaPct, double JitterMs, string? VersaoApp, bool Medido = true, string? Sentido = null);
public sealed record QualidadePainelDto(int Dias, QualidadeUnidadeDto[] PorUnidade, QualidadeItemDto[] Piores);

// ======================= painel de administração =======================

/// <summary>Um Softphone conectado (ou visto por último): versão e PC.</summary>
public sealed record ClienteDto(string Login, string Nome, string? Unidade, string? Versao, string? Pc, DateTime? VistoEm, bool Online);

public sealed record PainelAdminDto(
    string VersaoHub, DateTime IniciadoEm, int Online, int Diretorio, bool Ldap, DateTime? SincronizadoEm, string? ErroDiretorio,
    bool Reunioes, bool Transcricao, bool Resumo, string ChaveIa, string Modelo, int RetencaoDias,
    int Gravacoes, double GravacoesMb, double EspacoLivreGb, Dictionary<string, int> Versoes, ClienteDto[] Clientes, string[] Admins,
    GrupoAdminDto[]? GruposAdmin = null);

/// <summary>Grupo do AD cujos membros são administradores (Hub 1.2.3). Erro: o grupo não foi lido no AD.</summary>
public sealed record GrupoAdminDto(string Nome, int Membros, string? Erro);
/// <summary>1.2.4: um grupo achado na busca do painel (Nome = sAMAccountName).</summary>
public sealed record GrupoAdDto(string Nome, string? Descricao);

/// <summary>Resultado do "Sincronizar agora" (Hub 1.2.3).</summary>
public sealed record SincronizacaoDto(int Total, DateTime? SincronizadoEm);

/// <summary>Mudanças da IA pelo painel. Chave vazia = mantém; ApagarChave = volta a usar a do hub.env (ou nenhuma).</summary>
public sealed record ConfigIaDto(string? Chave, bool ApagarChave, string? Modelo, int? RetencaoDias);

public sealed record TesteIaDto(bool Ok, string Mensagem);

// ----- 1.2.5: várias IAs (provedores), em ordem ou revezando -----

/// <summary>Tipos de provedor. "anthropic" usa o formato da Anthropic; os outros, o formato compatível com OpenAI.</summary>
public static class TipoProvedorIa
{
    public const string Anthropic = "anthropic", OpenAi = "openai", Gemini = "gemini", Xai = "xai", DeepSeek = "deepseek", Outra = "outra";
    public static readonly string[] Todos = [Anthropic, OpenAi, Gemini, Xai, DeepSeek, Outra];
}

/// <summary>Um provedor como o painel vê (a chave nunca volta, só o fim).</summary>
public sealed record ProvedorIaDto(string Id, string Nome, string Tipo, string Url, string Modelo, bool Ligado, string ChaveFim,
    DateTime? UltimoOk, DateTime? UltimaFalha, string? UltimoErro, string Uso = "tudo");

/// <summary>Lista e modo: "ordem" (o primeiro que responder) ou "revezar" (alterna, e falha passa para o próximo).</summary>
public sealed record ProvedoresIaDto(string Modo, ProvedorIaDto[] Lista);

/// <summary>Novo (Id vazio) ou alteração. Chave vazia na alteração = mantém a gravada. Url vazia = a padrão do tipo.</summary>
/// <summary>Uso: "tudo", "chat" (Sugerir resposta) ou "ligacoes" (resumo e copiloto): permite uma IA aprovada só para um tipo de dado.</summary>
public sealed record SalvarProvedorIaDto(string? Id, string Nome, string Tipo, string? Url, string Modelo, string? Chave, bool Ligado, string? Uso = null);

public sealed record OrdemProvedoresIaDto(string Modo, string[] Ids);

// ----- 1.2.6: perfis da base de conhecimento (setor e pessoal) -----

/// <summary>Linha das listas do painel. Setor: Chave = nome do setor; Pessoa: Chave = login. Grupo: quem pode editar o setor pelo app.</summary>
public sealed record PerfilResumoDto(string Chave, string Nome, string? Setor, int Pessoas, bool TemPerfil, long Tamanho, DateTime? AlteradoEm, string? Grupo);

public sealed record PerfisIaDto(PerfilResumoDto[] Setores, PerfilResumoDto[] Pessoas);

// 1.2.7: perfil em categorias, com anexos que a IA lê
/// <summary>Arquivo anexado a uma categoria. Resumo: o que a IA lê dele (ex.: "2 abas, 1.250 linhas").</summary>
public sealed record AnexoPerfilDto(string Id, string Nome, long Tamanho, DateTime EnviadoEm, string Resumo, int Caracteres);
/// <summary>Uma categoria do perfil (um assunto). Id gerado por quem cria (6 a 40 letras/números/-/_).</summary>
public sealed record CategoriaPerfilDto(string Id, string Titulo, string Texto, AnexoPerfilDto[]? Anexos = null);
public sealed record PerfilDto(CategoriaPerfilDto[] Categorias);
/// <summary>Modelo de categoria: título e a dica do que escrever (a TI edita no painel).</summary>
public sealed record ModeloCategoriaDto(string Titulo, string Dica);
public sealed record ModelosPerfilDto(ModeloCategoriaDto[] Pessoal, ModeloCategoriaDto[] Setor);

/// <summary>O que o app mostra em "Meu perfil para a IA". PodeEditarSetor: está no grupo do AD que a TI ligou ao setor.</summary>
public sealed record MeuPerfilDto(PerfilDto Pessoal, DateTime? AlteradoEm, string? Setor, bool PodeEditarSetor, PerfilDto? PerfilSetor, ModelosPerfilDto Modelos);

public sealed record GrupoPerfilSetorDto(string Setor, string? Grupo);

/// <summary>Pede a lista de modelos à IA. Com Id e sem chave, usa a chave gravada desse provedor.</summary>
public sealed record BuscarModelosIaDto(string? Id, string Tipo, string? Url, string? Chave);

public sealed record ArquivoConhecimentoDto(string Nome, long Tamanho, DateTime AlteradoEm);

public sealed record RamalAdminDto(string Login, string Nome, string? RamalAprendido, string? RamalAd, bool Ativo);

/// <summary>Conversa: direta (2 pessoas) ou grupo. Título de conversa direta = nome da outra pessoa (montado no cliente).</summary>
public sealed record ConversaDto(
    long Id,
    bool Grupo,
    string? Titulo,
    string[] Membros,
    MensagemDto? Ultima,
    int NaoLidas,
    long UltimaLidaPorTodos,
    string? Criador = null);

/// <summary>Admin (1.1): a pessoa vê o painel de administração no app.</summary>
public sealed record EntrarResposta(string Token, DateTime ExpiraEm, PessoaDto Eu, string VersaoServidor, bool Admin = false);

public sealed record EntrarComSenha(string Login, string Senha);

/// <summary>
/// Conta SIP que a TI cadastrou no AD para esta pessoa (1.2.8; a 1.2.9 acrescentou o Login). Só a própria pessoa recebe.
/// Ramal = ramal de USO (ipPhone: o que se disca e aparece no diretório); Login = ramal de LOGIN (o que registra no PABX; sem ele,
/// vale o ramal); Senha = senha do SIP. Tudo nulo = o AD não tem os dados.
/// </summary>
public sealed record ContaSipAdDto(string? Ramal, string? Senha, string? Login = null);

/// <summary>Passe para entrar numa sala do Jitsi. Url = sala + token (pessoal); UrlConvite = sala sem token (vai no chat).</summary>
public sealed record ReuniaoDto(string Sala, string Titulo, string Url, string UrlConvite, DateTime ExpiraEm);

/// <summary>Alguém está me chamando para uma reunião/chamada de vídeo (toca no Softphone, como no Teams).</summary>
public sealed record ConviteDto(string Sala, string Titulo, string De, string NomeDe, long? Conversa, bool Grupo);

// ======================= IA: gravação, transcrição, resumo =======================

/// <summary>Andamento de uma gravação no servidor.</summary>
public static class StatusGravacao
{
    public const string Fila = "fila";                 // chegou, esperando a vez
    public const string Transcrevendo = "transcrevendo";
    public const string Resumindo = "resumindo";
    public const string Pronta = "pronta";             // transcrição (e resumo, se a IA estiver ligada) prontos
    public const string SoAudio = "so-audio";          // transcrição desligada no servidor: fica só o áudio
    public const string Guardada = "guardada";         // 1.2.4: só o áudio; a pessoa não pediu transcrição (botão Transcrever)
    public const string Erro = "erro";
}

/// <summary>Trecho falado. Lado "eu" = quem gravou; "outro" = o outro lado da ligação.</summary>
public sealed record SegmentoDto(string Lado, double Inicio, double Fim, string Texto);

/// <summary>O que a IA tirou da ligação.</summary>
public sealed record ResumoDto(
    string Assunto,
    string Resumo,
    string[] ProximosPassos,
    string? QuemLigou,       // nome/empresa que a pessoa disse na ligação (identificação de quem ligou)
    string Sentimento,       // positivo | neutro | negativo
    string? SugestaoResposta,  // rascunho de retorno (e-mail/mensagem) para a pessoa
    string? Ia = null);        // 1.2.5: nome do provedor de IA que fez o resumo

public sealed record GravacaoDto(
    long Id,
    string Numero,
    string? Nome,
    string Direcao,          // entrada | saida
    DateTime Inicio,
    int DuracaoSegundos,
    string Status,
    string? Erro,
    string? Assunto,
    DateTime ExpiraEm);

public sealed record GravacaoDetalheDto(GravacaoDto Gravacao, SegmentoDto[] Segmentos, ResumoDto? Resumo);

/// <summary>O que já se sabe de um número antes de atender: ligações gravadas anteriores (só as minhas).</summary>
public sealed record ContextoNumeroDto(string Numero, string? QuemE, ContextoLigacaoDto[] Anteriores);
public sealed record ContextoLigacaoDto(long Gravacao, DateTime Quando, string? Assunto, string? Resumo);

/// <summary>Recursos de IA ligados no servidor (o app esconde o que não existe).</summary>
public sealed record EstadoIaDto(bool Gravacao, bool Transcricao, bool Resumo, int RetencaoDias, int GravacaoMaxMb);

public sealed record SugestaoDto(string Texto);

/// <summary>Nomes dos métodos/eventos do SignalR (um lugar só, para servidor e cliente não divergirem).</summary>
public static class HubNomes
{
    public const string Rota = "/hub";

    // servidor -> cliente
    public const string EvMensagem = "Mensagem";
    public const string EvPresenca = "Presenca";          // (login, estado, recado)
    public const string EvDigitando = "Digitando";        // (conversa, login)
    public const string EvConversa = "Conversa";          // (ConversaDto) criada ou alterada
    public const string EvLida = "Lida";                  // (conversa, login, ultimaId)
    public const string EvPessoa = "Pessoa";              // (PessoaDto) entrou/alterou ramal
    public const string EvConvite = "Convite";            // (ConviteDto) chamada de vídeo tocando
    public const string EvConviteResposta = "ConviteResposta"; // (sala, login, aceitou)
    public const string EvConviteCancelado = "ConviteCancelado"; // (sala) quem chamou desistiu
    public const string EvConversaRemovida = "ConversaRemovida"; // (conversa) saí/excluíram/ocultei em outro PC
    public const string EvGravacao = "Gravacao";          // (GravacaoDto) andamento de uma gravação minha
    // 1.1
    public const string EvMensagemAlterada = "MensagemAlterada"; // (MensagemDto) editada ou apagada
    public const string EvComunicado = "Comunicado";              // (ComunicadoDto) aviso da TI
    public const string EvComunicadoCancelado = "ComunicadoCancelado"; // (id)
    public const string EvEstadoIa = "EstadoIa";                  // (EstadoIaDto) a TI ligou/desligou a IA pelo painel

    // cliente -> servidor
    public const string DefinirPresenca = "DefinirPresenca";  // (estado, recado?)
    public const string InformarRamal = "InformarRamal";      // (ramal)
    public const string ListarPessoas = "ListarPessoas";      // () -> PessoaDto[]
    public const string ListarConversas = "ListarConversas";  // () -> ConversaDto[]
    public const string AbrirDireta = "AbrirDireta";          // (login) -> ConversaDto
    public const string CriarGrupo = "CriarGrupo";            // (titulo, logins[]) -> ConversaDto
    public const string Historico = "Historico";              // (conversa, antesDeId, quantidade) -> MensagemDto[]
    public const string Enviar = "Enviar";                    // (conversa, texto, arquivoId?) -> MensagemDto
    public const string MarcarLida = "MarcarLida";            // (conversa, ultimaId)
    public const string Digitar = "Digitar";                  // (conversa)
    public const string IniciarReuniao = "IniciarReuniao";    // (conversa?, logins[]?, tocar) -> ReuniaoDto
    public const string EntrarReuniao = "EntrarReuniao";      // (sala) -> ReuniaoDto
    public const string ResponderConvite = "ResponderConvite"; // (sala, deLogin, aceitou)
    public const string CancelarConvite = "CancelarConvite";  // (sala)
    public const string ConvidarParaReuniao = "ConvidarParaReuniao"; // (sala, titulo, logins[]) -> logins offline (1.2.2)
    public const string OcultarConversa = "OcultarConversa";  // (conversa) some da minha lista até chegar mensagem nova
    public const string SairDoGrupo = "SairDoGrupo";          // (conversa)
    public const string ExcluirGrupo = "ExcluirGrupo";        // (conversa) só quem criou; apaga para todos
    // 1.1
    public const string EnviarMensagem = "EnviarMensagem";    // (NovaMensagemDto) -> MensagemDto (com citação e @menções)
    public const string EditarMensagem = "EditarMensagem";    // (id, texto) -> MensagemDto (só o autor)
    public const string Reagir = "Reagir";                    // (id, emoji ou vazio) -> MensagemDto (1.3.4; qualquer membro da conversa)
    public const string ApagarMensagem = "ApagarMensagem";    // (id) -> MensagemDto (só o autor; fica "mensagem apagada")
    public const string Buscar = "Buscar";                    // (texto) -> ResultadoBuscaDto[] (só nas minhas conversas)
    public const string InformarCliente = "InformarCliente";  // (versao, pc) para o painel da TI
    public const string ComunicadosPendentes = "ComunicadosPendentes"; // () -> ComunicadoDto[] ainda não confirmados
    public const string ConfirmarComunicado = "ConfirmarComunicado";   // (id) "Ciente"
    public const string PrefixoConvite = "📹 Reunião: ";      // mensagem do chat que vira botão "Entrar"
}

// ======================= 1.3: artigos, chamadas, copiloto, auditoria =======================

/// <summary>Artigo da base de conhecimento. SetoresLeitura vazio = todos leem; SetoresEdicao vazio = só a TI edita.</summary>
public sealed record ArtigoDto(long Id, string Titulo, string Categoria, string[] Tags, string Corpo, string[] SetoresLeitura, string[] SetoresEdicao,
    bool Publicado, int Versao, string Autor, DateTime AtualizadoEm, bool PodeEditar);
public sealed record ArtigoResumoDto(long Id, string Titulo, string Categoria, string[] Tags, string Trecho, int Versao, DateTime AtualizadoEm);
public sealed record SalvarArtigoDto(long? Id, string Titulo, string Categoria, string[] Tags, string Corpo, string[] SetoresLeitura, string[] SetoresEdicao, bool Publicado);
public sealed record ArtigoVersaoDto(int Versao, string Autor, DateTime Em, string Titulo);

/// <summary>O app manda ao fim de cada chamada (também perdidas e recusadas) para o painel da TI. Id = identificador da chamada.</summary>
public sealed record ChamadaRegistroDto(string Id, string Numero, string Direcao, string Resultado, DateTime Inicio, int DuracaoSegundos, bool Transferida, string? Motivo);
public sealed record ChamadaPainelDto(string Id, string Login, string Nome, string? Unidade, string? Setor, string Numero, string Direcao, string Resultado,
    DateTime Inicio, int DuracaoSegundos, bool Transferida, string? Motivo);

/// <summary>Anotação feita durante ou depois da chamada (protocolo = código que o atendente passa ao cliente).</summary>
public sealed record AnotacaoDto(long Id, string Protocolo, string Numero, string? Nome, string? Motivo, string Texto, long? ArtigoId, string Login, DateTime Em);
public sealed record NovaAnotacaoDto(string? Protocolo, string Numero, string? Nome, string? Motivo, string Texto, long? ArtigoId);

/// <summary>Legenda ao vivo: Papel = "atendente" ou "interlocutor". Final = false quando ainda pode mudar.</summary>
public sealed record LegendaTrechoDto(string Papel, string Texto, double Inicio, double Fim, bool Final);
public sealed record LegendaRespostaDto(LegendaTrechoDto[] Trechos, string? Erro);
/// <summary>O que está ligado no servidor para o atendimento: legenda (precisa do whisper), resposta por IA externa.</summary>
public sealed record CopilotoEstadoDto(bool Legenda, bool RespostaIa, bool Transcricao, bool Ia);

public sealed record PerguntaCopilotoDto(string Pergunta, string[] Contexto, string? ChamadaId);
public sealed record FonteCopilotoDto(long ArtigoId, string Titulo);
public sealed record RespostaCopilotoDto(bool EhPergunta, string Pergunta, bool Encontrou, string? Resposta, FonteCopilotoDto[] Fontes, bool Divergente, string? Aviso);

public sealed record AuditoriaDto(long Id, DateTime Em, string Usuario, string Acao, string Alvo, int Status, string? Detalhe);

/// <summary>Visão geral do painel: Telefonia, Atendimento e Sistema no período escolhido (hoje, 7d, 30d).</summary>
public sealed record ResumoOperacionalDto(string Periodo, DateTime Gerado, int Recebidas, int Realizadas, int Perdidas, int Recusadas, int Falhas, int Transferidas,
    double? TempoMedioSegundos, int ChamadasTotal, int RamaisOnline, int Cadastrados, int ErrosRecentes,
    int ArtigosPublicados, int Consultas, int Anotacoes, int Perguntas, int PerguntasSemResposta, bool HaDadosDeChamadas);
