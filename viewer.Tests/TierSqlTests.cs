using Microsoft.Data.Sqlite;
using Telltale.Viewer;

namespace Viewer.Tests;

/// <summary>
/// Runs the SQL that <see cref="TierSql"/> generates against a database shaped
/// like a live one, where the raw table holds the last 24 hours and the 1m
/// rollup holds everything before that.
/// </summary>
public class TierSqlTests : IDisposable
{
    const long Minute = 60_000L;
    const long Day = 86_400_000L;
    const long Now = 1_700_000_000_000L;
    const long Boundary = Now - Day;

    readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"telltale-tiersql-{Guid.NewGuid():N}.db");
    readonly SqliteConnection _conn;

    public TierSqlTests()
    {
        _conn = new SqliteConnection($"Data Source={_dbPath}");
        _conn.Open();
        Seed();
    }

    public void Dispose()
    {
        _conn.Dispose();
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    void Seed()
    {
        Execute("""
            CREATE TABLE machine (
                ts INTEGER PRIMARY KEY, cpu_pct REAL, memory_avail_mb REAL, commit_mb REAL,
                hard_faults INTEGER, disk_read_ms REAL, disk_write_ms REAL, memory_total_mb REAL,
                disk_busy_pct REAL, net_kbps REAL, gpu_busy_pct REAL);
            CREATE TABLE machine_1m (
                ts INTEGER PRIMARY KEY, cpu_pct_avg REAL, cpu_pct_max REAL, memory_avail_mb_avg REAL,
                memory_total_mb REAL, commit_mb_max REAL, hard_faults_total INTEGER,
                disk_read_ms_avg REAL, disk_write_ms_avg REAL, disk_busy_pct_avg REAL,
                disk_busy_pct_max REAL, net_kbps_avg REAL, gpu_busy_pct_avg REAL, sample_count INTEGER);
            """);

        // Rollup covers the older day, ending one minute before the raw table starts.
        for (long ts = Now - 2 * Day; ts < Boundary; ts += 30 * Minute)
        {
            Execute($"""
                INSERT INTO machine_1m (ts, cpu_pct_avg, memory_avail_mb_avg, memory_total_mb,
                                        commit_mb_max, hard_faults_total, disk_read_ms_avg,
                                        disk_write_ms_avg, disk_busy_pct_avg, net_kbps_avg,
                                        gpu_busy_pct_avg, sample_count)
                VALUES ({ts}, 20.0, 8000.0, 16000.0, 4000.0, 5, 1.0, 2.0, 10.0, 100.0, 3.0, 60)
                """);
        }

        // Raw covers the most recent day.
        for (long ts = Boundary; ts <= Now; ts += 30 * Minute)
        {
            Execute($"""
                INSERT INTO machine (ts, cpu_pct, memory_avail_mb, commit_mb, hard_faults,
                                     disk_read_ms, disk_write_ms, memory_total_mb,
                                     disk_busy_pct, net_kbps, gpu_busy_pct)
                VALUES ({ts}, 40.0, 6000.0, 5000.0, 9, 3.0, 4.0, 16000.0, 30.0, 300.0, 7.0)
                """);
        }
    }

    void Execute(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    Dictionary<string, TierCoverage> Coverage()
    {
        var coverage = new Dictionary<string, TierCoverage>();
        foreach (string table in new[] { "machine", "machine_1m" })
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = $"SELECT MIN(ts), MAX(ts) FROM {table}";
            using var reader = cmd.ExecuteReader();
            if (reader.Read() && !reader.IsDBNull(0))
                coverage[table] = new TierCoverage(reader.GetInt64(0), reader.GetInt64(1));
        }
        return coverage;
    }

    List<long> QueryTimeline(long from, long to)
    {
        var plan = TierSelection.Plan(from, to, isMachine: true, Coverage());
        string source = TierSql.Source(plan, isMachine: true);

        using var cmd = _conn.CreateCommand();
        if (!plan.IsSingleRawTier && plan.Bucket > 0)
        {
            cmd.CommandText = $"""
                SELECT (ts / @bucket) * @bucket as ts,
                       AVG(cpu_pct) as cpu_pct, AVG(memory_avail_mb) as memory_avail_mb,
                       MAX(commit_mb) as commit_mb, SUM(hard_faults) as hard_faults,
                       AVG(disk_read_ms) as disk_read_ms, AVG(disk_write_ms) as disk_write_ms,
                       memory_total_mb, AVG(disk_busy_pct) as disk_busy_pct,
                       AVG(net_kbps) as net_kbps, AVG(gpu_busy_pct) as gpu_busy_pct
                FROM {source} WHERE ts >= @from AND ts <= @to
                GROUP BY ts / @bucket ORDER BY ts
                """;
            cmd.Parameters.AddWithValue("@bucket", plan.Bucket);
        }
        else
        {
            cmd.CommandText = $"""
                SELECT ts, cpu_pct, memory_avail_mb, commit_mb, hard_faults,
                       disk_read_ms, disk_write_ms, memory_total_mb, disk_busy_pct, net_kbps, gpu_busy_pct
                FROM {source} WHERE ts >= @from AND ts <= @to ORDER BY ts
                """;
        }

        cmd.Parameters.AddWithValue("@from", from);
        cmd.Parameters.AddWithValue("@to", to);

        var timestamps = new List<long>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) timestamps.Add(reader.GetInt64(0));
        return timestamps;
    }

    [Fact]
    public void YesterdayReturnsData()
    {
        var timestamps = QueryTimeline(Now - 2 * Day, Boundary);

        Assert.NotEmpty(timestamps);
    }

    [Fact]
    public void RangeSpanningBoundaryReturnsBothSides()
    {
        var timestamps = QueryTimeline(Now - 2 * Day, Now);

        Assert.Contains(timestamps, ts => ts < Boundary);
        Assert.Contains(timestamps, ts => ts >= Boundary);
    }

    [Fact]
    public void RangeSpanningBoundaryHasNoGapAtTheBoundary()
    {
        var timestamps = QueryTimeline(Now - 2 * Day, Now);
        var plan = TierSelection.Plan(Now - 2 * Day, Now, isMachine: true, Coverage());

        long lastBefore = timestamps.Where(ts => ts < Boundary).Max();
        long firstAfter = timestamps.Where(ts => ts >= Boundary).Min();

        // The seeded data is continuous across the boundary, so consecutive
        // points must not be further apart there than the bucket allows.
        Assert.True(firstAfter - lastBefore <= plan.Bucket + 30 * Minute,
            $"gap of {firstAfter - lastBefore}ms at the boundary exceeds the bucket size {plan.Bucket}ms");
    }

    [Fact]
    public void TimestampsAreStrictlyIncreasing()
    {
        var timestamps = QueryTimeline(Now - 2 * Day, Now);

        Assert.Equal(timestamps.OrderBy(ts => ts), timestamps);
        Assert.Equal(timestamps.Distinct().Count(), timestamps.Count);
    }

    [Fact]
    public void RangeInsideRawTierReadsTheTableDirectly()
    {
        var plan = TierSelection.Plan(Boundary, Now, isMachine: true, Coverage());

        Assert.Equal("machine", TierSql.Source(plan, isMachine: true));
    }

    [Fact]
    public void MixedRangeBuildsAUnionOverBothTiers()
    {
        var plan = TierSelection.Plan(Now - 2 * Day, Now, isMachine: true, Coverage());
        string source = TierSql.Source(plan, isMachine: true);

        Assert.Contains("UNION ALL", source);
        Assert.Contains("FROM machine_1m", source);
        Assert.Contains("FROM machine", source);
        Assert.Contains("cpu_pct_avg AS cpu_pct", source);
    }

    [Fact]
    public void ProcessTierSourceNormalisesRollupColumnNames()
    {
        var plan = new TierPlan(
            new[]
            {
                new TierSlice("sample_1m", Now - 2 * Day, Boundary - 1),
                new TierSlice("sample", Boundary, Now),
            },
            60_000);

        string source = TierSql.Source(plan, isMachine: false);

        Assert.Contains("private_mb_max AS private_mb", source);
        Assert.Contains("io_kb_total AS io_kb", source);
        Assert.Contains("instance_id", source);
    }
}
