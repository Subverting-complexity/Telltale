using Microsoft.Data.Sqlite;
using Telltale.Viewer;

namespace Viewer.Tests;

/// <summary>
/// Weighting cases the main fixture cannot express, because each needs a database
/// shaped against it: rows with no measured value, and a tier changeover that does
/// not land on a bucket boundary.
/// </summary>
public class TierSqlWeightingEdgeCaseTests : IDisposable
{
    const long Minute = 60_000L;
    const long RawInterval = 5_000L;
    const int RollupSampleCount = 12;

    readonly SqliteConnection _conn;

    public TierSqlWeightingEdgeCaseTests()
    {
        _conn = new SqliteConnection("Data Source=:memory:");
        _conn.Open();
        Execute("""
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
            INSERT INTO process_instance (id, pid, name, first_seen, last_seen)
                VALUES (1, 100, 'chrome.exe', 0, 0);
            """);
    }

    public void Dispose() => _conn.Dispose();

    void Execute(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    TierPlan Plan(long from, long to) =>
        TierSelection.Plan(from, to, isMachine: false, TierCoverageReader.Read(_conn, isMachine: false));

    void AddBounds(SqliteCommand cmd, TierSource source)
    {
        foreach (TierBound bound in source.Parameters)
            cmd.Parameters.AddWithValue($"@{bound.Name}", bound.Value);
    }

    /// <summary>
    /// The collector stores a sample precisely when CPU could not be computed, so a
    /// NULL cpu_pct is ordinary data rather than corruption. Counting such a row's
    /// weight in the divisor while its value contributes nothing to the numerator
    /// would report a process as quieter than it was.
    /// </summary>
    [Fact]
    public void RowsWithNoMeasuredValueAreExcludedFromBothHalves()
    {
        long start = 1_700_000_000_000L;
        Execute($"""
            INSERT INTO sample (ts, instance_id, cpu_pct, private_mb, working_set_mb, io_kb)
            VALUES ({start}, 1, NULL, 100.0, 100.0, 0.0),
                   ({start + RawInterval}, 1, 10.0, 100.0, 100.0, 0.0),
                   ({start + 2 * RawInterval}, 1, 20.0, 100.0, 100.0, 0.0),
                   ({start + 3 * RawInterval}, 1, 30.0, 100.0, 100.0, 0.0);
            """);

        long to = start + 3 * RawInterval;
        TierSource source = TierSql.Source(Plan(start, to), isMachine: false);

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT {TierSql.WeightedAvg("s.cpu_pct", "weighted", "s.weight")}, AVG(s.cpu_pct) as plain
            FROM {source.Sql} s WHERE s.ts >= @from AND s.ts <= @to
            """;
        AddBounds(cmd, source);
        cmd.Parameters.AddWithValue("@from", start);
        cmd.Parameters.AddWithValue("@to", to);

        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());

        // AVG ignores the unmeasured row entirely, and so must the weighted form.
        // Charging its weight against a value that was never taken would read 15.
        Assert.Equal(20.0, reader.GetDouble(0), precision: 6);
        Assert.Equal(reader.GetDouble(1), reader.GetDouble(0), precision: 6);
    }

    /// <summary>The same, where the unmeasured row is a rollup row carrying real weight.</summary>
    [Fact]
    public void AnUnmeasuredRollupRowDoesNotDragTheMeanDown()
    {
        long rollupTs = 1_700_000_000_000L;
        long rawTs = rollupTs + Minute;
        Execute($"""
            INSERT INTO sample_1m (ts, instance_id, cpu_pct_avg, cpu_pct_max,
                                   private_mb_max, working_set_mb_max, io_kb_total, sample_count)
            VALUES ({rollupTs}, 1, NULL, NULL, 100.0, 100.0, 0.0, {RollupSampleCount});
            INSERT INTO sample (ts, instance_id, cpu_pct, private_mb, working_set_mb, io_kb)
            VALUES ({rawTs}, 1, 40.0, 100.0, 100.0, 0.0);
            """);

        TierSource source = TierSql.Source(Plan(rollupTs, rawTs), isMachine: false);

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT {TierSql.WeightedAvg("s.cpu_pct", "weighted", "s.weight")}
            FROM {source.Sql} s WHERE s.ts >= @from AND s.ts <= @to
            """;
        AddBounds(cmd, source);
        cmd.Parameters.AddWithValue("@from", rollupTs);
        cmd.Parameters.AddWithValue("@to", rawTs);

        // Only one row measured anything, so that is the answer. Counting the
        // rollup row's twelve against it would report roughly 3.
        Assert.Equal(40.0, Convert.ToDouble(cmd.ExecuteScalar()), precision: 6);
    }

    /// <summary>
    /// The grouped endpoints average in two stages, totalling a group across its
    /// instances at one instant before averaging over time. An instant where
    /// nothing was measured produces a NULL total, and it has to leave the divisor
    /// as well, exactly as a NULL row does in the single-stage form. A process's
    /// first observation is such an instant, so this is the common case rather
    /// than an exotic one.
    /// </summary>
    [Fact]
    public void AnInstantWhereNothingWasMeasuredLeavesTheTwoStageDivisor()
    {
        long start = 1_700_000_000_000L;
        // First observation carries no CPU, then the process runs at 90%.
        Execute($"""
            INSERT INTO sample (ts, instance_id, cpu_pct, private_mb, working_set_mb, io_kb)
            VALUES ({start}, 1, NULL, 100.0, 100.0, 0.0),
                   ({start + RawInterval}, 1, 90.0, 100.0, 100.0, 0.0),
                   ({start + 2 * RawInterval}, 1, 90.0, 100.0, 100.0, 0.0),
                   ({start + 3 * RawInterval}, 1, 90.0, 100.0, 100.0, 0.0);
            """);

        long to = start + 3 * RawInterval;
        TierSource source = TierSql.Source(Plan(start, to), isMachine: false);

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT {TierSql.AvgOfWeightedTotals("sub.ts_cpu_weighted", "sub.ts_weight", "grouped")}
            FROM (
                SELECT pi.name,
                       {TierSql.WeightedTotal("s.cpu_pct", "ts_cpu_weighted", "s.weight")},
                       {TierSql.InstantWeight("s.weight")} as ts_weight
                FROM {source.Sql} s
                JOIN process_instance pi ON pi.id = s.instance_id
                WHERE s.ts >= @from AND s.ts <= @to
                GROUP BY pi.name, s.ts
            ) sub
            GROUP BY sub.name
            """;
        AddBounds(cmd, source);
        cmd.Parameters.AddWithValue("@from", start);
        cmd.Parameters.AddWithValue("@to", to);

        // The three measured instants all read 90. Charging the unmeasured
        // instant's weight against nothing would report 67.5 instead, and would
        // disagree with the ungrouped endpoint over identical rows.
        Assert.Equal(90.0, Convert.ToDouble(cmd.ExecuteScalar()), precision: 6);
    }

    /// <summary>
    /// A mixed bucket is at least as wide as the coarsest tier interval, which is
    /// why a bucket was assumed to hold one tier only. But the bucket grid is
    /// anchored to the epoch while the raw tier starts at whatever instant the
    /// collector happened to sample, so the bucket containing that instant holds
    /// both. Averaging it unweighted lets the raw rows outvote the rollup row.
    /// </summary>
    [Fact]
    public void TheBucketHoldingTheTierChangeoverIsWeighted()
    {
        // Deliberately not minute-aligned: this is the case the main fixture avoids.
        long rawStart = 1_700_000_000_137L;
        long firstBucket = (rawStart / Minute) * Minute;

        Execute($"""
            INSERT INTO sample_1m (ts, instance_id, cpu_pct_avg, cpu_pct_max,
                                   private_mb_max, working_set_mb_max, io_kb_total, sample_count)
            VALUES ({firstBucket}, 1, 20.0, 25.0, 100.0, 100.0, 0.0, {RollupSampleCount});
            """);

        // Raw rows landing inside that same epoch-anchored minute bucket.
        for (long ts = rawStart; ts < firstBucket + Minute; ts += RawInterval)
        {
            Execute($"""
                INSERT INTO sample (ts, instance_id, cpu_pct, private_mb, working_set_mb, io_kb)
                VALUES ({ts}, 1, 40.0, 100.0, 100.0, 0.0);
                """);
        }

        long to = firstBucket + Minute - 1;
        var plan = Plan(firstBucket, to);
        TierSource source = TierSql.Source(plan, isMachine: false);

        // Both tiers really are in play, otherwise the test proves nothing.
        Assert.True(plan.Slices.Count > 1, "fixture did not produce a mixed plan");

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT {TierSql.WeightedAvg("s.cpu_pct", "weighted", "s.weight")},
                   AVG(s.cpu_pct) as unweighted, COUNT(*) as rows_read
            FROM {source.Sql} s WHERE s.ts >= @from AND s.ts <= @to
            """;
        AddBounds(cmd, source);
        cmd.Parameters.AddWithValue("@from", firstBucket);
        cmd.Parameters.AddWithValue("@to", to);

        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        double weighted = reader.GetDouble(0);
        double unweighted = reader.GetDouble(1);
        long rowsRead = reader.GetInt64(2);

        long rawRows = rowsRead - 1;
        double expected = (20.0 * RollupSampleCount + 40.0 * rawRows) / (RollupSampleCount + rawRows);

        Assert.Equal(expected, weighted, precision: 6);
        Assert.True(unweighted > weighted + 1,
            $"unweighted {unweighted} should overstate the weighted {weighted} in a straddling bucket");
    }
}
