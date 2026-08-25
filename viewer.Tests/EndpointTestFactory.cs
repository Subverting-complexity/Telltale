using Microsoft.Data.Sqlite;

namespace Viewer.Tests;

/// <summary>
/// Seeds a database with realistic data across raw and rollup tiers, with
/// timestamps recent enough for /api/alerts to include them. The values are
/// chosen so tests can distinguish a correctly aggregated result from a
/// hand-copy or an unweighted average.
/// </summary>
public class EndpointTestFactory : TelltaleTestFactory
{
    public const string TestProcessName = "testapp.exe";
    public const string TestProcessPath = @"C:\testapp\testapp.exe";
    public const double RawCpuPct = 30.0;
    public const double RawPrivateMb = 700.0;
    public const double RawWorkingSetMb = 900.0;
    public const double RawIoKb = 50.0;

    public const double MachineCpuPct = 25.0;
    public const double MachineMemoryAvailMb = 8000.0;
    public const double MachineDiskBusyPct = 15.0;
    public const double MachineNetKbps = 200.0;

    public const int SampleCount = 60;

    static readonly long _now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public static long Now => _now;
    public static long FourHoursAgo => _now - 4 * 3_600_000L;

    public EndpointTestFactory() : base(CreateDb())
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

        SeedProcessInstance(conn);
        SeedRawData(conn);
        SeedMachineData(conn);

        return path;
    }

    static void SeedProcessInstance(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO process_instance (id, pid, create_time, name, path, first_seen, last_seen)
            VALUES (1, 1234, @createTime, @name, @path, @firstSeen, @lastSeen)
            """;
        cmd.Parameters.AddWithValue("@createTime", FourHoursAgo);
        cmd.Parameters.AddWithValue("@name", TestProcessName);
        cmd.Parameters.AddWithValue("@path", TestProcessPath);
        cmd.Parameters.AddWithValue("@firstSeen", FourHoursAgo);
        cmd.Parameters.AddWithValue("@lastSeen", _now);
        cmd.ExecuteNonQuery();
    }

    static void SeedRawData(SqliteConnection conn)
    {
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;

        cmd.CommandText = """
            INSERT INTO sample (ts, instance_id, cpu_pct, private_mb, working_set_mb, io_kb)
            VALUES (@ts, 1, @cpu, @mem, @ws, @io)
            """;

        var tsParam = cmd.Parameters.Add("@ts", SqliteType.Integer);
        cmd.Parameters.AddWithValue("@cpu", RawCpuPct);
        cmd.Parameters.AddWithValue("@mem", RawPrivateMb);
        cmd.Parameters.AddWithValue("@ws", RawWorkingSetMb);
        cmd.Parameters.AddWithValue("@io", RawIoKb);

        long start = _now - SampleCount * 5_000L;
        for (int i = 0; i < SampleCount; i++)
        {
            tsParam.Value = start + i * 5_000L;
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
    }

    static void SeedMachineData(SqliteConnection conn)
    {
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;

        cmd.CommandText = """
            INSERT INTO machine (ts, cpu_pct, memory_avail_mb, commit_mb, hard_faults,
                                 disk_read_ms, disk_write_ms, memory_total_mb,
                                 disk_busy_pct, net_kbps, gpu_busy_pct)
            VALUES (@ts, @cpu, @mem, 4000.0, 5, 2.0, 3.0, 16000.0, @disk, @net, 4.0)
            """;

        var tsParam = cmd.Parameters.Add("@ts", SqliteType.Integer);
        cmd.Parameters.AddWithValue("@cpu", MachineCpuPct);
        cmd.Parameters.AddWithValue("@mem", MachineMemoryAvailMb);
        cmd.Parameters.AddWithValue("@disk", MachineDiskBusyPct);
        cmd.Parameters.AddWithValue("@net", MachineNetKbps);

        long start = _now - SampleCount * 5_000L;
        for (int i = 0; i < SampleCount; i++)
        {
            tsParam.Value = start + i * 5_000L;
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (!disposing) return;

        SqliteConnection.ClearAllPools();

        try
        {
            Directory.Delete(Path.GetDirectoryName(DbPath)!, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
