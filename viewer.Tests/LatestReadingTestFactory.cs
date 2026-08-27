using Microsoft.Data.Sqlite;

namespace Viewer.Tests;

/// <summary>
/// Seeds four processes whose ranking depends on which question is asked.
///
/// <c>steady.exe</c> burns a constant share of a core for the whole window and
/// wins on the average. <c>spiky.exe</c> does nothing at all until the final
/// reading, where it takes far more than steady ever did, and wins on the
/// instant. A latest-reading query that quietly aggregates over the range would
/// return them the other way round, which is the mistake worth catching.
///
/// <c>pair.exe</c> runs as two instances, the second only at the final reading,
/// which is what exercises totalling a group across its instances at one instant
/// rather than in its degenerate one-instance form. <c>gone.exe</c> stops being
/// recorded half way through, so a filter naming it has readings in the window
/// but none at the newest one.
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

    /// <summary>
    /// A memory high-water mark part way through, and deliberately not at the end.
    /// Without it steady.exe holds the same memory at every reading, and an
    /// assertion that the latest form reports the instant rather than the range's
    /// peak would pass either way.
    /// </summary>
    public const double SteadyPeakPrivateMb = 900.0;
    public const int SteadyPeakIndex = 10;

    /// <summary>
    /// A group with two instances, for the two-stage average that totals a group
    /// across its instances at one instant. The second appears only at the final
    /// reading, so the instant's total and the range's average differ sharply.
    /// </summary>
    public const string PairName = "pair.exe";
    public const double PairSteadyCpuPct = 5.0;
    public const double PairLateCpuPct = 25.0;

    /// <summary>
    /// Recorded only through the first half of the window. The name filter must
    /// not move the reading to the newest instant this one was seen at.
    /// </summary>
    public const string GoneName = "gone.exe";
    public const double GoneCpuPct = 2.0;

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

        using var conn = TestConnection.Open(path);

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
                   (2, 1002, @start, @spiky,  NULL, @start, @end),
                   (3, 1003, @start, @pair,   NULL, @start, @end),
                   (4, 1004, @start, @pair,   NULL, @start, @end),
                   (5, 1005, @start, @gone,   NULL, @start, @end)
            """;
        cmd.Parameters.AddWithValue("@start", FirstTs);
        cmd.Parameters.AddWithValue("@end", LatestTs);
        cmd.Parameters.AddWithValue("@steady", SteadyName);
        cmd.Parameters.AddWithValue("@spiky", SpikyName);
        cmd.Parameters.AddWithValue("@pair", PairName);
        cmd.Parameters.AddWithValue("@gone", GoneName);
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
            mem.Value = i == SteadyPeakIndex ? SteadyPeakPrivateMb : SteadyPrivateMb;
            io.Value = SteadyIoKb;
            cmd.ExecuteNonQuery();

            ts.Value = at;
            instance.Value = 2;
            cpu.Value = isLast ? SpikyPeakCpuPct : SpikyIdleCpuPct;
            mem.Value = isLast ? SpikyPeakPrivateMb : 0.0;
            io.Value = isLast ? SpikyPeakIoKb : 0.0;
            cmd.ExecuteNonQuery();

            ts.Value = at;
            instance.Value = 3;
            cpu.Value = PairSteadyCpuPct;
            mem.Value = SteadyPrivateMb;
            io.Value = SteadyIoKb;
            cmd.ExecuteNonQuery();

            // The pair's second instance exists only at the final reading.
            if (isLast)
            {
                ts.Value = at;
                instance.Value = 4;
                cpu.Value = PairLateCpuPct;
                mem.Value = SteadyPrivateMb;
                io.Value = SteadyIoKb;
                cmd.ExecuteNonQuery();
            }

            // gone.exe stops being recorded half way through the window.
            if (i < SampleCount / 2)
            {
                ts.Value = at;
                instance.Value = 5;
                cpu.Value = GoneCpuPct;
                mem.Value = SteadyPrivateMb;
                io.Value = SteadyIoKb;
                cmd.ExecuteNonQuery();
            }
        }

        tx.Commit();
    }

    // Cleanup handled by TelltaleTestFactory.Dispose.
}
