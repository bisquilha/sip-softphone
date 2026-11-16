using Microsoft.Data.Sqlite;

namespace Softphone.Hub;

/// <summary>
/// 1.2.7 (Claude Code, 05/11/2026): trechos dos anexos dos perfis, num índice de texto do próprio SQLite (FTS5, sem
/// acentos), para mandar à IA só a parte de uma planilha/documento grande que tem a ver com o assunto.
/// </summary>
public sealed partial class Banco
{
    private static void CriarTabelaTrechos(SqliteConnection c) => Exec(c,
        "CREATE VIRTUAL TABLE IF NOT EXISTS trechos USING fts5(anexo UNINDEXED, texto, tokenize='unicode61 remove_diacritics 2')");

    public void GravarTrechos(string anexo, IEnumerable<string> trechos)
    {
        using var c = Abrir();
        using var tx = c.BeginTransaction();
        Exec(c, "DELETE FROM trechos WHERE anexo=$a", ("$a", anexo));
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO trechos(anexo, texto) VALUES($a, $t)";
        var pa = cmd.Parameters.Add("$a", SqliteType.Text);
        var pt = cmd.Parameters.Add("$t", SqliteType.Text);
        foreach (var t in trechos) { pa.Value = anexo; pt.Value = t; cmd.ExecuteNonQuery(); }
        tx.Commit();
    }

    public void ApagarTrechos(string anexo)
    {
        using var c = Abrir();
        Exec(c, "DELETE FROM trechos WHERE anexo=$a", ("$a", anexo));
    }

    /// <summary>Melhores trechos para a consulta FTS5, só destes anexos. Relevância: quanto maior, melhor.</summary>
    public List<(string Anexo, string Texto, double Relevancia)> BuscarTrechos(IReadOnlyCollection<string> anexos, string consultaFts, int limite)
    {
        var r = new List<(string, string, double)>();
        if (anexos.Count == 0 || string.IsNullOrWhiteSpace(consultaFts)) return r;
        using var c = Abrir();
        using var cmd = c.CreateCommand();
        var nomes = anexos.Select((_, i) => "$x" + i).ToList();
        cmd.CommandText = $"SELECT anexo, texto, bm25(trechos) FROM trechos WHERE trechos MATCH $q AND anexo IN ({string.Join(",", nomes)}) ORDER BY bm25(trechos) LIMIT $l";
        cmd.Parameters.AddWithValue("$q", consultaFts);
        cmd.Parameters.AddWithValue("$l", limite);
        int i = 0;
        foreach (var a in anexos) cmd.Parameters.AddWithValue("$x" + i++, a);
        using var rd = cmd.ExecuteReader();
        while (rd.Read()) r.Add((rd.GetString(0), rd.GetString(1), -rd.GetDouble(2)));
        return r;
    }
}
