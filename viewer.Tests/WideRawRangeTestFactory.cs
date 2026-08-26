using Microsoft.Data.Sqlite;

namespace Viewer.Tests;

/// <summary>
/// A database holding nothing but raw machine rows, covering a window wider than
/// the raw-only exemption allows. This is the shape a machine ends up in when
/// RawRetentionHours is raised, or when the rollup worker stalls and the raw
/// table grows past its retention window.
///
/// Rows go in at ten second spacing rather than the collector's five, which is
/// dense enough that bucketing and not bucketing differ by an order of magnitude
/// in the row count, and sparse enough to seed quickly.
/// </summary>
public class WideRawRangeTestFactory : TelltaleTestFactory
{
    public const long MaxTs = 1_700_000_000_000;

    /// <summary>Comfortably past the roughly 27.8 hour exemption.</summary>
    public const long SpanMs = 40 * 3_600_000L;

    public const long IntervalMs = 10_000;

    public static long MinTs => MaxTs - SpanMs;

    public static long SeededRowCount => SpanMs / IntervalMs + 1;

    public WideRawRangeTestFactory() : base(CreateDb())
    {
    }

    private static string CreateDb()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"telltale-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "telltale.db");

        using var conn = new SqliteConnection($"Data Source={path}");
        conn.Open();

        using (var schemaCmd = conn.CreateCommand())
        {
            schemaCmd.CommandText = File.ReadAllText(
                Path.Combine(AppContext.BaseDirectory, "schema.sql"));
            schemaCmd.ExecuteNonQuery();
        }

        // Generated in SQLite rather than looped from here: 14,401 round trips
        // would dominate the test's runtime.
        using (var insertCmd = conn.CreateCommand())
        {
            insertCmd.CommandText = """
                WITH RECURSIVE series(ts) AS (
                    SELECT @minTs
                    UNION ALL
                    SELECT ts + @interval FROM series WHERE ts + @interval <= @maxTs
                )
                INSERT INTO machine (ts, cpu_pct, memory_avail_mb, commit_mb, hard_faults,
                                     disk_read_ms, disk_write_ms, memory_total_mb,
                                     disk_busy_pct, net_kbps, gpu_busy_pct)
                SELECT ts, 40.0, 6000.0, 5000.0, 9, 3.0, 4.0, 16000.0, 30.0, 300.0, 7.0
                FROM series
                """;
            insertCmd.Parameters.AddWithValue("@minTs", MinTs);
            insertCmd.Parameters.AddWithValue("@maxTs", MaxTs);
            insertCmd.Parameters.AddWithValue("@interval", IntervalMs);
            insertCmd.ExecuteNonQuery();
        }

        return path;
    }

    // Cleanup handled by TelltaleTestFactory.Dispose.
}
