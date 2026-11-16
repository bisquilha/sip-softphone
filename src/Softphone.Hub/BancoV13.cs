using System.Text;
using System.Text.Json;
using Softphone.Contratos;
using Microsoft.Data.Sqlite;

namespace Softphone.Hub;

/// <summary>
/// 1.3 (Claude Code, 10/11/2026): auditoria do painel, registro de chamadas, artigos da base de conhecimento com versões e
/// permissão por setor, anotações de atendimento e eventos do copiloto.
/// </summary>
public sealed partial class Banco
{
    private static readonly JsonSerializerOptions JsonV13 = new(JsonSerializerDefaults.Web);

    /// <summary>Cadeia de conexão do SQLite (para serviços do Hub que abrem a própria conexão).</summary>
    public string CadeiaDeConexao => _cs;

    private static void CriarTabelasV13(SqliteConnection c)
    {
        Exec(c, """
            CREATE TABLE IF NOT EXISTS auditoria(
              id INTEGER PRIMARY KEY AUTOINCREMENT, em TEXT NOT NULL, usuario TEXT NOT NULL COLLATE NOCASE, acao TEXT NOT NULL,
              alvo TEXT NOT NULL, status INTEGER NOT NULL, detalhe TEXT);
            CREATE INDEX IF NOT EXISTS ix_auditoria_em ON auditoria(em);
            CREATE TABLE IF NOT EXISTS chamadas(
              id TEXT NOT NULL, login TEXT NOT NULL COLLATE NOCASE, unidade TEXT, setor TEXT, numero TEXT NOT NULL, direcao TEXT NOT NULL,
              resultado TEXT NOT NULL, inicio TEXT NOT NULL, duracao INTEGER NOT NULL, transferida INTEGER NOT NULL DEFAULT 0, motivo TEXT,
              criada_em TEXT NOT NULL, PRIMARY KEY(id, login));
            CREATE INDEX IF NOT EXISTS ix_chamadas_inicio ON chamadas(inicio);
            CREATE TABLE IF NOT EXISTS artigos(
              id INTEGER PRIMARY KEY AUTOINCREMENT, titulo TEXT NOT NULL, categoria TEXT NOT NULL, tags TEXT NOT NULL, corpo TEXT NOT NULL,
              leitura TEXT NOT NULL, edicao TEXT NOT NULL, publicado INTEGER NOT NULL DEFAULT 0, versao INTEGER NOT NULL DEFAULT 1,
              autor TEXT NOT NULL, atualizado_em TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS artigo_versoes(
              artigo INTEGER NOT NULL, versao INTEGER NOT NULL, titulo TEXT NOT NULL, corpo TEXT NOT NULL, autor TEXT NOT NULL, em TEXT NOT NULL,
              PRIMARY KEY(artigo, versao));
            CREATE TABLE IF NOT EXISTS anotacoes(
              id INTEGER PRIMARY KEY AUTOINCREMENT, protocolo TEXT NOT NULL, numero TEXT NOT NULL, nome TEXT, motivo TEXT, texto TEXT NOT NULL,
              artigo_id INTEGER, login TEXT NOT NULL COLLATE NOCASE, em TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_anotacoes_numero ON anotacoes(numero);
            CREATE TABLE IF NOT EXISTS copiloto_eventos(
              id INTEGER PRIMARY KEY AUTOINCREMENT, login TEXT NOT NULL COLLATE NOCASE, em TEXT NOT NULL, tipo TEXT NOT NULL, detalhe TEXT);
            CREATE TABLE IF NOT EXISTS automacoes_exec(regra TEXT NOT NULL, chave TEXT NOT NULL, em TEXT NOT NULL, PRIMARY KEY(regra, chave));
            CREATE TABLE IF NOT EXISTS comunicados_dest(comunicado INTEGER NOT NULL, login TEXT NOT NULL COLLATE NOCASE, PRIMARY KEY(comunicado, login));
            CREATE TABLE IF NOT EXISTS comunicados_entregues(comunicado INTEGER NOT NULL, login TEXT NOT NULL COLLATE NOCASE, em TEXT NOT NULL, PRIMARY KEY(comunicado, login));
            CREATE VIRTUAL TABLE IF NOT EXISTS artigos_fts USING fts5(titulo, corpo, tags, tokenize='unicode61 remove_diacritics 2');
            """);
        if (!TemColuna(c, "comunicados", "exigir_ciencia")) Exec(c, "ALTER TABLE comunicados ADD COLUMN exigir_ciencia INTEGER NOT NULL DEFAULT 1");
        if (!TemColuna(c, "comunicados", "publico")) Exec(c, "ALTER TABLE comunicados ADD COLUMN publico TEXT");
    }

    // ======================= auditoria =======================

    public void Auditar(string usuario, string acao, string alvo, int status, string? detalhe = null)
    {
        using var c = Abrir();
        Exec(c, "INSERT INTO auditoria(em, usuario, acao, alvo, status, detalhe) VALUES($e, $u, $a, $al, $s, $d)",
            ("$e", Agora()), ("$u", usuario), ("$a", acao), ("$al", alvo), ("$s", status), ("$d", detalhe));
    }

    public List<AuditoriaDto> ListarAuditoria(DateTime? de, DateTime? ate, string? usuario, string? acao, string? texto, int limite, string? gravidade = null, string? ramal = null)
    {
        var r = new List<AuditoriaDto>();
        using var c = Abrir();
        using var cmd = c.CreateCommand();
        var onde = new List<string> { "1=1" };
        if (de != null) { onde.Add("em >= $de"); cmd.Parameters.AddWithValue("$de", de.Value.ToUniversalTime().ToString("O")); }
        if (ate != null) { onde.Add("em < $ate"); cmd.Parameters.AddWithValue("$ate", ate.Value.ToUniversalTime().ToString("O")); }
        if (!string.IsNullOrWhiteSpace(usuario)) { onde.Add("usuario = $u"); cmd.Parameters.AddWithValue("$u", usuario.Trim()); }
        if (!string.IsNullOrWhiteSpace(acao)) { onde.Add("acao = $a"); cmd.Parameters.AddWithValue("$a", acao.Trim().ToUpperInvariant()); }
        // gravidade pelo resultado: info (< 400), aviso (4xx: negado, inválido), erro (5xx)
        if (gravidade == "info") onde.Add("status < 400"); else if (gravidade == "aviso") onde.Add("status >= 400 AND status < 500"); else if (gravidade == "erro") onde.Add("status >= 500");
        if (!string.IsNullOrWhiteSpace(ramal)) { onde.Add("usuario IN (SELECT login FROM pessoas WHERE ramal = $rm OR ramal_ad = $rm)"); cmd.Parameters.AddWithValue("$rm", ramal.Trim()); }
        if (!string.IsNullOrWhiteSpace(texto))
        {
            onde.Add("(alvo LIKE $t ESCAPE '\\' OR COALESCE(detalhe,'') LIKE $t ESCAPE '\\')");
            cmd.Parameters.AddWithValue("$t", "%" + texto.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%");
        }
        cmd.CommandText = $"SELECT id, em, usuario, acao, alvo, status, detalhe FROM auditoria WHERE {string.Join(" AND ", onde)} ORDER BY id DESC LIMIT $l";
        cmd.Parameters.AddWithValue("$l", Math.Clamp(limite, 1, 5000));
        using var rd = cmd.ExecuteReader();
        while (rd.Read()) r.Add(new(rd.GetInt64(0), Data(rd.GetString(1)), rd.GetString(2), rd.GetString(3), rd.GetString(4), rd.GetInt32(5), S(rd, 6)));
        return r;
    }

    // ======================= chamadas (painel da TI) =======================

    public void InserirChamada(string login, string? unidade, string? setor, ChamadaRegistroDto c1)
    {
        using var c = Abrir();
        Exec(c, """
            INSERT OR REPLACE INTO chamadas(id, login, unidade, setor, numero, direcao, resultado, inicio, duracao, transferida, motivo, criada_em)
            VALUES($i, $l, $u, $s, $n, $d, $r, $in, $du, $t, $m, $c)
            """, ("$i", c1.Id), ("$l", login), ("$u", unidade), ("$s", setor), ("$n", c1.Numero), ("$d", c1.Direcao), ("$r", c1.Resultado),
            ("$in", c1.Inicio.ToUniversalTime().ToString("O")), ("$du", c1.DuracaoSegundos), ("$t", c1.Transferida ? 1 : 0), ("$m", c1.Motivo), ("$c", Agora()));
    }

    public bool HaChamadasRegistradas()
    {
        using var c = Abrir();
        return Escalar<long>(c, "SELECT COUNT(*) FROM (SELECT 1 FROM chamadas LIMIT 1)") > 0;
    }

    public List<ChamadaPainelDto> ListarChamadas(DateTime desde, string? resultado, string? direcao, string? unidade, string? setor, bool? transferida, int limite)
    {
        var r = new List<ChamadaPainelDto>();
        using var c = Abrir();
        using var cmd = c.CreateCommand();
        var onde = new List<string> { "c.inicio >= $d" };
        cmd.Parameters.AddWithValue("$d", desde.ToUniversalTime().ToString("O"));
        if (!string.IsNullOrWhiteSpace(resultado))
        {
            // vários resultados separados por vírgula (ex.: "Falhou,Ocupado,NaoAtendida")
            var itens = resultado.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(8).ToArray();
            onde.Add("c.resultado IN (" + string.Join(",", itens.Select((_, i) => "$r" + i)) + ")");
            for (int i = 0; i < itens.Length; i++) cmd.Parameters.AddWithValue("$r" + i, itens[i]);
        }
        if (transferida == true) onde.Add("c.transferida = 1");
        if (!string.IsNullOrWhiteSpace(direcao)) { onde.Add("c.direcao = $di"); cmd.Parameters.AddWithValue("$di", direcao); }
        if (!string.IsNullOrWhiteSpace(unidade)) { onde.Add("c.unidade = $u"); cmd.Parameters.AddWithValue("$u", unidade); }
        if (!string.IsNullOrWhiteSpace(setor)) { onde.Add("c.setor = $s"); cmd.Parameters.AddWithValue("$s", setor); }
        cmd.CommandText = $"""
            SELECT c.id, c.login, COALESCE(p.nome, c.login), c.unidade, c.setor, c.numero, c.direcao, c.resultado, c.inicio, c.duracao, c.transferida, c.motivo
            FROM chamadas c LEFT JOIN pessoas p ON p.login = c.login WHERE {string.Join(" AND ", onde)} ORDER BY c.inicio DESC LIMIT $l
            """;
        cmd.Parameters.AddWithValue("$l", Math.Clamp(limite, 1, 2000));
        using var rd = cmd.ExecuteReader();
        while (rd.Read()) r.Add(new(rd.GetString(0), rd.GetString(1), rd.GetString(2), S(rd, 3), S(rd, 4), rd.GetString(5), rd.GetString(6), rd.GetString(7),
            Data(rd.GetString(8)), rd.GetInt32(9), rd.GetInt64(10) == 1, S(rd, 11)));
        return r;
    }

    /// <summary>Contagens do período. Definições: Recebida/Realizada = direção; Perdida = recebida não atendida (inclui "não perturbe" e bloqueio);
    /// Recusada = o usuário recusou; Falha = a chamada realizada não completou; Transferida = encerrada por transferência (conta também como atendida).</summary>
    public (int Rec, int Real, int Perd, int Recu, int Falh, int Transf, double? Medio, int Total) ContagemChamadas(DateTime desde)
    {
        using var c = Abrir();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT
              COALESCE(SUM(CASE WHEN direcao='entrada' THEN 1 ELSE 0 END),0),
              COALESCE(SUM(CASE WHEN direcao='saida' THEN 1 ELSE 0 END),0),
              COALESCE(SUM(CASE WHEN resultado='Perdida' THEN 1 ELSE 0 END),0),
              COALESCE(SUM(CASE WHEN resultado='Recusada' THEN 1 ELSE 0 END),0),
              COALESCE(SUM(CASE WHEN resultado IN ('Falhou','Ocupado','NaoAtendida') THEN 1 ELSE 0 END),0),
              COALESCE(SUM(transferida),0),
              AVG(CASE WHEN duracao > 0 THEN duracao END),
              COUNT(*)
            FROM chamadas WHERE inicio >= $d
            """;
        cmd.Parameters.AddWithValue("$d", desde.ToUniversalTime().ToString("O"));
        using var rd = cmd.ExecuteReader();
        rd.Read();
        return (rd.GetInt32(0), rd.GetInt32(1), rd.GetInt32(2), rd.GetInt32(3), rd.GetInt32(4), rd.GetInt32(5), rd.IsDBNull(6) ? null : rd.GetDouble(6), rd.GetInt32(7));
    }

    // ======================= artigos =======================

    private sealed record Linha(long Id, string Titulo, string Categoria, string[] Tags, string Corpo, string[] Leitura, string[] Edicao, bool Publicado, int Versao, string Autor, DateTime Em);

    private static string[] Lista(string? json) => string.IsNullOrEmpty(json) ? [] : JsonSerializer.Deserialize<string[]>(json, JsonV13) ?? [];
    private static string ParaJson(IEnumerable<string>? l) => JsonSerializer.Serialize((l ?? []).Select(x => x.Trim()).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), JsonV13);

    private static Linha LerLinha(SqliteDataReader r) => new(r.GetInt64(0), r.GetString(1), r.GetString(2), Lista(r.GetString(3)), r.GetString(4), Lista(r.GetString(5)),
        Lista(r.GetString(6)), r.GetInt64(7) == 1, r.GetInt32(8), r.GetString(9), Data(r.GetString(10)));

    private const string SelArtigo = "SELECT id, titulo, categoria, tags, corpo, leitura, edicao, publicado, versao, autor, atualizado_em FROM artigos";

    /// <summary>Quem pode ler: artigo publicado e (sem restrição de setor ou setor do leitor na lista). A TI lê tudo, inclusive rascunhos.</summary>
    public static bool PodeLer(ArtigoDto a, string? setor, bool admin) =>
        admin || (a.Publicado && (a.SetoresLeitura.Length == 0 || (setor != null && a.SetoresLeitura.Contains(setor, StringComparer.OrdinalIgnoreCase))));

    public static bool PodeEditar(string[] setoresEdicao, string? setor, bool admin) =>
        admin || (setor != null && setoresEdicao.Contains(setor, StringComparer.OrdinalIgnoreCase));

    private ArtigoDto Dto(Linha l, string? setor, bool admin) =>
        new(l.Id, l.Titulo, l.Categoria, l.Tags, l.Corpo, l.Leitura, l.Edicao, l.Publicado, l.Versao, l.Autor, l.Em, PodeEditar(l.Edicao, setor, admin));

    public ArtigoDto? ObterArtigo(long id, string? setor, bool admin)
    {
        using var c = Abrir();
        using var cmd = c.CreateCommand();
        cmd.CommandText = SelArtigo + " WHERE id=$i";
        cmd.Parameters.AddWithValue("$i", id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? Dto(LerLinha(r), setor, admin) : null;
    }

    /// <summary>Todos os artigos que esta pessoa pode ler (a TI vê também os rascunhos).</summary>
    public List<ArtigoDto> ListarArtigos(string? setor, bool admin)
    {
        var r = new List<ArtigoDto>();
        using var c = Abrir();
        using var cmd = c.CreateCommand();
        cmd.CommandText = SelArtigo + " ORDER BY categoria, titulo";
        using var rd = cmd.ExecuteReader();
        while (rd.Read()) { var a = Dto(LerLinha(rd), setor, admin); if (PodeLer(a, setor, admin)) r.Add(a); }
        return r;
    }

    public long SalvarArtigo(SalvarArtigoDto d, string autor)
    {
        using var c = Abrir();
        using var tx = c.BeginTransaction();
        long id; int versao;
        var agora = Agora();
        if (d.Id is long existente && Escalar<long>(c, "SELECT COUNT(*) FROM artigos WHERE id=$i", ("$i", existente)) > 0)
        {
            id = existente;
            versao = Escalar<int>(c, "SELECT versao FROM artigos WHERE id=$i", ("$i", id)) + 1;
            Exec(c, "UPDATE artigos SET titulo=$t, categoria=$c, tags=$g, corpo=$b, leitura=$l, edicao=$e, publicado=$p, versao=$v, autor=$a, atualizado_em=$m WHERE id=$i",
                ("$t", d.Titulo.Trim()), ("$c", d.Categoria.Trim()), ("$g", ParaJson(d.Tags)), ("$b", d.Corpo), ("$l", ParaJson(d.SetoresLeitura)),
                ("$e", ParaJson(d.SetoresEdicao)), ("$p", d.Publicado ? 1 : 0), ("$v", versao), ("$a", autor), ("$m", agora), ("$i", id));
        }
        else
        {
            versao = 1;
            Exec(c, "INSERT INTO artigos(titulo, categoria, tags, corpo, leitura, edicao, publicado, versao, autor, atualizado_em) VALUES($t,$c,$g,$b,$l,$e,$p,1,$a,$m)",
                ("$t", d.Titulo.Trim()), ("$c", d.Categoria.Trim()), ("$g", ParaJson(d.Tags)), ("$b", d.Corpo), ("$l", ParaJson(d.SetoresLeitura)),
                ("$e", ParaJson(d.SetoresEdicao)), ("$p", d.Publicado ? 1 : 0), ("$a", autor), ("$m", agora));
            id = Escalar<long>(c, "SELECT last_insert_rowid()");
        }
        Exec(c, "INSERT OR REPLACE INTO artigo_versoes(artigo, versao, titulo, corpo, autor, em) VALUES($a,$v,$t,$b,$u,$e)",
            ("$a", id), ("$v", versao), ("$t", d.Titulo.Trim()), ("$b", d.Corpo), ("$u", autor), ("$e", agora));
        Exec(c, "DELETE FROM artigos_fts WHERE rowid=$i", ("$i", id));
        Exec(c, "INSERT INTO artigos_fts(rowid, titulo, corpo, tags) VALUES($i,$t,$b,$g)",
            ("$i", id), ("$t", d.Titulo.Trim()), ("$b", d.Corpo), ("$g", string.Join(' ', d.Tags ?? [])));
        tx.Commit();
        return id;
    }

    public bool ApagarArtigo(long id)
    {
        using var c = Abrir();
        Exec(c, "DELETE FROM artigos_fts WHERE rowid=$i", ("$i", id));
        Exec(c, "DELETE FROM artigo_versoes WHERE artigo=$i", ("$i", id));
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM artigos WHERE id=$i";
        cmd.Parameters.AddWithValue("$i", id);
        return cmd.ExecuteNonQuery() > 0;
    }

    public List<ArtigoVersaoDto> VersoesArtigo(long id)
    {
        var r = new List<ArtigoVersaoDto>();
        using var c = Abrir();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT versao, autor, em, titulo FROM artigo_versoes WHERE artigo=$i ORDER BY versao DESC";
        cmd.Parameters.AddWithValue("$i", id);
        using var rd = cmd.ExecuteReader();
        while (rd.Read()) r.Add(new(rd.GetInt32(0), rd.GetString(1), Data(rd.GetString(2)), rd.GetString(3)));
        return r;
    }

    private static readonly HashSet<string> Ignorar = new(StringComparer.OrdinalIgnoreCase)
    {
        "para","como","qual","quais","quando","onde","voces","voce","vocês","você","fazem","tem","têm","uma","uns","umas","com","sem","por","dos","das","nos","nas",
        "que","isso","essa","esse","esta","este","são","sao","ser","foi","mais","muito","pode","podem","gostaria","queria","favor","bom","boa","dia","tarde","noite",
        "mim","meu","minha","nosso","nossa","seu","sua","aos","ele","ela","eles","elas","ainda","tambem","também","entao","então","pois","porque","sobre","qualquer",
    };

    /// <summary>Monta a consulta FTS5 (termos com prefixo, ligados por OR) a partir de texto livre; vazia se não sobrar termo útil.</summary>
    public static string ConsultaFts(string texto)
    {
        var termos = new List<string>();
        var atual = new StringBuilder();
        foreach (var ch in texto + " ")
        {
            if (char.IsLetterOrDigit(ch)) { atual.Append(ch); continue; }
            if (atual.Length >= 3 && !Ignorar.Contains(atual.ToString())) termos.Add(atual.ToString());
            atual.Clear();
        }
        return string.Join(" OR ", termos.Distinct(StringComparer.OrdinalIgnoreCase).Take(12).Select(t => "\"" + t.Replace("\"", "") + "\"*"));
    }

    /// <summary>Artigos mais relevantes para o texto, JÁ filtrados pela permissão de quem pergunta (a regra vale antes de a IA ver qualquer coisa).</summary>
    public List<(ArtigoDto Artigo, double Relevancia)> BuscarArtigos(string texto, string? setor, bool admin, int limite, bool somentePublicados)
    {
        var r = new List<(ArtigoDto, double)>();
        var q = ConsultaFts(texto);
        if (q.Length == 0) return r;
        using var c = Abrir();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT rowid, bm25(artigos_fts, 6.0, 1.0, 3.0) FROM artigos_fts WHERE artigos_fts MATCH $q ORDER BY bm25(artigos_fts, 6.0, 1.0, 3.0) LIMIT 40";
        cmd.Parameters.AddWithValue("$q", q);
        var ids = new List<(long, double)>();
        using (var rd = cmd.ExecuteReader()) while (rd.Read()) ids.Add((rd.GetInt64(0), -rd.GetDouble(1)));
        foreach (var (id, rel) in ids)
        {
            var a = ObterArtigo(id, setor, admin);
            if (a == null || !PodeLer(a, setor, admin)) continue;
            if (somentePublicados && !a.Publicado) continue;
            r.Add((a, rel));
            if (r.Count >= limite) break;
        }
        return r;
    }

    // ======================= anotações e eventos =======================

    public long InserirAnotacao(string login, NovaAnotacaoDto n, string protocolo)
    {
        using var c = Abrir();
        Exec(c, "INSERT INTO anotacoes(protocolo, numero, nome, motivo, texto, artigo_id, login, em) VALUES($p,$n,$no,$m,$t,$a,$l,$e)",
            ("$p", protocolo), ("$n", n.Numero), ("$no", n.Nome), ("$m", n.Motivo), ("$t", n.Texto), ("$a", n.ArtigoId), ("$l", login), ("$e", Agora()));
        return Escalar<long>(c, "SELECT last_insert_rowid()");
    }

    public List<AnotacaoDto> Anotacoes(string? numero, string? login, int limite)
    {
        var r = new List<AnotacaoDto>();
        using var c = Abrir();
        using var cmd = c.CreateCommand();
        var onde = new List<string> { "1=1" };
        if (!string.IsNullOrWhiteSpace(numero)) { onde.Add("numero = $n"); cmd.Parameters.AddWithValue("$n", numero); }
        if (!string.IsNullOrWhiteSpace(login)) { onde.Add("login = $l"); cmd.Parameters.AddWithValue("$l", login); }
        cmd.CommandText = $"SELECT id, protocolo, numero, nome, motivo, texto, artigo_id, login, em FROM anotacoes WHERE {string.Join(" AND ", onde)} ORDER BY id DESC LIMIT $x";
        cmd.Parameters.AddWithValue("$x", Math.Clamp(limite, 1, 500));
        using var rd = cmd.ExecuteReader();
        while (rd.Read()) r.Add(new(rd.GetInt64(0), rd.GetString(1), rd.GetString(2), S(rd, 3), S(rd, 4), rd.GetString(5), rd.IsDBNull(6) ? null : rd.GetInt64(6), rd.GetString(7), Data(rd.GetString(8))));
        return r;
    }

    public void RegistrarEvento(string login, string tipo, string? detalhe)
    {
        using var c = Abrir();
        Exec(c, "INSERT INTO copiloto_eventos(login, em, tipo, detalhe) VALUES($l,$e,$t,$d)", ("$l", login), ("$e", Agora()), ("$t", tipo), ("$d", detalhe));
    }

    public (int Artigos, int Consultas, int Anotacoes, int Perguntas, int SemResposta) ContagemAtendimento(DateTime desde)
    {
        using var c = Abrir();
        var d = desde.ToUniversalTime().ToString("O");
        return (Escalar<int>(c, "SELECT COUNT(*) FROM artigos WHERE publicado=1"),
            Escalar<int>(c, "SELECT COUNT(*) FROM copiloto_eventos WHERE tipo='consulta' AND em >= $d", ("$d", d)),
            Escalar<int>(c, "SELECT COUNT(*) FROM anotacoes WHERE em >= $d", ("$d", d)),
            Escalar<int>(c, "SELECT COUNT(*) FROM copiloto_eventos WHERE tipo IN ('pergunta','sem_resposta') AND em >= $d", ("$d", d)),
            Escalar<int>(c, "SELECT COUNT(*) FROM copiloto_eventos WHERE tipo='sem_resposta' AND em >= $d", ("$d", d)));
    }

    // ======================= comunicados por público (unidade, setores, grupo) =======================

    /// <summary>Quem recebe: ativos que casam com unidade E setores (e, se vier, a lista de logins do grupo do AD).</summary>
    public List<Pessoa> PublicoDoComunicado(string? unidade, string[]? setores, IReadOnlyCollection<string>? loginsDoGrupo)
    {
        var r = new List<Pessoa>();
        foreach (var p in ListarPessoas())
        {
            if (!string.IsNullOrWhiteSpace(unidade) && !string.Equals(p.Unidade, unidade.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
            if (setores is { Length: > 0 } && !setores.Contains(p.Setor ?? "", StringComparer.OrdinalIgnoreCase)) continue;
            if (loginsDoGrupo != null && !loginsDoGrupo.Contains(p.Login, StringComparer.OrdinalIgnoreCase)) continue;
            r.Add(p);
        }
        return r;
    }

    public ComunicadoDto InserirComunicadoV13(NovoComunicadoV13Dto n, string autor, IEnumerable<string>? logins, string publico)
    {
        using var c = Abrir();
        var agora = DateTime.UtcNow;
        Exec(c, "INSERT INTO comunicados(titulo, texto, nivel, unidade, autor, criado_em, expira_em, exigir_ciencia, publico) VALUES($t, $x, $n, $u, $a, $c, $e, $ec, $p)",
            ("$t", n.Titulo.Trim()), ("$x", n.Texto.Trim()), ("$n", n.Nivel), ("$u", string.IsNullOrWhiteSpace(n.Unidade) ? null : n.Unidade.Trim()),
            ("$a", autor), ("$c", agora.ToString("O")), ("$e", agora.AddHours(Math.Clamp(n.Horas, 1, 24 * 30)).ToString("O")), ("$ec", n.ExigirCiencia ? 1 : 0), ("$p", publico));
        var id = Escalar<long>(c, "SELECT last_insert_rowid()");
        if (logins != null)
            foreach (var l in logins) Exec(c, "INSERT OR IGNORE INTO comunicados_dest(comunicado, login) VALUES($c, $l)", ("$c", id), ("$l", l));
        return Comunicado(c, id)!.Value.Dto;
    }

    private static string? PublicoDe(SqliteConnection c, long id) => Escalar<string>(c, "SELECT publico FROM comunicados WHERE id=$i", ("$i", id));

    public void MarcarEntregue(long comunicado, string login)
    {
        using var c = Abrir();
        Exec(c, "INSERT OR IGNORE INTO comunicados_entregues(comunicado, login, em) VALUES($c, $l, $e)", ("$c", comunicado), ("$l", login), ("$e", Agora()));
    }
}