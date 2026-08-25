using Microsoft.Data.Sqlite;

namespace Telltale.Viewer;

/// <summary>
/// Reads the time span each tier table actually holds, so tier selection can be
/// driven by where the data lives rather than by how old the request is.
/// </summary>
public static class TierCoverageReader
{
    public static Dictionary<string, TierCoverage> Read(SqliteConnection conn, bool isMachine)
    {
        var coverage = new Dictionary<string, TierCoverage>();
        IReadOnlyList<string> tiers = TierSelection.TiersFor(isMachine);

        List<string> present = PresentTables(conn, tiers);
        if (present.Count == 0) return coverage;

        using var cmd = conn.CreateCommand();

        // Scalar subqueries per aggregate, rather than MIN(ts), MAX(ts) in one
        // SELECT. Two aggregates in a single result set defeat SQLite's min/max
        // optimisation and scan the whole table; this form seeks the ts index.
        cmd.CommandText = string.Join(" UNION ALL ", present.Select(t =>
            $"SELECT '{t}' AS tier, (SELECT MIN(ts) FROM {t}) AS min_ts, (SELECT MAX(ts) FROM {t}) AS max_ts"));

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (reader.IsDBNull(1) || reader.IsDBNull(2)) continue;
            coverage[reader.GetString(0)] = new TierCoverage(reader.GetInt64(1), reader.GetInt64(2));
        }

        return coverage;
    }

    /// <summary>
    /// Which tier tables this database actually has. An older database predating
    /// the rollup tables is read without error rather than throwing.
    /// </summary>
    static List<string> PresentTables(SqliteConnection conn, IReadOnlyList<string> tiers)
    {
        var present = new List<string>(tiers.Count);

        using var cmd = conn.CreateCommand();
        string names = string.Join(", ", tiers.Select(t => $"'{t}'"));
        cmd.CommandText = $"SELECT name FROM sqlite_master WHERE type='table' AND name IN ({names})";

        using var reader = cmd.ExecuteReader();
        while (reader.Read()) present.Add(reader.GetString(0));

        return present;
    }
}
