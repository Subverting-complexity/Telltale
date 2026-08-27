using Microsoft.Extensions.Logging.Abstractions;
using Telltale.Collector;

namespace Collector.Tests;

/// <summary>
/// Drives whole rollup cycles against a real database, to show what actually
/// happens to the rows when the capture is over its size limit.
///
/// The policy tests next door cover which boundary moves and when. This covers
/// the thing that matters to whoever recorded the data: that it is still there
/// afterwards, at a coarser width, rather than deleted.
/// </summary>
public class SizePressureCycleTests : SqliteTestBase
{
    private const long HourMs = 3_600_000L;
    private const long DayMs = 24 * HourMs;

    public SizePressureCycleTests() : base("pressure")
    {
    }

    [Fact]
    public void OverTheLimit_TheDailyTiersRowsMoveToTheWeeklyTierRatherThanBeingDeleted()
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // Two hundred days of daily rows, all older than the hourly tier's configured
        // hand-over point, so pressure has somewhere real to push them.
        long[] seeded = [.. Enumerable.Range(0, 200).Select(i => now - ((360L - i) * DayMs))];
        foreach (long ts in seeded)
            InsertDaily(ts, sampleCount: 10);

        long readingsBefore = TotalSampleCount();
        Assert.Equal(2_000, readingsBefore);

        RunOneCycle(maxDatabaseSizeMb: 0);

        // Nothing was deleted. Every reading the daily tier stood for is still
        // accounted for, now inside weekly buckets.
        Assert.Equal(readingsBefore, TotalSampleCount());

        Assert.True(Count("machine_1w") > 0, "The weekly tier should have taken the promoted buckets.");
        Assert.True(Count("machine_1d") < seeded.Length,
            "The daily tier should have handed over the rows that aged past its tightened retention.");

        // And there are fewer rows than there were, which is the point: the same
        // readings in less space.
        Assert.True(Count("machine_1d") + Count("machine_1w") < seeded.Length,
            "Summarising should leave fewer rows standing for the same readings.");
    }

    [Fact]
    public void OverTheLimit_ThePressureItAppliedIsWrittenDown()
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        for (int i = 0; i < 50; i++)
            InsertDaily(now - ((360L - i) * DayMs), sampleCount: 10);

        RunOneCycle(maxDatabaseSizeMb: 0);

        var applied = Db.ReadTierPressure();

        Assert.True(applied.ContainsKey(StorageTiers.OneDay.SampleTable),
            "The tier that gave something up should have said so.");

        // Tightened, and not past the point the hourly tier is configured to hand over.
        Assert.True(applied[StorageTiers.OneDay.SampleTable] < 730 * DayMs);
        Assert.True(applied[StorageTiers.OneDay.SampleTable] >= 180 * DayMs);
    }

    [Fact]
    public void UnderTheLimit_NothingIsTightenedAndNothingMoves()
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        for (int i = 0; i < 20; i++)
            InsertDaily(now - ((360L - i) * DayMs), sampleCount: 10);

        int before = Count("machine_1d");

        RunOneCycle(maxDatabaseSizeMb: 500);

        Assert.Empty(Db.ReadTierPressure());
        Assert.Equal(before, Count("machine_1d"));
        Assert.Equal(0, Count("machine_1w"));
    }

    [Fact]
    public void PressureAlreadyApplied_IsStillInForceOnTheNextCycle()
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        for (int i = 0; i < 50; i++)
            InsertDaily(now - ((360L - i) * DayMs), sampleCount: 10);

        RunOneCycle(maxDatabaseSizeMb: 0);
        long tightened = Db.ReadTierPressure()[StorageTiers.OneDay.SampleTable];

        // A cycle that is comfortably under the limit must not hand the detail back.
        // It cannot: the finer rows were folded away and are not recoverable.
        RunOneCycle(maxDatabaseSizeMb: 500);

        Assert.Equal(tightened, Db.ReadTierPressure()[StorageTiers.OneDay.SampleTable]);
    }

    private void RunOneCycle(int maxDatabaseSizeMb)
    {
        var config = new TelltaleConfig { MaxDatabaseSizeMb = maxDatabaseSizeMb };
        var worker = new RollupWorker(NullLogger<RollupWorker>.Instance, config, Db);

        worker.RunRollup();
    }

    /// <summary>Every reading still accounted for, wherever it now sits.</summary>
    private long TotalSampleCount() =>
        Convert.ToInt64(Scalar("""
            SELECT COALESCE(SUM(sample_count), 0) FROM (
                SELECT sample_count FROM machine_1d
                UNION ALL SELECT sample_count FROM machine_1w
                UNION ALL SELECT sample_count FROM machine_1h
                UNION ALL SELECT sample_count FROM machine_10m
                UNION ALL SELECT sample_count FROM machine_1m)
            """));

    private void InsertDaily(long ts, int sampleCount) =>
        Execute($"""
            INSERT INTO machine_1d
                (ts, cpu_pct_avg, cpu_pct_max, memory_avail_mb_avg, memory_total_mb,
                 commit_mb_max, hard_faults_total, disk_read_ms_avg, disk_write_ms_avg,
                 disk_busy_pct_avg, disk_busy_pct_max, net_kbps_avg, gpu_busy_pct_avg, sample_count)
            VALUES ({ts}, 50.0, 60.0, 8000, 16000, 12000, 0, 1.0, 2.0, 30.0, 40.0, 1000, NULL, {sampleCount})
            """);
}
