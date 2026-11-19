using Softphone.Contratos;
using Microsoft.Data.Sqlite;

namespace Softphone.Hub;

/// <summary>1.1: citar/editar/apagar/menções/busca no chat, comunicados da TI, qualidade das ligações, configuração do painel.</summary>
public sealed partial class Banco
{
    private static void CriarTabelasV11(SqliteConnection c)
    {
        if (!TemColuna(c, "mensagens", "resposta_a")) Exec(c, "ALTER TABLE mensagens ADD COLUMN resposta_a INTEGER");
        if (!TemColuna(c, "mensagens", "editada_em")) Exec(c, "ALTER TABLE mensagens ADD COLUMN editada_em TEXT");
        if (!TemColuna(c, "mensagens", "apagada")) Exec(c, "ALTER TABLE mensagens ADD COLUMN apagada INTEGER NOT NULL DEFAULT 0");
        if (!TemColuna(c, "mensagens", "mencoes")) Exec(c, "ALTER TABLE mensagens ADD COLUMN mencoes TEXT");
        Exec(c, """
            CREATE TABLE IF NOT EXISTS qualidade(
              id INTEGER PRIMARY KEY AUTOINCREMENT, login TEXT NOT NULL COLLATE NOCASE, unidade TEXT, setor TEXT,
              numero TEXT, direcao TEXT, inicio TEXT NOT NULL, duracao INTEGER NOT NULL, codec TEXT,
              mos REAL NOT NULL, mos_min REAL, perda REAL NOT NULL, jitter REAL NOT NULL, rtt REAL, conferencia INTEGER NOT NULL DEFAULT 0,
              versao TEXT, criada_em TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_qualidade_inicio ON qualidade(inicio);
            CREATE TABLE IF NOT EXISTS comunicados(
              id INTEGER PRIMARY KEY AUTOINCREMENT, titulo TEXT NOT NULL, texto TEXT NOT NULL, nivel TEXT NOT NULL, unidade TEXT,
              autor TEXT NOT NULL, criado_em TEXT NOT NULL, expira_em TEXT NOT NULL, cancelado INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS comunicados_lidos(
              comunicado INTEGER NOT NULL, login TEXT NOT NULL COLLATE NOCASE, lido_em TEXT NOT NULL, PRIMARY KEY(comunicado, login));
            CREATE TABLE IF NOT EXISTS config(chave TEXT PRIMARY KEY, valor TEXT);
            CREATE TABLE IF NOT EXISTS clientes(
              login TEXT PRIMARY KEY COLLATE NOCASE, versao TEXT, pc TEXT, visto_em TEXT NOT NULL);
            """);
        // 1.3: "sem medição" (nenhum pacote chegou) deixa de contar como 100% de perda; sentido do áudio medido; id da chamada
        if (!TemColuna(c, "qualidade", "medido")) Exec(c, "ALTER TABLE qualidade ADD COLUMN medido INTEGER NOT NULL DEFAULT 1");
        if (!TemColuna(c, "qualidade", "sentido")) Exec(c, "ALTER TABLE qualidade ADD COLUMN sentido TEXT");
        if (!TemColuna(c, "qualidade", "chamada_id")) Exec(c, "ALTER TABLE qualidade ADD COLUMN chamada_id TEXT");
        // registros antigos com a assinatura do valor sintético (100% de perda e 0 ms de variação) passam a "sem medição"
        Exec(c, "UPDATE qualidade SET medido = 0 WHERE medido = 1 AND perda >= 99.9 AND jitter <= 0.001 AND sentido IS NULL");
        CriarTabelaTrechos(c);
        CriarTabelasV13(c);
        // 1.3.4: reações às mensagens (uma por pessoa por mensagem)
        Exec(c, """
            CREATE TABLE IF NOT EXISTS reacoes(
              mensagem INTEGER NOT NULL, login TEXT NOT NULL COLLATE NOCASE, emoji TEXT NOT NULL, criada_em TEXT NOT NULL,
              PRIMARY KEY(mensagem, login));
            """);
    }

    /// <summary>1.3.4: reage (ou troca a reação) à mensagem; o mesmo emoji de novo, ou emoji vazio, tira a reação. Devolve a mensagem atualizada.</summary>
    public MensagemDto Reagir(long mensagem, string login, string? emoji)
    {
        using var c = Abrir();
        var atual = Escalar<string>(c, "SELECT emoji FROM reacoes WHERE mensagem=$m AND login=$l", ("$m", mensagem), ("$l", login));
        if (string.IsNullOrEmpty(emoji) || atual == emoji)
            Exec(c, "DELETE FROM reacoes WHERE mensagem=$m AND login=$l", ("$m", mensagem), ("$l", login));
        else
            Exec(c, "INSERT INTO reacoes(mensagem, login, emoji, criada_em) VALUES($m, $l, $e, $t) ON CONFLICT(mensagem, login) DO UPDATE SET emoji=$e, criada_em=$t",
                ("$m", mensagem), ("$l", login), ("$e", emoji), ("$t", Agora()));
        return Mensagem(c, mensagem)!;
    }

    /// <summary>Emoji aceito como reação: texto curto sem letras, números nem controles (um emoji pode ter vários caracteres: família, bandeira, tom de pele).</summary>
    public static bool EmojiValido(string? e)
    {
        if (string.IsNullOrEmpty(e) || e.Length > 24) return false;
        foreach (var ch in e)
            if (char.IsAsciiLetterOrDigit(ch) || char.IsControl(ch) || char.IsWhiteSpace(ch)) return false;
        return e.Any(ch => ch > 0x7F);
    }

    private static Dictionary<long, ReacaoDto[]> LerReacoes(SqliteConnection c, IEnumerable<long> ids)
    {
        var lista = ids.Distinct().ToList();
        var r = new Dictionary<long, ReacaoDto[]>();
        if (lista.Count == 0) return r;
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT mensagem, emoji, login FROM reacoes WHERE mensagem IN ({string.Join(",", lista)}) ORDER BY criada_em, login";
        var linhas = new List<(long M, string E, string L)>();
        using (var rd = cmd.ExecuteReader())
            while (rd.Read()) linhas.Add((rd.GetInt64(0), rd.GetString(1), rd.GetString(2)));
        foreach (var g in linhas.GroupBy(x => x.M))
            r[g.Key] = g.GroupBy(x => x.E).Select(e => new ReacaoDto(e.Key, e.Select(x => x.L).ToArray())).ToArray();
        return r;
    }

    // ======================= mensagens (1.1) =======================

    private const string SelMensagem = """
        SELECT m.id, m.conversa, m.autor, m.texto, m.arquivo_id, m.enviada_em, m.resposta_a, m.editada_em, m.apagada, m.mencoes,
               r.autor, r.texto, r.apagada, r.arquivo_id
        FROM mensagens m LEFT JOIN mensagens r ON r.id = m.resposta_a
        """;

    public const string TextoApagada = "🚫 Mensagem apagada";

    private static List<MensagemDto> LerMensagens(SqliteConnection c, SqliteCommand cmd)
    {
        var brutas = new List<(long Id, long Conv, string Autor, string Texto, string? Arq, string Em, long? Resp, string? Edit, bool Apag, string? Menc,
            string? RAutor, string? RTexto, bool RApag, string? RArq)>();
        using (var r = cmd.ExecuteReader())
            while (r.Read())
                brutas.Add((r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetString(3), S(r, 4), r.GetString(5),
                    r.IsDBNull(6) ? null : r.GetInt64(6), S(r, 7), !r.IsDBNull(8) && r.GetInt64(8) == 1, S(r, 9),
                    S(r, 10), S(r, 11), !r.IsDBNull(12) && r.GetInt64(12) == 1, S(r, 13)));
        var reacoes = LerReacoes(c, brutas.Where(m => !m.Apag).Select(m => m.Id));
        return brutas.Select(m =>
        {
            string? trecho = null;
            if (m.Resp != null)
            {
                trecho = m.RAutor == null ? "(mensagem não existe mais)"
                    : m.RApag ? TextoApagada
                    : string.IsNullOrWhiteSpace(m.RTexto) ? (m.RArq != null ? "📎 arquivo" : "")
                    : (m.RTexto!.Length > 140 ? m.RTexto[..140] + "…" : m.RTexto);
            }
            return new MensagemDto(m.Id, m.Conv, m.Autor, m.Apag ? TextoApagada : m.Texto,
                m.Apag || m.Arq is null ? null : Arquivo(c, m.Arq), Data(m.Em),
                m.Resp, m.RAutor, trecho, m.Edit is null ? null : Data(m.Edit), m.Apag,
                string.IsNullOrEmpty(m.Menc) ? null : m.Menc.Split(',', StringSplitOptions.RemoveEmptyEntries),
                reacoes.TryGetValue(m.Id, out var rc) ? rc : null);
        }).ToList();
    }

    private static MensagemDto? Mensagem(SqliteConnection c, long id)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = SelMensagem + " WHERE m.id=$i";
        cmd.Parameters.AddWithValue("$i", id);
        return LerMensagens(c, cmd).FirstOrDefault();
    }

    public MensagemDto? Mensagem(long id)
    {
        using var c = Abrir();
        return Mensagem(c, id);
    }

    public MensagemDto Editar(long id, string texto)
    {
        using var c = Abrir();
        Exec(c, "UPDATE mensagens SET texto=$t, editada_em=$e WHERE id=$i AND apagada=0", ("$t", texto), ("$e", Agora()), ("$i", id));
        return Mensagem(c, id)!;
    }

    /// <summary>Marca como apagada (o texto some de verdade). Retorna o arquivo que ficou sem uso, para apagar do disco.</summary>
    public (MensagemDto Msg, string? ArquivoSemUso) Apagar(long id)
    {
        using var c = Abrir();
        var arq = Escalar<string>(c, "SELECT arquivo_id FROM mensagens WHERE id=$i", ("$i", id));
        Exec(c, "UPDATE mensagens SET texto='', arquivo_id=NULL, mencoes=NULL, apagada=1, editada_em=$e WHERE id=$i", ("$e", Agora()), ("$i", id));
        Exec(c, "DELETE FROM reacoes WHERE mensagem=$i", ("$i", id));
        string? semUso = null;
        if (arq != null && Escalar<long>(c, "SELECT COUNT(*) FROM mensagens WHERE arquivo_id=$a", ("$a", arq)) == 0)
        {
            Exec(c, "DELETE FROM arquivos WHERE id=$a", ("$a", arq));
            semUso = arq;
        }
        return (Mensagem(c, id)!, semUso);
    }

    /// <summary>Busca nas conversas de que a pessoa participa (texto e nome de arquivo), das mais novas para as mais antigas.</summary>
    public List<ResultadoBuscaDto> Buscar(string login, string texto, int limite = 50)
    {
        using var c = Abrir();
        using var cmd = c.CreateCommand();
        cmd.CommandText = SelMensagem + """
             JOIN membros b ON b.conversa = m.conversa AND b.login = $l
             LEFT JOIN arquivos a ON a.id = m.arquivo_id
             WHERE m.apagada = 0 AND (m.texto LIKE $q ESCAPE '\' OR a.nome LIKE $q ESCAPE '\')
             ORDER BY m.id DESC LIMIT $n
            """;
        var escapado = texto.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
        cmd.Parameters.AddWithValue("$l", login);
        cmd.Parameters.AddWithValue("$q", "%" + escapado + "%");
        cmd.Parameters.AddWithValue("$n", Math.Clamp(limite, 1, 200));
        var msgs = LerMensagens(c, cmd);
        var titulos = new Dictionary<long, (string?, bool)>();
        foreach (var conv in msgs.Select(m => m.Conversa).Distinct())
        {
            using var t = c.CreateCommand();
            t.CommandText = "SELECT titulo, grupo FROM conversas WHERE id=$c";
            t.Parameters.AddWithValue("$c", conv);
            using var r = t.ExecuteReader();
            titulos[conv] = r.Read() ? (S(r, 0), r.GetInt64(1) == 1) : (null, false);
        }
        return msgs.Select(m => new ResultadoBuscaDto(m, titulos[m.Conversa].Item1, titulos[m.Conversa].Item2)).ToList();
    }

    // ======================= clientes (versão do app por pessoa) =======================

    public void RegistrarCliente(string login, string? versao, string? pc)
    {
        using var c = Abrir();
        Exec(c, """
            INSERT INTO clientes(login, versao, pc, visto_em) VALUES($l, $v, $p, $t)
            ON CONFLICT(login) DO UPDATE SET versao=$v, pc=$p, visto_em=$t
            """, ("$l", login), ("$v", versao), ("$p", pc), ("$t", Agora()));
    }

    public List<(string Login, string? Versao, string? Pc, DateTime VistoEm)> Clientes()
    {
        using var c = Abrir();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT login, versao, pc, visto_em FROM clientes ORDER BY visto_em DESC";
        using var r = cmd.ExecuteReader();
        var l = new List<(string, string?, string?, DateTime)>();
        while (r.Read()) l.Add((r.GetString(0), S(r, 1), S(r, 2), Data(r.GetString(3))));
        return l;
    }

    // ======================= qualidade =======================

    public void InserirQualidade(string login, string? unidade, string? setor, QualidadeLigacaoDto q)
    {
        using var c = Abrir();
        // 1.3: apps antigos não mandam "Medido": "100% de perda com 0 ms de variação" é o valor sintético de "nenhum pacote chegou", não uma medição
        bool medido = q.Medido ?? !(q.PerdaPct >= 99.9 && q.JitterMs <= 0.001);
        Exec(c, """
            INSERT INTO qualidade(login, unidade, setor, numero, direcao, inicio, duracao, codec, mos, mos_min, perda, jitter, rtt, conferencia, versao, criada_em, medido, sentido, chamada_id)
            VALUES($l, $u, $s, $n, $d, $i, $du, $co, $m, $mm, $p, $j, $r, $cf, $v, $t, $md, $se, $ci)
            """, ("$l", login), ("$u", unidade), ("$s", setor), ("$n", q.Numero), ("$d", q.Direcao), ("$i", q.Inicio.ToUniversalTime().ToString("O")),
            ("$du", q.DuracaoSegundos), ("$co", q.Codec), ("$m", q.Mos), ("$mm", q.MosMinimo), ("$p", q.PerdaPct), ("$j", q.JitterMs),
            ("$r", q.RttMs), ("$cf", q.Conferencia ? 1 : 0), ("$v", q.VersaoApp), ("$t", Agora()), ("$md", medido ? 1 : 0),
            ("$se", q.Sentido ?? "recebido"), ("$ci", q.ChamadaId));
    }

    public QualidadePainelDto PainelQualidade(int dias, double mosRuim)
    {
        using var c = Abrir();
        var desde = DateTime.UtcNow.AddDays(-dias).ToString("O");
        var porUnidade = new List<QualidadeUnidadeDto>();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = """
                SELECT COALESCE(unidade, '(sem unidade)'), SUM(medido), COALESCE(AVG(CASE WHEN medido=1 THEN mos END), 0),
                       SUM(CASE WHEN medido=1 AND mos < $r THEN 1 ELSE 0 END), COALESCE(AVG(CASE WHEN medido=1 THEN perda END), 0),
                       COALESCE(AVG(CASE WHEN medido=1 THEN jitter END), 0), SUM(CASE WHEN medido=0 THEN 1 ELSE 0 END)
                FROM qualidade WHERE inicio >= $d GROUP BY COALESCE(unidade, '(sem unidade)') ORDER BY AVG(CASE WHEN medido=1 THEN mos END)
                """;
            cmd.Parameters.AddWithValue("$d", desde);
            cmd.Parameters.AddWithValue("$r", mosRuim);
            using var r = cmd.ExecuteReader();
            while (r.Read()) porUnidade.Add(new(r.GetString(0), r.GetInt32(1), r.GetDouble(2), r.GetInt32(3), r.GetDouble(4), r.GetDouble(5), r.GetInt32(6)));
        }
        var piores = new List<QualidadeItemDto>();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = """
                SELECT q.login, COALESCE(p.nome, q.login), q.unidade, q.numero, q.inicio, q.duracao, q.mos, q.perda, q.jitter, q.versao, q.medido, q.sentido
                FROM qualidade q LEFT JOIN pessoas p ON p.login = q.login
                WHERE q.inicio >= $d AND (q.mos < $r OR q.medido = 0) ORDER BY q.medido, q.mos LIMIT 100
                """;
            cmd.Parameters.AddWithValue("$d", desde);
            cmd.Parameters.AddWithValue("$r", mosRuim);
            using var r = cmd.ExecuteReader();
            while (r.Read()) piores.Add(new(r.GetString(0), r.GetString(1), S(r, 2), S(r, 3) ?? "", Data(r.GetString(4)), r.GetInt32(5),
                r.GetDouble(6), r.GetDouble(7), r.GetDouble(8), S(r, 9), r.GetInt64(10) == 1, S(r, 11)));
        }
        return new QualidadePainelDto(dias, porUnidade.ToArray(), piores.ToArray());
    }

    // ======================= comunicados =======================

    private const string SelComunicado = """
        SELECT c.id, c.titulo, c.texto, c.nivel, c.unidade, c.autor, COALESCE(p.nome, c.autor), c.criado_em, c.expira_em, c.cancelado, COALESCE(c.exigir_ciencia, 1)
        FROM comunicados c LEFT JOIN pessoas p ON p.login = c.autor
        """;

    private static (ComunicadoDto Dto, bool Cancelado) LerComunicado(SqliteDataReader r) =>
        (new ComunicadoDto(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), S(r, 4), r.GetString(5), r.GetString(6),
            Data(r.GetString(7)), Data(r.GetString(8)), r.GetInt64(10) == 1), r.GetInt64(9) == 1);

    public ComunicadoDto InserirComunicado(NovoComunicadoDto n, string autor)
    {
        using var c = Abrir();
        var agora = DateTime.UtcNow;
        Exec(c, "INSERT INTO comunicados(titulo, texto, nivel, unidade, autor, criado_em, expira_em) VALUES($t, $x, $n, $u, $a, $c, $e)",
            ("$t", n.Titulo.Trim()), ("$x", n.Texto.Trim()), ("$n", n.Nivel), ("$u", string.IsNullOrWhiteSpace(n.Unidade) ? null : n.Unidade.Trim()),
            ("$a", autor), ("$c", agora.ToString("O")), ("$e", agora.AddHours(Math.Clamp(n.Horas, 1, 24 * 30)).ToString("O")));
        var id = Escalar<long>(c, "SELECT last_insert_rowid()");
        return Comunicado(c, id)!.Value.Dto;
    }

    private static (ComunicadoDto Dto, bool Cancelado)? Comunicado(SqliteConnection c, long id)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = SelComunicado + " WHERE c.id=$i";
        cmd.Parameters.AddWithValue("$i", id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? LerComunicado(r) : null;
    }

    /// <summary>Ativos (não cancelados, não vencidos) para a pessoa (todos, ou a unidade dela) e que ela ainda não confirmou.</summary>
    public List<ComunicadoDto> ComunicadosPendentes(string login, string? unidade)
    {
        using var c = Abrir();
        using var cmd = c.CreateCommand();
        cmd.CommandText = SelComunicado + """
             WHERE c.cancelado = 0 AND c.expira_em > $agora
               AND (c.unidade IS NULL OR c.unidade = $u COLLATE NOCASE)
               AND (NOT EXISTS (SELECT 1 FROM comunicados_dest d0 WHERE d0.comunicado = c.id) OR EXISTS (SELECT 1 FROM comunicados_dest d WHERE d.comunicado = c.id AND d.login = $l))
               AND (COALESCE(c.exigir_ciencia, 1) = 1 OR NOT EXISTS (SELECT 1 FROM comunicados_entregues e WHERE e.comunicado = c.id AND e.login = $l))
               AND NOT EXISTS (SELECT 1 FROM comunicados_lidos l WHERE l.comunicado = c.id AND l.login = $l)
             ORDER BY c.id
            """;
        cmd.Parameters.AddWithValue("$agora", DateTime.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$u", (object?)unidade ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$l", login);
        using var r = cmd.ExecuteReader();
        var l = new List<ComunicadoDto>();
        while (r.Read()) l.Add(LerComunicado(r).Dto);
        return l;
    }

    public void ConfirmarComunicado(long id, string login)
    {
        using var c = Abrir();
        Exec(c, "INSERT OR IGNORE INTO comunicados_lidos(comunicado, login, lido_em) VALUES($c, $l, $t)", ("$c", id), ("$l", login), ("$t", Agora()));
    }

    public bool CancelarComunicado(long id)
    {
        using var c = Abrir();
        Exec(c, "UPDATE comunicados SET cancelado=1 WHERE id=$i", ("$i", id));
        return Escalar<long>(c, "SELECT changes()") > 0;
    }

    public List<ComunicadoAdminDto> ComunicadosAdmin(int limite = 50)
    {
        using var c = Abrir();
        var lista = new List<(ComunicadoDto Dto, bool Cancelado)>();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = SelComunicado + " ORDER BY c.id DESC LIMIT $n";
            cmd.Parameters.AddWithValue("$n", limite);
            using var r = cmd.ExecuteReader();
            while (r.Read()) lista.Add(LerComunicado(r));
        }
        return lista.Select(x =>
        {
            int lidos = (int)Escalar<long>(c, "SELECT COUNT(*) FROM comunicados_lidos WHERE comunicado=$i", ("$i", x.Dto.Id));
            int comLista = (int)Escalar<long>(c, "SELECT COUNT(*) FROM comunicados_dest WHERE comunicado=$i", ("$i", x.Dto.Id));
            int dest = comLista > 0 ? comLista : (int)(x.Dto.Unidade == null
                ? Escalar<long>(c, "SELECT COUNT(*) FROM pessoas WHERE ativo=1")
                : Escalar<long>(c, "SELECT COUNT(*) FROM pessoas WHERE ativo=1 AND unidade=$u COLLATE NOCASE", ("$u", x.Dto.Unidade)));
            int entregues = (int)Escalar<long>(c, "SELECT COUNT(*) FROM comunicados_entregues WHERE comunicado=$i", ("$i", x.Dto.Id));
            return new ComunicadoAdminDto(x.Dto, lidos, dest, x.Cancelado, entregues, PublicoDe(c, x.Dto.Id));
        }).ToList();
    }

    /// <summary>Logins ativos de uma unidade (null = todos), para mandar o comunicado só para quem é de lá.</summary>
    public List<string> LoginsDaUnidade(string? unidade)
    {
        using var c = Abrir();
        using var cmd = c.CreateCommand();
        cmd.CommandText = unidade == null ? "SELECT login FROM pessoas WHERE ativo=1" : "SELECT login FROM pessoas WHERE ativo=1 AND unidade=$u COLLATE NOCASE";
        if (unidade != null) cmd.Parameters.AddWithValue("$u", unidade);
        using var r = cmd.ExecuteReader();
        var l = new List<string>();
        while (r.Read()) l.Add(r.GetString(0));
        return l;
    }

    public List<string> Unidades()
    {
        using var c = Abrir();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT unidade FROM pessoas WHERE ativo=1 AND unidade IS NOT NULL AND unidade <> '' ORDER BY unidade COLLATE NOCASE";
        using var r = cmd.ExecuteReader();
        var l = new List<string>();
        while (r.Read()) l.Add(r.GetString(0));
        return l;
    }

    // ======================= configuração do painel =======================

    public string? LerConfig(string chave)
    {
        using var c = Abrir();
        return Escalar<string>(c, "SELECT valor FROM config WHERE chave=$k", ("$k", chave));
    }

    public void GravarConfig(string chave, string? valor)
    {
        using var c = Abrir();
        if (valor == null) Exec(c, "DELETE FROM config WHERE chave=$k", ("$k", chave));
        else Exec(c, "INSERT INTO config(chave, valor) VALUES($k, $v) ON CONFLICT(chave) DO UPDATE SET valor=$v", ("$k", chave), ("$v", valor));
    }

    // ======================= limpeza =======================

    /// <summary>Apaga medições de qualidade velhas e comunicados vencidos há mais de 30 dias.</summary>
    public (int Qualidade, int Comunicados) Limpar(int diasQualidade)
    {
        using var c = Abrir();
        Exec(c, "DELETE FROM qualidade WHERE inicio < $d", ("$d", DateTime.UtcNow.AddDays(-diasQualidade).ToString("O")));
        // 1.3: auditoria guarda 365 dias; chamadas do painel seguem a retenção da qualidade
        Exec(c, "DELETE FROM auditoria WHERE em < $a", ("$a", DateTime.UtcNow.AddDays(-365).ToString("O")));
        Exec(c, "DELETE FROM chamadas WHERE inicio < $d", ("$d", DateTime.UtcNow.AddDays(-Math.Max(diasQualidade, 90)).ToString("O")));
        int q = (int)Escalar<long>(c, "SELECT changes()");
        var limite = DateTime.UtcNow.AddDays(-30).ToString("O");
        Exec(c, "DELETE FROM comunicados_lidos WHERE comunicado IN (SELECT id FROM comunicados WHERE expira_em < $d)", ("$d", limite));
        Exec(c, "DELETE FROM comunicados WHERE expira_em < $d", ("$d", limite));
        int k = (int)Escalar<long>(c, "SELECT changes()");
        return (q, k);
    }
}
