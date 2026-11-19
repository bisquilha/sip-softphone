using Softphone.Contratos;
using Microsoft.Data.Sqlite;

namespace Softphone.Hub;

/// <summary>Armazenamento do Hub em SQLite (arquivo único em Dados/hub.db, modo WAL). ~500 usuários cabem com folga.</summary>
public sealed partial class Banco
{
    private readonly string _cs;

    public Banco(OpcoesHub op)
    {
        Directory.CreateDirectory(op.Dados);
        _cs = new SqliteConnectionStringBuilder { DataSource = Path.Combine(op.Dados, "hub.db"), Cache = SqliteCacheMode.Shared, Pooling = true }.ToString();
        using var c = Abrir();
        Exec(c, """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS pessoas(
              login TEXT PRIMARY KEY COLLATE NOCASE, nome TEXT NOT NULL, setor TEXT, unidade TEXT, email TEXT,
              ramal TEXT, visto_em TEXT, recado TEXT, ativo INTEGER NOT NULL DEFAULT 1);
            CREATE TABLE IF NOT EXISTS conversas(
              id INTEGER PRIMARY KEY AUTOINCREMENT, grupo INTEGER NOT NULL, titulo TEXT, chave_direta TEXT UNIQUE, criada_em TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS membros(
              conversa INTEGER NOT NULL, login TEXT NOT NULL COLLATE NOCASE, ultima_lida INTEGER NOT NULL DEFAULT 0,
              PRIMARY KEY(conversa, login));
            CREATE INDEX IF NOT EXISTS ix_membros_login ON membros(login);
            CREATE TABLE IF NOT EXISTS mensagens(
              id INTEGER PRIMARY KEY AUTOINCREMENT, conversa INTEGER NOT NULL, autor TEXT NOT NULL COLLATE NOCASE,
              texto TEXT NOT NULL, arquivo_id TEXT, enviada_em TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_mensagens_conversa ON mensagens(conversa, id);
            CREATE TABLE IF NOT EXISTS arquivos(
              id TEXT PRIMARY KEY, nome TEXT NOT NULL, tamanho INTEGER NOT NULL, tipo TEXT NOT NULL, dono TEXT NOT NULL, criado_em TEXT NOT NULL);
            """);
        // colunas acrescentadas depois da primeira versão (bancos antigos ganham na hora)
        if (!TemColuna(c, "conversas", "criador")) Exec(c, "ALTER TABLE conversas ADD COLUMN criador TEXT");
        if (!TemColuna(c, "membros", "oculta")) Exec(c, "ALTER TABLE membros ADD COLUMN oculta INTEGER NOT NULL DEFAULT 0");
        if (!TemColuna(c, "pessoas", "ramal_ad")) Exec(c, "ALTER TABLE pessoas ADD COLUMN ramal_ad TEXT");
        CriarTabelasIa(c);
        CriarTabelasV11(c);
    }

    /// <summary>Gravações de ligações (IA). Áudio em Dados/gravacoes/&lt;id&gt;.wav; apagadas após a retenção.</summary>
    private static void CriarTabelasIa(SqliteConnection c) => Exec(c, """
        CREATE TABLE IF NOT EXISTS gravacoes(
          id INTEGER PRIMARY KEY AUTOINCREMENT, dono TEXT NOT NULL COLLATE NOCASE, numero TEXT NOT NULL, nome TEXT,
          direcao TEXT NOT NULL, inicio TEXT NOT NULL, duracao INTEGER NOT NULL, status TEXT NOT NULL, erro TEXT,
          segmentos TEXT, resumo TEXT, assunto TEXT, criada_em TEXT NOT NULL);
        CREATE INDEX IF NOT EXISTS ix_gravacoes_dono ON gravacoes(dono, id);
        """);

    // ======================= gravações (IA) =======================

    public sealed record Gravacao(long Id, string Dono, string Numero, string? Nome, string Direcao, DateTime Inicio, int Duracao,
        string Status, string? Erro, string? Segmentos, string? Resumo, string? Assunto, DateTime CriadaEm);

    private const string SelGravacao = "SELECT id, dono, numero, nome, direcao, inicio, duracao, status, erro, segmentos, resumo, assunto, criada_em FROM gravacoes";

    private static Gravacao LerGravacao(SqliteDataReader r) => new(r.GetInt64(0), r.GetString(1), r.GetString(2), S(r, 3), r.GetString(4),
        Data(r.GetString(5)), r.GetInt32(6), r.GetString(7), S(r, 8), S(r, 9), S(r, 10), S(r, 11), Data(r.GetString(12)));

    public long InserirGravacao(string dono, string numero, string? nome, string direcao, DateTime inicio, int duracao, string status = StatusGravacao.Fila)
    {
        using var c = Abrir();
        Exec(c, """
            INSERT INTO gravacoes(dono, numero, nome, direcao, inicio, duracao, status, criada_em)
            VALUES($d, $n, $no, $di, $i, $du, $s, $c)
            """, ("$d", dono), ("$n", numero), ("$no", nome), ("$di", direcao), ("$i", inicio.ToUniversalTime().ToString("O")),
            ("$du", duracao), ("$s", status), ("$c", Agora()));
        return Escalar<long>(c, "SELECT last_insert_rowid()");
    }

    public Gravacao? ObterGravacao(long id)
    {
        using var c = Abrir();
        using var cmd = c.CreateCommand();
        cmd.CommandText = SelGravacao + " WHERE id=$i";
        cmd.Parameters.AddWithValue("$i", id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? LerGravacao(r) : null;
    }

    public List<Gravacao> GravacoesDe(string dono, int limite = 300)
    {
        using var c = Abrir();
        using var cmd = c.CreateCommand();
        cmd.CommandText = SelGravacao + " WHERE dono=$d ORDER BY id DESC LIMIT $l";
        cmd.Parameters.AddWithValue("$d", dono);
        cmd.Parameters.AddWithValue("$l", limite);
        using var r = cmd.ExecuteReader();
        var l = new List<Gravacao>();
        while (r.Read()) l.Add(LerGravacao(r));
        return l;
    }

    /// <summary>Ids que ficaram no meio do caminho (o Hub reiniciou): voltam para a fila.</summary>
    public List<long> GravacoesPendentes()
    {
        using var c = Abrir();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id FROM gravacoes WHERE status IN ($a, $b, $c) ORDER BY id";
        cmd.Parameters.AddWithValue("$a", StatusGravacao.Fila);
        cmd.Parameters.AddWithValue("$b", StatusGravacao.Transcrevendo);
        cmd.Parameters.AddWithValue("$c", StatusGravacao.Resumindo);
        using var r = cmd.ExecuteReader();
        var l = new List<long>();
        while (r.Read()) l.Add(r.GetInt64(0));
        return l;
    }

    public List<long> GravacoesCriadasAntesDe(DateTime limiteUtc)
    {
        using var c = Abrir();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id FROM gravacoes WHERE criada_em < $l";
        cmd.Parameters.AddWithValue("$l", limiteUtc.ToString("O"));
        using var r = cmd.ExecuteReader();
        var l = new List<long>();
        while (r.Read()) l.Add(r.GetInt64(0));
        return l;
    }

    public void StatusDaGravacao(long id, string status, string? erro = null)
    {
        using var c = Abrir();
        Exec(c, "UPDATE gravacoes SET status=$s, erro=$e WHERE id=$i", ("$s", status), ("$e", erro), ("$i", id));
    }

    public void SalvarTranscricao(long id, string segmentosJson)
    {
        using var c = Abrir();
        Exec(c, "UPDATE gravacoes SET segmentos=$s WHERE id=$i", ("$s", segmentosJson), ("$i", id));
    }

    public void SalvarResumo(long id, string resumoJson, string? assunto)
    {
        using var c = Abrir();
        Exec(c, "UPDATE gravacoes SET resumo=$r, assunto=$a WHERE id=$i", ("$r", resumoJson), ("$a", assunto), ("$i", id));
    }

    public void ExcluirGravacao(long id)
    {
        using var c = Abrir();
        Exec(c, "DELETE FROM gravacoes WHERE id=$i", ("$i", id));
    }

    private static bool TemColuna(SqliteConnection c, string tabela, string coluna)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{tabela}') WHERE name=$n";
        cmd.Parameters.AddWithValue("$n", coluna);
        return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
    }

    private SqliteConnection Abrir()
    {
        var c = new SqliteConnection(_cs);
        c.Open();
        return c;
    }

    private static void Exec(SqliteConnection c, string sql, params (string, object?)[] p)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in p) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private static T? Escalar<T>(SqliteConnection c, string sql, params (string, object?)[] p)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in p) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        var r = cmd.ExecuteScalar();
        return r is null or DBNull ? default : (T)Convert.ChangeType(r, typeof(T));
    }

    private static string Agora() => DateTime.UtcNow.ToString("O");
    private static DateTime Data(string s) => DateTime.Parse(s, null, System.Globalization.DateTimeStyles.RoundtripKind);
    private static string? S(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    // ======================= pessoas =======================

    public sealed record Pessoa(string Login, string Nome, string? Setor, string? Unidade, string? Email, string? Ramal, DateTime? VistoEm, string? Recado);

    /// <summary>Sincronização com o AD: atualiza nome/setor/unidade/e-mail/ramal do AD; quem não veio fica inativo.
    /// O ramal aprendido pelo Softphone (coluna ramal) é preservado e vale mais que o do AD.</summary>
    public int SincronizarPessoas(IReadOnlyCollection<PessoaAd> doAd)
    {
        using var c = Abrir();
        using var tx = c.BeginTransaction();
        Exec(c, "UPDATE pessoas SET ativo = 0");
        foreach (var p in doAd)
        {
            Exec(c, """
                INSERT INTO pessoas(login, nome, setor, unidade, email, ramal_ad, ativo) VALUES($l, $n, $s, $u, $e, $r, 1)
                ON CONFLICT(login) DO UPDATE SET nome=$n, setor=$s, unidade=$u, email=$e, ramal_ad=$r, ativo=1
                """, ("$l", p.Login), ("$n", p.Nome), ("$s", p.Setor), ("$u", p.Unidade), ("$e", p.Email), ("$r", p.Ramal));
        }
        tx.Commit();
        return doAd.Count;
    }

    /// <summary>
    /// Ramal que aparece para todos (e que o chat disca). 1.3.1: o do AD (ramal de USO, ipPhone) manda, como decidido em 10/11
    /// ("AD substitui"); o aprendido pelo Softphone só vale quando o AD não tem ramal. Antes o aprendido vinha primeiro e apps antigos
    /// (que informam o ramal de LOGIN) faziam o chat discar o número errado: "Não autorizado pelo PABX".
    /// </summary>
    private const string SelPessoa = """
        SELECT p.login, p.nome, p.setor, p.unidade, p.email,
               CASE WHEN p.ramal_ad IS NOT NULL AND NOT EXISTS(SELECT 1 FROM pessoas q WHERE q.ramal = p.ramal_ad AND q.login <> p.login)
                    THEN p.ramal_ad ELSE p.ramal END,
               p.visto_em, p.recado
        FROM pessoas p
        """;

    /// <summary>Para a TI conferir e gravar no AD os ramais que o Softphone aprendeu.</summary>
    public sealed record RamalAdmin(string Login, string Nome, string? RamalAprendido, string? RamalAd, bool Ativo);

    public List<RamalAdmin> ListarRamais()
    {
        using var c = Abrir();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT login, nome, ramal, ramal_ad, ativo FROM pessoas WHERE ramal IS NOT NULL OR ramal_ad IS NOT NULL ORDER BY nome COLLATE NOCASE";
        using var r = cmd.ExecuteReader();
        var l = new List<RamalAdmin>();
        while (r.Read()) l.Add(new(r.GetString(0), r.GetString(1), S(r, 2), S(r, 3), r.GetInt64(4) == 1));
        return l;
    }

    /// <summary>Só para o modo de teste (simula o ipPhone vindo do AD).</summary>
    public void DefinirRamalAd(string login, string? ramal)
    {
        using var c = Abrir();
        Exec(c, "UPDATE pessoas SET ramal_ad=$r WHERE login=$l", ("$r", string.IsNullOrWhiteSpace(ramal) ? null : ramal.Trim()), ("$l", login));
    }

    /// <summary>Garante a pessoa (login que ainda não veio na sincronização, ex.: usuário novo ou modo de teste).</summary>
    public void GarantirPessoa(string login, string nome, string? setor = null, string? unidade = null, string? email = null)
    {
        using var c = Abrir();
        Exec(c, """
            INSERT INTO pessoas(login, nome, setor, unidade, email, ativo) VALUES($l, $n, $s, $u, $e, 1)
            ON CONFLICT(login) DO UPDATE SET ativo=1
            """, ("$l", login), ("$n", nome), ("$s", setor), ("$u", unidade), ("$e", email));
    }

    /// <summary>Só para o modo de teste (simula o department vindo do AD).</summary>
    public void DefinirSetorParaTeste(string login, string? setor)
    {
        using var c = Abrir();
        Exec(c, "UPDATE pessoas SET setor=$s WHERE login=$l", ("$s", string.IsNullOrWhiteSpace(setor) ? null : setor.Trim()), ("$l", login));
    }

    /// <summary>A última sincronização com o AD viu esta pessoa (ou ela entrou depois disso).</summary>
    public bool PessoaAtiva(string login)
    {
        using var c = Abrir();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT ativo FROM pessoas WHERE login=$l";
        cmd.Parameters.AddWithValue("$l", login);
        return cmd.ExecuteScalar() is long a && a == 1;
    }

    public Pessoa? ObterPessoa(string login)
    {
        using var c = Abrir();
        using var cmd = c.CreateCommand();
        cmd.CommandText = SelPessoa + " WHERE p.login=$l";
        cmd.Parameters.AddWithValue("$l", login);
        using var r = cmd.ExecuteReader();
        return r.Read() ? LerPessoa(r) : null;
    }

    public List<Pessoa> ListarPessoas()
    {
        using var c = Abrir();
        using var cmd = c.CreateCommand();
        cmd.CommandText = SelPessoa + " WHERE p.ativo=1 ORDER BY p.nome COLLATE NOCASE";
        using var r = cmd.ExecuteReader();
        var lista = new List<Pessoa>();
        while (r.Read()) lista.Add(LerPessoa(r));
        return lista;
    }

    private static Pessoa LerPessoa(SqliteDataReader r) =>
        new(r.GetString(0), r.GetString(1), S(r, 2), S(r, 3), S(r, 4), S(r, 5), r.IsDBNull(6) ? null : Data(r.GetString(6)), S(r, 7));

    public void DefinirRamal(string login, string? ramal)
    {
        using var c = Abrir();
        // um ramal pertence a uma pessoa só: quem tinha esse ramal antes perde (troca de mesa/ramal)
        if (!string.IsNullOrWhiteSpace(ramal)) Exec(c, "UPDATE pessoas SET ramal=NULL WHERE ramal=$r AND login<>$l", ("$r", ramal), ("$l", login));
        Exec(c, "UPDATE pessoas SET ramal=$r WHERE login=$l", ("$r", string.IsNullOrWhiteSpace(ramal) ? null : ramal.Trim()), ("$l", login));
    }

    public void DefinirRecado(string login, string? recado)
    {
        using var c = Abrir();
        Exec(c, "UPDATE pessoas SET recado=$r WHERE login=$l", ("$r", string.IsNullOrWhiteSpace(recado) ? null : recado.Trim()), ("$l", login));
    }

    public void MarcarVisto(string login)
    {
        using var c = Abrir();
        Exec(c, "UPDATE pessoas SET visto_em=$v WHERE login=$l", ("$v", Agora()), ("$l", login));
    }

    // ======================= conversas =======================

    public bool EhMembro(long conversa, string login)
    {
        using var c = Abrir();
        return Escalar<long>(c, "SELECT COUNT(*) FROM membros WHERE conversa=$c AND login=$l", ("$c", conversa), ("$l", login)) > 0;
    }

    public string[] Membros(long conversa)
    {
        using var c = Abrir();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT login FROM membros WHERE conversa=$c ORDER BY login";
        cmd.Parameters.AddWithValue("$c", conversa);
        using var r = cmd.ExecuteReader();
        var l = new List<string>();
        while (r.Read()) l.Add(r.GetString(0));
        return l.ToArray();
    }

    public long ObterOuCriarDireta(string a, string b)
    {
        var par = new[] { a.ToLowerInvariant(), b.ToLowerInvariant() }.OrderBy(x => x, StringComparer.Ordinal).ToArray();
        string chave = par[0] + "|" + par[1];
        using var c = Abrir();
        var id = Escalar<long>(c, "SELECT id FROM conversas WHERE chave_direta=$k", ("$k", chave));
        if (id > 0) return id;
        using var tx = c.BeginTransaction();
        Exec(c, "INSERT INTO conversas(grupo, chave_direta, criada_em) VALUES(0, $k, $t)", ("$k", chave), ("$t", Agora()));
        id = Escalar<long>(c, "SELECT last_insert_rowid()");
        foreach (var l in par.Distinct()) Exec(c, "INSERT OR IGNORE INTO membros(conversa, login) VALUES($c, $l)", ("$c", id), ("$l", l));
        tx.Commit();
        return id;
    }

    public long CriarGrupo(string titulo, IEnumerable<string> logins, string criador)
    {
        using var c = Abrir();
        using var tx = c.BeginTransaction();
        Exec(c, "INSERT INTO conversas(grupo, titulo, criada_em, criador) VALUES(1, $t, $d, $k)", ("$t", titulo.Trim()), ("$d", Agora()), ("$k", criador.ToLowerInvariant()));
        var id = Escalar<long>(c, "SELECT last_insert_rowid()");
        foreach (var l in logins.Select(x => x.ToLowerInvariant()).Distinct())
            Exec(c, "INSERT OR IGNORE INTO membros(conversa, login) VALUES($c, $l)", ("$c", id), ("$l", l));
        tx.Commit();
        return id;
    }

    public ConversaDto? Conversa(long id, string paraQuem)
    {
        using var c = Abrir();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, grupo, titulo, criador FROM conversas WHERE id=$c";
        cmd.Parameters.AddWithValue("$c", id);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return MontarConversa(c, r.GetInt64(0), r.GetInt64(1) == 1, S(r, 2), S(r, 3), paraQuem);
    }

    public List<ConversaDto> ConversasDe(string login)
    {
        using var c = Abrir();
        var ids = new List<(long, bool, string?, string?)>();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT c.id, c.grupo, c.titulo, c.criador FROM conversas c JOIN membros m ON m.conversa=c.id WHERE m.login=$l AND m.oculta=0";
            cmd.Parameters.AddWithValue("$l", login);
            using var r = cmd.ExecuteReader();
            while (r.Read()) ids.Add((r.GetInt64(0), r.GetInt64(1) == 1, S(r, 2), S(r, 3)));
        }
        return ids.Select(x => MontarConversa(c, x.Item1, x.Item2, x.Item3, x.Item4, login))
                  .OrderByDescending(x => x.Ultima?.EnviadaEm ?? DateTime.MinValue).ToList();
    }

    private ConversaDto MontarConversa(SqliteConnection c, long id, bool grupo, string? titulo, string? criador, string paraQuem)
    {
        var membros = new List<(string Login, long UltimaLida)>();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT login, ultima_lida FROM membros WHERE conversa=$c";
            cmd.Parameters.AddWithValue("$c", id);
            using var r = cmd.ExecuteReader();
            while (r.Read()) membros.Add((r.GetString(0), r.GetInt64(1)));
        }
        var minha = membros.FirstOrDefault(m => string.Equals(m.Login, paraQuem, StringComparison.OrdinalIgnoreCase)).UltimaLida;
        var naoLidas = (int)Escalar<long>(c, "SELECT COUNT(*) FROM mensagens WHERE conversa=$c AND id>$u AND autor<>$l",
            ("$c", id), ("$u", minha), ("$l", paraQuem));
        var outros = membros.Where(m => !string.Equals(m.Login, paraQuem, StringComparison.OrdinalIgnoreCase)).ToList();
        long lidaPorTodos = outros.Count == 0 ? 0 : outros.Min(m => m.UltimaLida);
        var ultima = Mensagens(c, id, long.MaxValue, 1).FirstOrDefault();
        return new ConversaDto(id, grupo, titulo, membros.Select(m => m.Login).OrderBy(x => x).ToArray(), ultima, naoLidas, lidaPorTodos, criador);
    }

    public void Ocultar(long conversa, string login)
    {
        using var c = Abrir();
        Exec(c, "UPDATE membros SET oculta=1 WHERE conversa=$c AND login=$l", ("$c", conversa), ("$l", login));
    }

    /// <summary>Desfaz o ocultar (quando a própria pessoa reabre a conversa).</summary>
    public void Mostrar(long conversa, string login)
    {
        using var c = Abrir();
        Exec(c, "UPDATE membros SET oculta=0 WHERE conversa=$c AND login=$l", ("$c", conversa), ("$l", login));
    }

    public void Sair(long conversa, string login)
    {
        using var c = Abrir();
        Exec(c, "DELETE FROM membros WHERE conversa=$c AND login=$l", ("$c", conversa), ("$l", login));
    }

    public string? CriadorDe(long conversa)
    {
        using var c = Abrir();
        return Escalar<string>(c, "SELECT criador FROM conversas WHERE id=$c", ("$c", conversa));
    }

    /// <summary>Apaga a conversa inteira (mensagens, membros e registro dos arquivos). Retorna os ids dos arquivos para apagar do disco.</summary>
    public List<string> Excluir(long conversa)
    {
        using var c = Abrir();
        var arquivos = new List<string>();
        using (var cmd = c.CreateCommand())
        {
            // só arquivos que não foram mandados em nenhuma outra conversa
            cmd.CommandText = """
                SELECT DISTINCT arquivo_id FROM mensagens WHERE conversa=$c AND arquivo_id IS NOT NULL
                AND arquivo_id NOT IN (SELECT arquivo_id FROM mensagens WHERE conversa<>$c AND arquivo_id IS NOT NULL)
                """;
            cmd.Parameters.AddWithValue("$c", conversa);
            using var r = cmd.ExecuteReader();
            while (r.Read()) arquivos.Add(r.GetString(0));
        }
        using var tx = c.BeginTransaction();
        Exec(c, "DELETE FROM reacoes WHERE mensagem IN (SELECT id FROM mensagens WHERE conversa=$c)", ("$c", conversa));
        Exec(c, "DELETE FROM mensagens WHERE conversa=$c", ("$c", conversa));
        Exec(c, "DELETE FROM membros WHERE conversa=$c", ("$c", conversa));
        Exec(c, "DELETE FROM conversas WHERE id=$c", ("$c", conversa));
        foreach (var a in arquivos) Exec(c, "DELETE FROM arquivos WHERE id=$i", ("$i", a));
        tx.Commit();
        return arquivos;
    }

    // ======================= mensagens =======================

    public MensagemDto Inserir(long conversa, string autor, string texto, string? arquivoId, long? respostaA = null, string[]? mencoes = null)
    {
        using var c = Abrir();
        var t = Agora();
        Exec(c, "INSERT INTO mensagens(conversa, autor, texto, arquivo_id, enviada_em, resposta_a, mencoes) VALUES($c, $a, $x, $f, $t, $r, $m)",
            ("$c", conversa), ("$a", autor), ("$x", texto), ("$f", arquivoId), ("$t", t), ("$r", respostaA),
            ("$m", mencoes is { Length: > 0 } ? string.Join(",", mencoes) : null));
        var id = Escalar<long>(c, "SELECT last_insert_rowid()");
        Exec(c, "UPDATE membros SET oculta=0 WHERE conversa=$c AND oculta=1", ("$c", conversa));
        // quem escreve leu tudo até aqui
        Exec(c, "UPDATE membros SET ultima_lida=$u WHERE conversa=$c AND login=$l", ("$u", id), ("$c", conversa), ("$l", autor));
        return Mensagem(c, id)!;
    }

    public List<MensagemDto> Historico(long conversa, long antesDe, int quantidade)
    {
        using var c = Abrir();
        var l = Mensagens(c, conversa, antesDe <= 0 ? long.MaxValue : antesDe, Math.Clamp(quantidade, 1, 200));
        l.Reverse(); // do mais antigo para o mais novo
        return l;
    }

    private List<MensagemDto> Mensagens(SqliteConnection c, long conversa, long antesDe, int quantidade)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = SelMensagem + " WHERE m.conversa=$c AND m.id<$a ORDER BY m.id DESC LIMIT $q";
        cmd.Parameters.AddWithValue("$c", conversa);
        cmd.Parameters.AddWithValue("$a", antesDe);
        cmd.Parameters.AddWithValue("$q", quantidade);
        return LerMensagens(c, cmd);
    }

    public void MarcarLida(long conversa, string login, long ultimaId)
    {
        using var c = Abrir();
        Exec(c, "UPDATE membros SET ultima_lida=MAX(ultima_lida, $u) WHERE conversa=$c AND login=$l", ("$u", ultimaId), ("$c", conversa), ("$l", login));
    }

    // ======================= arquivos =======================

    public ArquivoDto RegistrarArquivo(string id, string nome, long tamanho, string tipo, string dono)
    {
        using var c = Abrir();
        Exec(c, "INSERT INTO arquivos(id, nome, tamanho, tipo, dono, criado_em) VALUES($i, $n, $t, $p, $d, $c)",
            ("$i", id), ("$n", nome), ("$t", tamanho), ("$p", tipo), ("$d", dono), ("$c", Agora()));
        return new ArquivoDto(id, nome, tamanho, tipo);
    }

    public ArquivoDto? Arquivo(string id)
    {
        using var c = Abrir();
        return Arquivo(c, id);
    }

    private static ArquivoDto? Arquivo(SqliteConnection c, string id)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, nome, tamanho, tipo FROM arquivos WHERE id=$i";
        cmd.Parameters.AddWithValue("$i", id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? new ArquivoDto(r.GetString(0), r.GetString(1), r.GetInt64(2), r.GetString(3)) : null;
    }

    /// <summary>Pode baixar: quem enviou, ou quem é membro de uma conversa onde o arquivo foi mandado.</summary>
    public bool PodeBaixar(string arquivoId, string login)
    {
        using var c = Abrir();
        if (Escalar<long>(c, "SELECT COUNT(*) FROM arquivos WHERE id=$i AND dono=$l", ("$i", arquivoId), ("$l", login)) > 0) return true;
        return Escalar<long>(c, """
            SELECT COUNT(*) FROM mensagens m JOIN membros b ON b.conversa=m.conversa
            WHERE m.arquivo_id=$i AND b.login=$l
            """, ("$i", arquivoId), ("$l", login)) > 0;
    }

    public bool ArquivoEhDe(string arquivoId, string login)
    {
        using var c = Abrir();
        return Escalar<long>(c, "SELECT COUNT(*) FROM arquivos WHERE id=$i AND dono=$l", ("$i", arquivoId), ("$l", login)) > 0;
    }
}
