using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Telltale.Collector;

namespace Collector.Tests;

/// <summary>
/// Covers the rollup contract: a bucket is promoted exactly once, whole, and a
/// cycle whose cutoff falls inside a bucket leaves that bucket alone until it is
/// complete. Regression cover for issue #26, where a partly promoted bucket was
/// promoted again on the next cycle and wedged the pipeline permanently.
/// </summary>
public class RollupTests : IDisposable
{
    private const long MinuteMs = 60_000L;

    /// <summary>An arbitrary timestamp sitting exactly on a ten minute boundary.</summary>
    private const long BucketStart = 1_700_000_000_000L / 600_000L * 600_000L;

    private readonly string _dbPath;
    private readonly Database _db;

    public RollupTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"telltale_rollup_{Guid.NewGuid()}.db");
        _db = new Database(_dbPath, new SilentLogger());
    }

    [Fact]
    public void Rollup_LeavesTheBucketContainingTheCutoffAlone()
    {
        long earlier = BucketStart;
        long straddling = BucketStart + MinuteMs;

        WriteMachine(earlier, earlier + 30_000);
        WriteMachine(straddling, straddling + 20_000, straddling + 40_000);

        // A cutoff a third of the way into the second minute. Only the first minute
        // is complete, so only the first minute may be promoted.
        _db.RollupSamples(straddling + 20_001, "machine", "machine_1m", 1, isMachine: true);

        Assert.Equal([earlier], Timestamps("machine_1m"));
        Assert.Equal(3, Count("machine"));
        Assert.Equal(0, Count("machine", $"ts < {straddling}"));
    }

    [Fact]
    public void Rollup_RepeatedCyclesAcrossTheSameBucket_DoNotFail()
    {
        long bucket = BucketStart;
        WriteMachine(bucket, bucket + 20_000, bucket + 40_000);

        // First cycle: the cutoff lands inside the bucket. Before the fix this
        // promoted the two rows older than the cutoff and deleted them, leaving the
        // third behind under a bucket timestamp that now existed.
        _db.RollupSamples(bucket + 30_000, "machine", "machine_1m", 1, isMachine: true);

        // Second cycle: the bucket is complete. Before the fix this tried to insert
        // the same bucket timestamp a second time, the primary key rejected it, and
        // the whole transaction including the delete rolled back.
        _db.RollupSamples(bucket + MinuteMs + 30_000, "machine", "machine_1m", 1, isMachine: true);

        Assert.Equal([bucket], Timestamps("machine_1m"));
        Assert.Equal(3L, Scalar($"SELECT sample_count FROM machine_1m WHERE ts = {bucket}"));
        Assert.Equal(0, Count("machine"));
    }

    [Fact]
    public void Rollup_RunTwiceWithTheSameCutoff_IsIdempotent()
    {
        long bucket = BucketStart;
        WriteMachine(bucket, bucket + 30_000);
        long cutoff = bucket + MinuteMs;

        _db.RollupSamples(cutoff, "machine", "machine_1m", 1, isMachine: true);
        _db.RollupSamples(cutoff, "machine", "machine_1m", 1, isMachine: true);

        Assert.Equal([bucket], Timestamps("machine_1m"));
        Assert.Equal(2L, Scalar($"SELECT sample_count FROM machine_1m WHERE ts = {bucket}"));
    }

    [Fact]
    public void Rollup_RowLandingInAnAlreadyPromotedBucket_IsDiscardedNotRePromoted()
    {
        long bucket = BucketStart;
        WriteMachine(bucket, bucket + 30_000);
        _db.RollupSamples(bucket + MinuteMs, "machine", "machine_1m", 1, isMachine: true);

        // A row arriving late for a bucket that has already been promoted, which is
        // also the shape of a database left wedged by the previous behaviour.
        WriteMachine(bucket + 45_000);

        _db.RollupSamples(bucket + (2 * MinuteMs), "machine", "machine_1m", 1, isMachine: true);

        Assert.Equal([bucket], Timestamps("machine_1m"));
        Assert.Equal(2L, Scalar($"SELECT sample_count FROM machine_1m WHERE ts = {bucket}"));

        // The late row is still removed, so the raw table cannot grow without bound.
        Assert.Equal(0, Count("machine"));
    }

    [Fact]
    public void Rollup_ProcessSamples_DoNotDuplicateAnAlreadyPromotedBucket()
    {
        long bucket = BucketStart;
        long instanceId = _db.GetOrCreateProcessInstance(4321, 100, "test.exe", null, null, bucket);

        WriteSample(instanceId, bucket, bucket + 30_000);
        _db.RollupSamples(bucket + MinuteMs, "sample", "sample_1m", 1, isMachine: false);

        WriteSample(instanceId, bucket + 45_000);
        _db.RollupSamples(bucket + (2 * MinuteMs), "sample", "sample_1m", 1, isMachine: false);

        // sample_1m has no key on ts, so a repeated promotion would insert a second
        // row for the same minute and silently double count rather than failing.
        Assert.Equal(1, Count("sample_1m", $"ts = {bucket} AND instance_id = {instanceId}"));
        Assert.Equal(2L, Scalar($"SELECT sample_count FROM sample_1m WHERE ts = {bucket}"));
        Assert.Equal(0, Count("sample"));
    }

    [Fact]
    public void Rollup_TenMinuteReRollup_LeavesAnIncompleteBucketAlone()
    {
        long first = BucketStart;
        long second = BucketStart + 600_000L;

        for (int i = 0; i < 10; i++)
            WriteMachineRollup(first + (i * MinuteMs));
        for (int i = 0; i < 4; i++)
            WriteMachineRollup(second + (i * MinuteMs));

        // A cutoff four minutes into the second ten minute bucket. Only the first is
        // complete. Running twice proves the incomplete one is not half promoted.
        _db.RollupSamples(second + (4 * MinuteMs), "machine_1m", "machine_10m", 10, isMachine: true);
        _db.RollupSamples(second + (4 * MinuteMs), "machine_1m", "machine_10m", 10, isMachine: true);

        Assert.Equal([first], Timestamps("machine_10m"));
        Assert.Equal(10L, Scalar($"SELECT sample_count FROM machine_10m WHERE ts = {first}"));
        Assert.Equal(4, Count("machine_1m"));
    }

    private void WriteMachine(params long[] timestamps)
    {
        foreach (long ts in timestamps)
            _db.WriteMachineSample(ts,
                new MachineSample(50.0, 8000, 12000, 0, 1.0, 2.0, 16000, 30.0, 1000, null));
    }

    private void WriteSample(long instanceId, params long[] timestamps)
    {
        foreach (long ts in timestamps)
            _db.WriteSampleBatch(ts, [new SampleRow(instanceId, 5.0, 100, 200, 50, 10, 100)]);
    }

    /// <summary>
    /// Seeds a one minute rollup row directly. The tier two re-rollup reads this
    /// table rather than the raw one, and no collector API writes it.
    /// </summary>
    private void WriteMachineRollup(long ts) => Execute($"""
        INSERT INTO machine_1m
            (ts, cpu_pct_avg, cpu_pct_max, memory_avail_mb_avg, memory_total_mb,
             commit_mb_max, hard_faults_total, disk_read_ms_avg, disk_write_ms_avg,
             disk_busy_pct_avg, disk_busy_pct_max, net_kbps_avg, gpu_busy_pct_avg, sample_count)
        VALUES ({ts}, 50.0, 60.0, 8000, 16000, 12000, 0, 1.0, 2.0, 30.0, 40.0, 1000, NULL, 1)
        """);

    private long[] Timestamps(string table)
    {
        using var conn = Connect();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT ts FROM {table} ORDER BY ts";
        using var reader = cmd.ExecuteReader();

        var results = new List<long>();
        while (reader.Read())
            results.Add(reader.GetInt64(0));
        return [.. results];
    }

    private int Count(string table, string? where = null) =>
        (int)(long)Scalar($"SELECT COUNT(*) FROM {table}" + (where is null ? "" : $" WHERE {where}"))!;

    private object? Scalar(string sql)
    {
        using var conn = Connect();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    private void Execute(string sql)
    {
        using var conn = Connect();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private SqliteConnection Connect()
    {
        var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        return conn;
    }

    public void Dispose()
    {
        _db.Dispose();
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { File.Delete(_dbPath + suffix); } catch { /* best effort cleanup */ }
        }
        GC.SuppressFinalize(this);
    }

    private sealed class SilentLogger : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => false;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter) { }
    }
}
