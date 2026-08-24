using Microsoft.Data.Sqlite;
using Telltale.Viewer;

namespace Viewer.Tests;

/// <summary>
/// Runs the SQL that <see cref="TierSql"/> generates against a database shaped
/// like a live one, where the raw tables hold recent data and the 1m rollups
/// hold everything before that. Rows are seeded at each tier's native interval
/// so a missing bucket at the boundary is actually detectable.
/// </summary>
public class TierSqlTests : IDisposable
{
    const long Second = 1_000L;
    const long Minute = 60_000L;
    const long Hour = 3_600_000L;
    /// <summary>Minute-aligned, so no bucket straddles the two tiers.</summary>
    const long Now = 1_699_999_980_000L;

    /// <summary>Where the raw tables take over from the rollups.</summary>
    const long Boundary = Now - 6 * Hour;

    const long RollupStart = Now - 12 * Hour;

    /// <summary>CPU level each side of the boundary holds steadily in the seed.</summary>
    const double RollupCpu = 20.0;
    const double RawCpu = 40.0;

    /// <summary>
    /// A peak stored on the rollup rows that is above their average and below the
    /// raw level, so a query reading the averaged column instead of the stored
    /// maximum is distinguishable from one reading either level.
    /// </summary>
    const double RollupCpuPeak = 35.0;

    /// <summary>Raw samples behind one rollup row: one minute at the 5 second raw interval.</summary>
    const int RollupSampleCount = 12;

    /// <summary>
    /// The boundary sits halfway through the seeded range, so each tier covers the
    /// same amount of time and a correctly time-weighted mean is the midpoint of
    /// the two levels. An unweighted mean lands near the raw level instead,
    /// because the raw tier contributes twelve times as many rows for that time.
    ///
    /// Comparisons against it allow a couple of decimal places: the raw side is
    /// seeded through an inclusive endpoint, so it carries one row more than an
    /// exact half and the true midpoint sits a thousandth above this.
    /// </summary>
    const double TimeWeightedCpu = (RollupCpu + RawCpu) / 2;

    readonly SqliteConnection _conn;

    public TierSqlTests()
    {
        // In-memory, so there is no temp file to leak and no shared pool to clear.
        _conn = new SqliteConnection("Data Source=:memory:");
        _conn.Open();
        Seed();
    }

    public void Dispose() => _conn.Dispose();

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
            CREATE TABLE process_instance (
                id INTEGER PRIMARY KEY, pid INTEGER, name TEXT, path TEXT,
                command_line TEXT, first_seen INTEGER, last_seen INTEGER);
            CREATE TABLE sample (
                ts INTEGER NOT NULL, instance_id INTEGER NOT NULL, cpu_pct REAL,
                private_mb REAL, working_set_mb REAL, io_kb REAL, threads INTEGER, handles INTEGER);
            CREATE TABLE sample_1m (
                ts INTEGER NOT NULL, instance_id INTEGER NOT NULL, cpu_pct_avg REAL,
                cpu_pct_max REAL, private_mb_max REAL, working_set_mb_max REAL,
                io_kb_total REAL, sample_count INTEGER);
            INSERT INTO process_instance (id, pid, name, path, first_seen, last_seen)
                VALUES (1, 100, 'chrome.exe', 'C:\\chrome.exe', 0, 0);
            """);

        using var tx = _conn.BeginTransaction();

        // Rollups cover the older half at their native 1 minute interval,
        // stopping one interval before the raw tables begin.
        for (long ts = RollupStart; ts < Boundary; ts += Minute)
        {
            Execute($"""
                INSERT INTO machine_1m (ts, cpu_pct_avg, cpu_pct_max, memory_avail_mb_avg,
                                        memory_total_mb, commit_mb_max, hard_faults_total,
                                        disk_read_ms_avg, disk_write_ms_avg, disk_busy_pct_avg,
                                        disk_busy_pct_max, net_kbps_avg, gpu_busy_pct_avg,
                                        sample_count)
                VALUES ({ts}, {RollupCpu}, {RollupCpuPeak}, 8000.0, 16000.0, 4000.0, 5, 1.0, 2.0,
                        10.0, 18.0, 100.0, 3.0, {RollupSampleCount});
                INSERT INTO sample_1m (ts, instance_id, cpu_pct_avg, cpu_pct_max,
                                       private_mb_max, working_set_mb_max, io_kb_total, sample_count)
                VALUES ({ts}, 1, {RollupCpu}, {RollupCpuPeak}, 500.0, 600.0, 120.0, {RollupSampleCount});
                """, tx);
        }

        // Raw tables cover the recent half at their native 5 second interval.
        for (long ts = Boundary; ts <= Now; ts += 5 * Second)
        {
            Execute($"""
                INSERT INTO machine (ts, cpu_pct, memory_avail_mb, commit_mb, hard_faults,
                                     disk_read_ms, disk_write_ms, memory_total_mb,
                                     disk_busy_pct, net_kbps, gpu_busy_pct)
                VALUES ({ts}, {RawCpu}, 6000.0, 5000.0, 9, 3.0, 4.0, 16000.0, 30.0, 300.0, 7.0);
                INSERT INTO sample (ts, instance_id, cpu_pct, private_mb, working_set_mb, io_kb, threads, handles)
                VALUES ({ts}, 1, {RawCpu}, 700.0, 800.0, 10.0, 12, 300);
                """, tx);
        }

        tx.Commit();
    }

    void Execute(string sql, SqliteTransaction? tx = null)
    {
        using var cmd = _conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    TierPlan Plan(long from, long to, bool isMachine) =>
        TierSelection.Plan(from, to, isMachine, TierCoverageReader.Read(_conn, isMachine));

    void AddBounds(SqliteCommand cmd, TierSource source)
    {
        foreach (TierBound bound in source.Parameters)
            cmd.Parameters.AddWithValue($"@{bound.Name}", bound.Value);
    }

    /// <summary>Mirrors the /api/timeline query shape.</summary>
    List<long> QueryTimeline(long from, long to)
    {
        var plan = Plan(from, to, isMachine: true);
        TierSource source = TierSql.Source(plan, isMachine: true);

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
                FROM {source.Sql} WHERE ts >= @from AND ts <= @to
                GROUP BY ts / @bucket ORDER BY ts
                """;
            cmd.Parameters.AddWithValue("@bucket", plan.Bucket);
        }
        else
        {
            cmd.CommandText = $"""
                SELECT ts, cpu_pct, memory_avail_mb, commit_mb, hard_faults,
                       disk_read_ms, disk_write_ms, memory_total_mb, disk_busy_pct, net_kbps, gpu_busy_pct
                FROM {source.Sql} WHERE ts >= @from AND ts <= @to ORDER BY ts
                """;
        }

        AddBounds(cmd, source);
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
        var timestamps = QueryTimeline(RollupStart, Boundary - Minute);

        Assert.NotEmpty(timestamps);
    }

    [Fact]
    public void RangeSpanningBoundaryReturnsBothSides()
    {
        var timestamps = QueryTimeline(RollupStart, Now);

        Assert.Contains(timestamps, ts => ts < Boundary);
        Assert.Contains(timestamps, ts => ts >= Boundary);
    }

    [Fact]
    public void RangeSpanningBoundaryHasNoGapAtTheBoundary()
    {
        var timestamps = QueryTimeline(RollupStart, Now);
        var plan = Plan(RollupStart, Now, isMachine: true);

        long lastBefore = timestamps.Where(ts => ts < Boundary).Max();
        long firstAfter = timestamps.Where(ts => ts >= Boundary).Min();

        // Seeded at native density, so consecutive buckets across the boundary
        // must be exactly one bucket apart. A dropped bucket measures two.
        Assert.True(firstAfter - lastBefore <= plan.Bucket,
            $"gap of {firstAfter - lastBefore}ms at the boundary exceeds one bucket ({plan.Bucket}ms)");
    }

    [Fact]
    public void SeriesIsEvenlySpacedAcrossTheWholeRange()
    {
        var timestamps = QueryTimeline(RollupStart, Now);
        var plan = Plan(RollupStart, Now, isMachine: true);

        var gaps = timestamps.Zip(timestamps.Skip(1), (a, b) => b - a).Distinct().ToList();

        Assert.Equal(new[] { plan.Bucket }, gaps);
    }

    [Fact]
    public void TimestampsAreStrictlyIncreasing()
    {
        var timestamps = QueryTimeline(RollupStart, Now);

        Assert.Equal(timestamps.OrderBy(ts => ts), timestamps);
        Assert.Equal(timestamps.Distinct().Count(), timestamps.Count);
    }

    /// <summary>
    /// A range served by one raw tier is still projected rather than named bare.
    /// Callers aggregate with the weight column unconditionally, so it has to
    /// exist whichever tiers were chosen.
    /// </summary>
    [Fact]
    public void RangeInsideRawTierStillCarriesWeightAndPeakColumns()
    {
        var plan = Plan(Boundary, Now, isMachine: true);
        TierSource source = TierSql.Source(plan, isMachine: true);

        Assert.True(plan.IsSingleRawTier);
        Assert.Contains("FROM machine ", source.Sql);
        Assert.Contains("1 AS weight", source.Sql);
        Assert.Contains("cpu_pct AS cpu_pct_peak", source.Sql);
        Assert.NotEmpty(source.Parameters);
    }

    /// <summary>Every raw row stands for itself, so weighting cannot shift a single-tier answer.</summary>
    [Fact]
    public void SingleRawTierWeightingMatchesAPlainAverage()
    {
        var plan = Plan(Boundary, Now, isMachine: false);
        TierSource source = TierSql.Source(plan, isMachine: false);

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT {TierSql.WeightedAvg("s.cpu_pct", "weighted", "s.weight")}, AVG(s.cpu_pct) as plain
            FROM {source.Sql} s WHERE s.ts >= @from AND s.ts <= @to
            """;
        AddBounds(cmd, source);
        cmd.Parameters.AddWithValue("@from", Boundary);
        cmd.Parameters.AddWithValue("@to", Now);

        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(reader.GetDouble(1), reader.GetDouble(0), precision: 6);
        Assert.Equal(RawCpu, reader.GetDouble(0), precision: 6);
    }

    [Fact]
    public void MixedRangeBuildsAUnionOverBothTiers()
    {
        var plan = Plan(RollupStart, Now, isMachine: true);
        TierSource source = TierSql.Source(plan, isMachine: true);

        Assert.Contains("UNION ALL", source.Sql);
        Assert.Contains("FROM machine_1m", source.Sql);
        Assert.Contains("cpu_pct_avg AS cpu_pct", source.Sql);
    }

    [Fact]
    public void SliceBoundsAreParameterisedNotInterpolated()
    {
        var plan = Plan(RollupStart, Now, isMachine: true);
        TierSource source = TierSql.Source(plan, isMachine: true);

        Assert.Equal(plan.Slices.Count * 2, source.Parameters.Count);
        foreach (TierSlice slice in plan.Slices)
        {
            Assert.DoesNotContain(slice.From.ToString(), source.Sql);
            Assert.DoesNotContain(slice.To.ToString(), source.Sql);
        }
    }

    // --- Process side: the four endpoints that read sample tables ---

    /// <summary>Mirrors the grouped /api/processes query shape.</summary>
    [Fact]
    public void GroupedProcessQueryReturnsRowsAcrossBothTiers()
    {
        var plan = Plan(RollupStart, Now, isMachine: false);
        TierSource source = TierSql.Source(plan, isMachine: false);

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT sub.name, AVG(sub.ts_cpu) as avg_cpu_pct, MAX(sub.ts_mem) as peak_private_mb,
                   SUM(sub.ts_io) as total_io_kb, MAX(sub.inst_cnt) as instance_count
            FROM (
                SELECT pi.name, SUM(s.cpu_pct) as ts_cpu, SUM(s.private_mb) as ts_mem,
                       SUM(s.io_kb) as ts_io, COUNT(DISTINCT s.instance_id) as inst_cnt
                FROM {source.Sql} s
                JOIN process_instance pi ON pi.id = s.instance_id
                WHERE s.ts >= @from AND s.ts <= @to
                GROUP BY pi.name, s.ts
            ) sub
            GROUP BY sub.name
            """;
        AddBounds(cmd, source);
        cmd.Parameters.AddWithValue("@from", RollupStart);
        cmd.Parameters.AddWithValue("@to", Now);

        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("chrome.exe", reader.GetString(0));
        Assert.True(reader.GetDouble(1) > 0);
    }

    /// <summary>Mirrors the /api/process/{id} query shape.</summary>
    [Fact]
    public void ProcessDetailQueryReturnsPointsAcrossBothTiers()
    {
        var plan = Plan(RollupStart, Now, isMachine: false);
        TierSource source = TierSql.Source(plan, isMachine: false);

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT (s.ts / @bucket) * @bucket as ts,
                   AVG(s.cpu_pct) as cpu_pct, MAX(s.private_mb) as private_mb,
                   MAX(s.working_set_mb) as working_set_mb, SUM(s.io_kb) as io_kb
            FROM {source.Sql} s
            WHERE s.instance_id = @id AND s.ts >= @from AND s.ts <= @to
            GROUP BY s.ts / @bucket ORDER BY ts
            """;
        cmd.Parameters.AddWithValue("@bucket", plan.Bucket);
        AddBounds(cmd, source);
        cmd.Parameters.AddWithValue("@id", 1);
        cmd.Parameters.AddWithValue("@from", RollupStart);
        cmd.Parameters.AddWithValue("@to", Now);

        var timestamps = new List<long>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) timestamps.Add(reader.GetInt64(0));

        Assert.Contains(timestamps, ts => ts < Boundary);
        Assert.Contains(timestamps, ts => ts >= Boundary);
    }

    /// <summary>
    /// Mirrors the /api/process-group/{name} query shape. The group is totalled
    /// per instant before bucketing, so a bucket holding twelve raw rows reads
    /// the same as one holding a single rollup row for the same CPU level.
    /// </summary>
    [Fact]
    public void ProcessGroupQueryDoesNotStepAtTheTierBoundary()
    {
        var plan = Plan(RollupStart, Now, isMachine: false);
        TierSource source = TierSql.Source(plan, isMachine: false);
        long bucket = plan.Bucket > 0 ? plan.Bucket : 5000;

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT (sub.ts / @bucket) * @bucket as ts,
                   AVG(sub.ts_cpu) as cpu_pct, AVG(sub.ts_mem) as private_mb,
                   AVG(sub.ts_ws) as working_set_mb, SUM(sub.ts_io) as io_kb,
                   MAX(sub.inst_cnt) as instance_count
            FROM (
                SELECT s.ts,
                       SUM(s.cpu_pct) as ts_cpu, SUM(s.private_mb) as ts_mem,
                       SUM(s.working_set_mb) as ts_ws, SUM(s.io_kb) as ts_io,
                       COUNT(DISTINCT s.instance_id) as inst_cnt
                FROM {source.Sql} s
                JOIN process_instance pi ON pi.id = s.instance_id
                WHERE pi.name = @name AND s.ts >= @from AND s.ts <= @to
                GROUP BY s.ts
            ) sub
            GROUP BY sub.ts / @bucket ORDER BY ts
            """;
        AddBounds(cmd, source);
        cmd.Parameters.AddWithValue("@bucket", bucket);
        cmd.Parameters.AddWithValue("@name", "chrome.exe");
        cmd.Parameters.AddWithValue("@from", RollupStart);
        cmd.Parameters.AddWithValue("@to", Now);

        var rollupCpu = new List<double>();
        var rawCpu = new List<double>();
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                long ts = reader.GetInt64(0);
                (ts < Boundary ? rollupCpu : rawCpu).Add(reader.GetDouble(1));
            }
        }

        Assert.NotEmpty(rollupCpu);
        Assert.NotEmpty(rawCpu);

        // The seed holds one instance at a steady level per side, so every
        // bucket must read as that level. Summing rows over a bucket instead
        // would scale with row density and inflate the raw side many times over.
        Assert.All(rollupCpu, cpu => Assert.Equal(20.0, cpu, precision: 6));
        Assert.All(rawCpu, cpu => Assert.Equal(40.0, cpu, precision: 6));
    }

    [Fact]
    public void ProcessTierSourceNormalisesRollupColumnNames()
    {
        var plan = Plan(RollupStart, Now, isMachine: false);
        TierSource source = TierSql.Source(plan, isMachine: false);

        Assert.Contains("private_mb_max AS private_mb", source.Sql);
        Assert.Contains("io_kb_total AS io_kb", source.Sql);
        Assert.Contains("instance_id", source.Sql);
    }

    // --- Weighting rows by the time they cover ---

    /// <summary>
    /// The ungrouped /api/processes average. The seeded range is half rollup and
    /// half raw at two different steady levels, so the honest answer is the
    /// midpoint. Counting rows equally pulls it most of the way to the raw level.
    /// </summary>
    [Fact]
    public void UngroupedProcessAverageIsWeightedByTimeNotRowCount()
    {
        var plan = Plan(RollupStart, Now, isMachine: false);
        TierSource source = TierSql.Source(plan, isMachine: false);

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT {TierSql.WeightedAvg("s.cpu_pct", "weighted", "s.weight")},
                   AVG(s.cpu_pct) as unweighted
            FROM {source.Sql} s
            JOIN process_instance pi ON pi.id = s.instance_id
            WHERE s.ts >= @from AND s.ts <= @to
            GROUP BY pi.id
            """;
        AddBounds(cmd, source);
        cmd.Parameters.AddWithValue("@from", RollupStart);
        cmd.Parameters.AddWithValue("@to", Now);

        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        double weighted = reader.GetDouble(0);
        double unweighted = reader.GetDouble(1);

        Assert.Equal(TimeWeightedCpu, weighted, precision: 2);

        // Guards the test itself: if the seed ever stopped exercising the bias,
        // the assertion above would pass for the wrong reason.
        Assert.True(unweighted > TimeWeightedCpu + 5,
            $"seed no longer exercises the bias: unweighted {unweighted} is not far from {TimeWeightedCpu}");
    }

    /// <summary>The grouped /api/processes shape, which weights per instant rather than per row.</summary>
    [Fact]
    public void GroupedProcessAverageIsWeightedByTimeNotRowCount()
    {
        var plan = Plan(RollupStart, Now, isMachine: false);
        TierSource source = TierSql.Source(plan, isMachine: false);

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT sub.name,
                   {TierSql.AvgOfWeightedTotals("sub.ts_cpu_weighted", "sub.ts_weight", "avg_cpu_pct")},
                   AVG(sub.ts_cpu) as unweighted
            FROM (
                SELECT pi.name, SUM(s.cpu_pct) as ts_cpu,
                       {TierSql.WeightedTotal("s.cpu_pct", "ts_cpu_weighted")},
                       {TierSql.InstantWeight} as ts_weight
                FROM {source.Sql} s
                JOIN process_instance pi ON pi.id = s.instance_id
                WHERE s.ts >= @from AND s.ts <= @to
                GROUP BY pi.name, s.ts
            ) sub
            GROUP BY sub.name
            """;
        AddBounds(cmd, source);
        cmd.Parameters.AddWithValue("@from", RollupStart);
        cmd.Parameters.AddWithValue("@to", Now);

        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());

        Assert.Equal(TimeWeightedCpu, reader.GetDouble(1), precision: 2);
        Assert.True(reader.GetDouble(2) > TimeWeightedCpu + 5);
    }

    /// <summary>
    /// Shifting the window changes how much of it each tier serves. A time-weighted
    /// mean should follow that shift in proportion to the time involved, which here
    /// means tracking the seeded levels exactly. Counting rows equally barely
    /// responds at all, because the raw tier already dominates the row count in
    /// both windows: an hour of real composition change moves it about one point
    /// where the honest answer moves five.
    /// </summary>
    [Fact]
    public void AverageTracksTimeProportionAsTheWindowShifts()
    {
        (double Weighted, double Unweighted) Average(long from, long to)
        {
            var plan = Plan(from, to, isMachine: false);
            TierSource source = TierSql.Source(plan, isMachine: false);

            using var cmd = _conn.CreateCommand();
            cmd.CommandText = $"""
                SELECT {TierSql.WeightedAvg("s.cpu_pct", "weighted", "s.weight")},
                       AVG(s.cpu_pct) as unweighted
                FROM {source.Sql} s WHERE s.ts >= @from AND s.ts <= @to
                """;
            AddBounds(cmd, source);
            cmd.Parameters.AddWithValue("@from", from);
            cmd.Parameters.AddWithValue("@to", to);

            using var reader = cmd.ExecuteReader();
            Assert.True(reader.Read());
            return (reader.GetDouble(0), reader.GetDouble(1));
        }

        // Both windows are four hours wide and straddle the boundary. The first
        // splits evenly; the second draws an hour more from the raw side.
        var centred = Average(Boundary - 2 * Hour, Boundary + 2 * Hour);
        var shifted = Average(Boundary - 1 * Hour, Boundary + 3 * Hour);

        // Two hours at 20 and two at 40, then one hour at 20 and three at 40.
        Assert.Equal((RollupCpu * 2 + RawCpu * 2) / 4, centred.Weighted, precision: 2);
        Assert.Equal((RollupCpu * 1 + RawCpu * 3) / 4, shifted.Weighted, precision: 2);

        double weightedMove = Math.Abs(shifted.Weighted - centred.Weighted);
        double unweightedMove = Math.Abs(shifted.Unweighted - centred.Unweighted);

        Assert.True(weightedMove > 4 * unweightedMove,
            $"weighted moved {weightedMove} and unweighted {unweightedMove}; the "
            + "unweighted mean is supposed to be the one that ignores the real time mix");
    }

    // --- Peaks compared like with like ---

    /// <summary>
    /// A maximum over a mixed range previously took raw 5 second peaks on one side
    /// and 1 minute averages on the other, so the recent half looked peakier for
    /// no real reason. The rollup side must report its stored maximum.
    /// </summary>
    [Fact]
    public void PeakReadsTheStoredRollupMaximumNotItsAverage()
    {
        var plan = Plan(RollupStart, Boundary - Minute, isMachine: false);
        TierSource source = TierSql.Source(plan, isMachine: false);

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT MAX(s.cpu_pct_peak) as peak, MAX(s.cpu_pct) as averaged
            FROM {source.Sql} s WHERE s.ts >= @from AND s.ts <= @to
            """;
        AddBounds(cmd, source);
        cmd.Parameters.AddWithValue("@from", RollupStart);
        cmd.Parameters.AddWithValue("@to", Boundary - Minute);

        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());

        Assert.Equal(RollupCpuPeak, reader.GetDouble(0), precision: 6);
        Assert.Equal(RollupCpu, reader.GetDouble(1), precision: 6);
    }

    /// <summary>The machine tables carry a stored maximum for CPU and disk busy too.</summary>
    [Fact]
    public void MachinePeakReadsTheStoredRollupMaximum()
    {
        var plan = Plan(RollupStart, Boundary - Minute, isMachine: true);
        TierSource source = TierSql.Source(plan, isMachine: true);

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT MAX(cpu_pct_peak), MAX(disk_busy_pct_peak)
            FROM {source.Sql} WHERE ts >= @from AND ts <= @to
            """;
        AddBounds(cmd, source);
        cmd.Parameters.AddWithValue("@from", RollupStart);
        cmd.Parameters.AddWithValue("@to", Boundary - Minute);

        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());

        Assert.Equal(RollupCpuPeak, reader.GetDouble(0), precision: 6);
        Assert.Equal(18.0, reader.GetDouble(1), precision: 6);
    }

    /// <summary>
    /// The /api/alerts sample count. Summing weights answers "how many raw samples
    /// does this stand for", which is what the field claims to be; counting rows
    /// mixes rows covering five seconds with rows covering a minute.
    /// </summary>
    [Fact]
    public void AlertSampleCountSumsRawSamplesRepresented()
    {
        var plan = Plan(RollupStart, Now, isMachine: false);
        TierSource source = TierSql.Source(plan, isMachine: false);

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT SUM(s.weight) as represented, COUNT(*) as rows_read
            FROM {source.Sql} s WHERE s.ts >= @from AND s.ts <= @to
            """;
        AddBounds(cmd, source);
        cmd.Parameters.AddWithValue("@from", RollupStart);
        cmd.Parameters.AddWithValue("@to", Now);

        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        long represented = reader.GetInt64(0);
        long rowsRead = reader.GetInt64(1);

        long rollupRows = (Boundary - RollupStart) / Minute;
        long rawRows = rowsRead - rollupRows;

        Assert.Equal(rollupRows * RollupSampleCount + rawRows, represented);
        Assert.True(represented > rowsRead);
    }
}
