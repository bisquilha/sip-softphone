using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Softphone.Core;
using Microsoft.Win32;

namespace Softphone.App;

/// <summary>Configurações do usuário (%APPDATA%\Softphone\config.json). A senha do ramal vai criptografada com DPAPI (só este usuário do Windows abre).</summary>
public sealed class ConfigApp
{
    public ContaSip Conta { get; set; } = new();
    public string? SenhaProtegida { get; set; }
    /// <summary>1.1.8: ramal e senha vieram do AD (a TI cadastrou) e se atualizam sozinhos; a tela de Configurações os mostra travados.</summary>
    public bool ContaGerenciadaAd { get; set; }
    /// <summary>1.1.9: ramal de USO que a TI cadastrou no AD (o que se disca e aparece no diretório). Diferente do ramal de LOGIN, que registra no PABX
    /// (Conta.Usuario). Só vale com a conta gerenciada pelo AD.</summary>
    public string? RamalDeUso { get; set; }
    public string? Microfone { get; set; }
    public string? AltoFalante { get; set; }
    public string? Campainha { get; set; }
    public int VolumeCampainha { get; set; } = 60;
    public bool IniciarComWindows { get; set; } = true;
    public bool NaoPerturbe { get; set; }
    public bool LogSip { get; set; }
    public bool AvisoBandejaMostrado { get; set; }
    public List<string> Bloqueados { get; set; } = new();
    public double? JanelaEsquerda { get; set; }
    public double? JanelaTopo { get; set; }

    // ---- Hub (chat, presença, diretório) ----
    /// <summary>Endereço do servidor do chat (ex.: https://softphone.corp.example.com). Vazio = chat desligado.</summary>
    public string? HubUrl { get; set; }
    /// <summary>Token do Hub criptografado (DPAPI), para reconectar sem pedir senha quando o login automático não der.</summary>
    public string? HubTokenProtegido { get; set; }
    public DateTime HubTokenExpira { get; set; }
    /// <summary>Presença escolhida pela pessoa (Disponível, Ausente, Não perturbe). "Em ligação" é automático.</summary>
    public Softphone.Contratos.EstadoPresenca PresencaEscolhida { get; set; } = Softphone.Contratos.EstadoPresenca.Disponivel;
    public string? Recado { get; set; }
    /// <summary>Fica "Ausente" sozinho depois de N minutos sem mexer no PC (0 = nunca).</summary>
    public int AusenteAposMinutos { get; set; } = 10;
    public bool NotificarMensagens { get; set; } = true;
    public bool SomMensagem { get; set; } = true;

    // ---- aparência ----
    /// <summary>"Sistema" (segue o Windows), "Claro" ou "Escuro".</summary>
    public string Tema { get; set; } = "Sistema";
    /// <summary>1.2.10: cor (#RRGGBB) do tema "Personalizado", escolhida na tabela de cores.</summary>
    public string? CorTema { get; set; }
    /// <summary>1.2.11: últimos emojis usados (reações e mensagens), do mais novo para o mais antigo.</summary>
    public List<string> EmojisRecentes { get; set; } = new();
    /// <summary>Aviso de chamada no canto de cima (true) ou de baixo (false) da tela.</summary>
    public bool AvisoNoTopo { get; set; }

    // ---- áudio ----
    public string? ToqueArquivo { get; set; }
    /// <summary>1.3: toque por tipo de chamada: id de toque pronto (Toques.Presets) ou caminho de .wav; vazio = toque padrão acima.</summary>
    /// <summary>1.2.7: toque da chamada recebida (id de Toques.Presets; vazio = clássico). Vale para os tipos que não têm toque próprio.</summary>
    public string? ToquePadrao { get; set; }
    /// <summary>1.2.7: som das teclas do discador (id de Toques.SonsTecla; vazio = tons de telefone).</summary>
    public string? SomTecla { get; set; }
    public string? ToqueInterno { get; set; }
    public string? ToqueExterno { get; set; }
    public string? ToqueFila { get; set; }
    /// <summary>1.3: números (ou ramais) de fila ou setor, separados por vírgula: tocam o "toque de fila ou setor".</summary>
    public string? NumerosFila { get; set; }
    /// <summary>1.3: painel lateral de artigos e anotações durante a ligação (aberto/recolhido).</summary>
    public bool PainelAtendimentoAberto { get; set; } = true;
    /// <summary>Toca o aviso falado "esta ligação está sendo gravada" ao gravar. Desligado desde 10/11/2026 (pedido do Ana).</summary>
    public bool AvisoFaladoGravacao { get; set; } = false;
    /// <summary>1.3: atalhos globais trocados pela pessoa (chave da ação -> "Ctrl+Alt+X"); o que não está aqui usa o padrão.</summary>
    public Dictionary<string, string>? Atalhos { get; set; }
    /// <summary>1.3: até 3 ramais/números favoritos para os atalhos "Ligar para o favorito".</summary>
    public string[]? Favoritos { get; set; }
    /// <summary>1.2.4: botões da aba Atalhos (discagem rápida, conversa, transferir, tons, link, status). Validação em Core\AtalhosTela.cs.</summary>
    public List<AtalhoTela>? BotoesAtalho { get; set; }
    public List<string> Codecs { get; set; } = new() { "PCMA", "PCMU", "G722" };
    /// <summary>Ganho do microfone em % (100 = normal).</summary>
    public int GanhoMicrofone { get; set; } = 100;
    /// <summary>Volume da conversa em % (100 = normal).</summary>
    public int VolumeConversa { get; set; } = 100;
    /// <summary>Cancelamento de eco (WebRTC). Ligado por padrão: com headset não atrapalha.</summary>
    public bool CancelarEco { get; set; } = true;
    /// <summary>Redução de ruído: Desligado, Baixo, Moderado, Alto, MuitoAlto.</summary>
    public string? ReducaoRuido { get; set; } = "Moderado";
    /// <summary>Volume automático do microfone (AGC).</summary>
    public bool VolumeAutomatico { get; set; }

    // ---- clicar para ligar (links tel:/sip:/callto:) ----
    public bool AbrirLinksTelefone { get; set; } = true;
    /// <summary>Ao clicar num link: liga na hora (true) ou só preenche o discador.</summary>
    public bool LinkDiscaDireto { get; set; } = true;
    /// <summary>Prefixo para números externos vindos de links (ex.: "0"). Vazio = disca como está (sem o +55).</summary>
    public string? PrefixoExterno { get; set; }

    // ---- chamadas ----
    public bool AtenderAutomatico { get; set; }
    /// <summary>1.1.3: toda ligação atendida começa a gravar sozinha (com o aviso).</summary>
    public bool GravarAutomatico { get; set; }
    /// <summary>1.1.5: transcrever e resumir toda gravação sozinho. Desligado = só guarda o áudio; transcreve pelo botão em Gravações.</summary>
    public bool TranscreverAutomatico { get; set; }
    public int AtenderAutomaticoSegundos { get; set; } = 3;
    /// <summary>Atender sozinho só ramais internos (até 5 dígitos).</summary>
    public bool AtenderAutomaticoSoInternos { get; set; } = true;
    public string? EncaminharSempre { get; set; }
    public string? EncaminharSeOcupado { get; set; }
    public string? EncaminharSeNaoAtender { get; set; }
    public int EncaminharSegundos { get; set; } = 20;
    /// <summary>Atalhos de teclado em qualquer janela: Ctrl+Alt+A atende, Ctrl+Alt+D desliga, Ctrl+Alt+M mudo.</summary>
    public bool AtalhosGlobais { get; set; } = true;

    // ---- rede ----
    public int RtpPortaInicial { get; set; }
    public int RtpPortaFinal { get; set; }
    public double? ChatLargura { get; set; }
    public double? ChatAltura { get; set; }

    /// <summary>Acesso à senha em texto (só em memória). NUNCA serializar: no arquivo fica só SenhaProtegida.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string Senha
    {
        get => Armazenamento.Abrir(SenhaProtegida);
        set => SenhaProtegida = Armazenamento.Proteger(value);
    }
}

public sealed class RegistroHistorico
{
    public DateTime Data { get; set; }
    public DirecaoChamada Direcao { get; set; }
    public string Numero { get; set; } = "";
    public string? Nome { get; set; }
    public int DuracaoSegundos { get; set; }
    public ResultadoChamada Resultado { get; set; }
    public string? Motivo { get; set; }
    /// <summary>Qualidade da ligação (nota 1-4,5, perda %, variação ms); null = não medida.</summary>
    public double? Mos { get; set; }
    public double? PerdaPct { get; set; }
    public double? JitterMs { get; set; }
    public bool? Conferencia { get; set; }
}

public sealed class Contato
{
    public string Nome { get; set; } = "";
    public string Numero { get; set; } = "";
    public string? Setor { get; set; }
    /// <summary>1.3: empresa do contato (aparece no aviso de chamada recebida).</summary>
    public string? Empresa { get; set; }
}

internal static class Armazenamento
{
    /// <summary>%APPDATA%\Softphone. SOFTPHONE_DADOS troca a pasta (testes / suporte).</summary>
    public static string Pasta { get; } = ResolverPasta();

    /// <summary>1.2.1 (renome para Softphone): os dados passam de %APPDATA%\Softphone para %APPDATA%\Softphone na primeira abertura (move a pasta inteira; se não der, copia).</summary>
    private static string ResolverPasta()
    {
        if (Environment.GetEnvironmentVariable("SOFTPHONE_DADOS") is { Length: > 0 } p) return p;
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var nova = Path.Combine(appData, "Softphone"); var antiga = Path.Combine(appData, "Softphone");
        try
        {
            if (!Directory.Exists(nova) && Directory.Exists(antiga))
            {
                try { Directory.Move(antiga, nova); }
                catch
                {
                    foreach (var d in Directory.GetDirectories(antiga, "*", SearchOption.AllDirectories)) Directory.CreateDirectory(d.Replace(antiga, nova));
                    foreach (var f in Directory.GetFiles(antiga, "*", SearchOption.AllDirectories)) File.Copy(f, f.Replace(antiga, nova), true);
                }
            }
        }
        catch { }
        return nova;
    }
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static readonly byte[] Entropia = Encoding.UTF8.GetBytes("Softphone.v1");

    public static T Carregar<T>(string arquivo, Func<T> padrao)
    {
        try
        {
            var caminho = Path.Combine(Pasta, arquivo);
            if (File.Exists(caminho)) return JsonSerializer.Deserialize<T>(File.ReadAllText(caminho), Json) ?? padrao();
        }
        catch (Exception ex)
        {
            Log.Escrever($"Não foi possível ler {arquivo}: {ex.Message}");
        }
        return padrao();
    }

    public static void Salvar<T>(string arquivo, T valor)
    {
        try
        {
            Directory.CreateDirectory(Pasta);
            var caminho = Path.Combine(Pasta, arquivo);
            var temp = caminho + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(valor, Json));
            File.Move(temp, caminho, true);
        }
        catch (Exception ex)
        {
            Log.Escrever($"Não foi possível salvar {arquivo}: {ex.Message}");
        }
    }

    public static ConfigApp CarregarConfig()
    {
        var cfg = Carregar("config.json", () => new ConfigApp());
        // Primeira execução: puxa os padrões da TI (servidor do PABX) do softphone.padrao.json ao lado do .exe.
        if (string.IsNullOrWhiteSpace(cfg.Conta.Servidor))
        {
            try
            {
                var padrao = Path.Combine(AppContext.BaseDirectory, "softphone.padrao.json");
                if (File.Exists(padrao))
                {
                    var texto = File.ReadAllText(padrao);
                    var p = JsonSerializer.Deserialize<ContaSip>(texto, Json);
                    using (var doc = JsonDocument.Parse(texto))
                        if (cfg.HubUrl is null && doc.RootElement.TryGetProperty("Hub", out var hub)) cfg.HubUrl = hub.GetString();
                    if (p != null)
                    {
                        cfg.Conta.Servidor = p.Servidor;
                        cfg.Conta.Dominio = p.Dominio;
                        cfg.Conta.Proxy = p.Proxy;
                        cfg.Conta.Transporte = p.Transporte;
                        cfg.Conta.RegistroSegundos = p.RegistroSegundos;
                        cfg.Conta.ManterAtivoSegundos = p.ManterAtivoSegundos;
                    }
                }
            }
            catch (Exception ex) { Log.Escrever("softphone.padrao.json inválido: " + ex.Message); }
        }
        // Endereço do Hub (chat): quem já tinha configuração de antes também passa a receber o padrão da TI.
        if (cfg.HubUrl is null)
        {
            try
            {
                var padrao = Path.Combine(AppContext.BaseDirectory, "softphone.padrao.json");
                if (File.Exists(padrao))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(padrao));
                    if (doc.RootElement.TryGetProperty("Hub", out var hub)) cfg.HubUrl = hub.GetString();
                }
            }
            catch { }
        }
        // SOFTPHONE_HUB troca o endereço (testes/suporte)
        if (Environment.GetEnvironmentVariable("SOFTPHONE_HUB") is { Length: > 0 } hubEnv) cfg.HubUrl = hubEnv;
        // O nome de exibição padrão é o nome do usuário do Windows.
        if (string.IsNullOrWhiteSpace(cfg.Conta.NomeExibicao))
        {
            try { cfg.Conta.NomeExibicao = System.DirectoryServices.AccountManagement.UserPrincipal.Current?.DisplayName ?? ""; } catch { }
        }
        return cfg;
    }

    public static string? Proteger(string texto)
    {
        if (string.IsNullOrEmpty(texto)) return null;
        var dados = ProtectedData.Protect(Encoding.UTF8.GetBytes(texto), Entropia, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(dados);
    }

    public static string Abrir(string? protegido)
    {
        if (string.IsNullOrEmpty(protegido)) return "";
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(protegido), Entropia, DataProtectionScope.CurrentUser)); }
        catch { return ""; }
    }

    private const string ChaveRun = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static void AplicarInicioComWindows(bool ligar)
    {
        try
        {
            using var chave = Registry.CurrentUser.OpenSubKey(ChaveRun, writable: true);
            if (chave == null) return;
            chave.DeleteValue("Softphone", false);   // nome antigo (antes do renome para Softphone)
            if (ligar) chave.SetValue("Softphone", $"\"{Environment.ProcessPath}\" --minimizado");
            else chave.DeleteValue("Softphone", false);
        }
        catch (Exception ex) { Log.Escrever("Não foi possível ajustar o início com o Windows: " + ex.Message); }
    }

    /// <summary>Compara só os dígitos (ignora +55, traços, espaços).</summary>
    public static bool MesmoNumero(string a, string b)
    {
        string da = new(a.Where(char.IsDigit).ToArray()), db = new(b.Where(char.IsDigit).ToArray());
        if (da.Length == 0 || db.Length == 0) return string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
        if (da == db) return true;
        // 011 9xxxx-xxxx x 9xxxx-xxxx: compara pelos últimos 8 dígitos quando os dois são números longos
        return da.Length >= 8 && db.Length >= 8 && da[^8..] == db[^8..];
    }
}

/// <summary>Log em %APPDATA%\Softphone\logs (um arquivo por dia, guarda 14 dias).</summary>
internal static class Log
{
    private static readonly object Trava = new();
    private static readonly string PastaLogs = Path.Combine(Armazenamento.Pasta, "logs");

    public static void Escrever(string mensagem)
    {
        try
        {
            lock (Trava)
            {
                Directory.CreateDirectory(PastaLogs);
                File.AppendAllText(Path.Combine(PastaLogs, $"softphone-{DateTime.Now:yyyyMMdd}.log"), $"{DateTime.Now:HH:mm:ss.fff} {mensagem}{Environment.NewLine}");
            }
        }
        catch { }
    }

    public static void Limpar()
    {
        try
        {
            if (!Directory.Exists(PastaLogs)) return;
            foreach (var f in Directory.GetFiles(PastaLogs, "softphone-*.log"))
                if (File.GetLastWriteTime(f) < DateTime.Now.AddDays(-14)) File.Delete(f);
        }
        catch { }
    }
}
