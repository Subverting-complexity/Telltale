using Microsoft.Data.Sqlite;

namespace Viewer.Tests;

/// <summary>
/// Seeds two processes whose ranking depends on which question is asked.
///
/// <c>steady.exe</c> burns a constant share of a core for the whole window and
/// wins on the average. <c>spiky.exe</c> does nothing at all until the final
/// reading, where it takes far more than steady ever did, and wins on the
/// instant. A latest-reading query that quietly aggregates over the range would
/// return them the other way round, which is the mistake worth catching.
/// </summary>
public class LatestReadingTestFactory : TelltaleTestFactory
{
    public const string SteadyName = "steady.exe";
    public const string SpikyName = "spiky.exe";

    public const int SampleCount = 60;
    public const long IntervalMs = 5_000;

    public const double SteadyCpuPct = 10.0;
    public const double SteadyPrivateMb = 100.0;
    public const double SteadyIoKb = 4.0;

    /// <summary>What spiky.exe uses at every reading except the last one.</summary>
    public const double SpikyIdleCpuPct = 0.0;

    /// <summary>What spiky.exe uses at the final reading.</summary>
    public const double SpikyPeakCpuPct = 90.0;
    public const double SpikyPeakPrivateMb = 800.0;
    public const double SpikyPeakIoKb = 250.0;

    static readonly long _now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>The timestamp of the final reading, aligned to the sample interval.</summary>
    public static long LatestTs => _now - _now % IntervalMs;

    public static long FirstTs => LatestTs - (SampleCount - 1) * IntervalMs;

    /// <summary>A window that ends before the recording starts, so it holds nothing.</summary>
    public static long BeforeAnythingTs => FirstTs - 24 * 3_600_000L;

    public LatestReadingTestFactory() : base(CreateDb())
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

        SeedInstances(conn);
        SeedSamples(conn);

        return path;
    }

    static void SeedInstances(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO process_instance (id, pid, create_time, name, path, first_seen, last_seen)
            VALUES (1, 1001, @start, @steady, NULL, @start, @end),
                   (2, 1002, @start, @spiky,  NULL, @start, @end)
            """;
        cmd.Parameters.AddWithValue("@start", FirstTs);
        cmd.Parameters.AddWithValue("@end", LatestTs);
        cmd.Parameters.AddWithValue("@steady", SteadyName);
        cmd.Parameters.AddWithValue("@spiky", SpikyName);
        cmd.ExecuteNonQuery();
    }

    static void SeedSamples(SqliteConnection conn)
    {
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;

        cmd.CommandText = """
            INSERT INTO sample (ts, instance_id, cpu_pct, private_mb, working_set_mb, io_kb)
            VALUES (@ts, @instance, @cpu, @mem, @mem, @io)
            """;

        var ts = cmd.Parameters.Add("@ts", SqliteType.Integer);
        var instance = cmd.Parameters.Add("@instance", SqliteType.Integer);
        var cpu = cmd.Parameters.Add("@cpu", SqliteType.Real);
        var mem = cmd.Parameters.Add("@mem", SqliteType.Real);
        var io = cmd.Parameters.Add("@io", SqliteType.Real);

        for (int i = 0; i < SampleCount; i++)
        {
            long at = FirstTs + i * IntervalMs;
            bool isLast = i == SampleCount - 1;

            ts.Value = at;
            instance.Value = 1;
            cpu.Value = SteadyCpuPct;
            mem.Value = SteadyPrivateMb;
            io.Value = SteadyIoKb;
            cmd.ExecuteNonQuery();

            ts.Value = at;
            instance.Value = 2;
            cpu.Value = isLast ? SpikyPeakCpuPct : SpikyIdleCpuPct;
            mem.Value = isLast ? SpikyPeakPrivateMb : 0.0;
            io.Value = isLast ? SpikyPeakIoKb : 0.0;
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
    }

    // Cleanup handled by TelltaleTestFactory.Dispose.
}
