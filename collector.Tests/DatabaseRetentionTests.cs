using Telltale.Collector;

namespace Collector.Tests;

/// <summary>
/// Covers how the database is kept from growing without bound: age based deletion,
/// orphan cleanup, the size cap, and incremental vacuum actually returning pages.
///
/// The vacuum case only means anything now that issue #37 is fixed. Before that,
/// auto_vacuum was off and IncrementalVacuum could not reclaim a single page no
/// matter how much had been deleted.
/// </summary>
public class DatabaseRetentionTests() : SqliteTestBase("retention")
{
    private const long Ts = 1_700_000_000_000L;
    private const long HourMs = 3_600_000L;


    [Fact]
    public void DeleteOldData_RemovesOnlyRowsOlderThanTheCutoff()
    {
        Db.WriteMachineSample(Ts, Machine());
        Db.WriteMachineSample(Ts + HourMs, Machine());
        Db.WriteMachineSample(Ts + (2 * HourMs), Machine());

        Db.DeleteOldData("machine", Ts + HourMs);

        // The cutoff is exclusive: the row sitting exactly on it is kept.
        Assert.Equal([Ts + HourMs, Ts + (2 * HourMs)], Timestamps("machine"));
    }

    [Fact]
    public void DeleteOldData_OnATableWithNothingOldEnough_ChangesNothing()
    {
        Db.WriteMachineSample(Ts, Machine());

        Db.DeleteOldData("machine", Ts - HourMs);

        Assert.Equal(1, Count("machine"));
    }

    [Fact]
    public void DeleteOrphanedProcessInstances_RemovesProcessesWithNoSamplesLeft()
    {
        long kept = Db.GetOrCreateProcessInstance(1, 100, "kept.exe", null, null, Ts);
        long orphan = Db.GetOrCreateProcessInstance(2, 100, "gone.exe", null, null, Ts);
        Db.WriteSampleBatch(Ts, [new SampleRow(kept, 1.0, 10, 20, 1, 1, 1)]);

        Db.DeleteOrphanedProcessInstances();

        // Rollup and retention delete samples but not the process rows pointing at
        // them, so without this the identity table grows forever.
        Assert.Equal(1, Count("process_instance"));
        Assert.Equal(1, Count("process_instance", $"id = {kept}"));
        Assert.Equal(0, Count("process_instance", $"id = {orphan}"));
    }

    [Fact]
    public void DeleteOrphanedProcessInstances_KeepsAProcessStillReferencedByARollupTier()
    {
        long id = Db.GetOrCreateProcessInstance(1, 100, "rolled.exe", null, null, Ts);
        Db.WriteSampleBatch(Ts, [new SampleRow(id, 1.0, 10, 20, 1, 1, 1)]);
        Db.RollupSamples(Ts + 60_000, StorageTiers.Raw, StorageTiers.OneMinute, isMachine: false);

        Db.DeleteOrphanedProcessInstances();

        // The raw sample is gone, but sample_1m still names this process, so the
        // viewer would show a row it cannot label if this were deleted.
        Assert.Equal(0, Count("sample"));
        Assert.Equal(1, Count("sample_1m"));
        Assert.Equal(1, Count("process_instance", $"id = {id}"));
    }

    [Fact]
    public void EnforceSizeLimit_UnderTheCap_DeletesNothing()
    {
        SeedTenMinuteRollups(days: 3);
        int before = Count("machine_10m");

        Db.EnforceSizeLimit(long.MaxValue);

        Assert.Equal(before, Count("machine_10m"));
    }

    [Fact]
    public void EnforceSizeLimit_OverTheCap_DropsTheOldestDayOfTheCoarsestTierFirst()
    {
        SeedTenMinuteRollups(days: 3);
        long oldest = Timestamps("machine_10m")[0];

        // A cap of one byte forces both passes, so the tier order is what is on show.
        Db.EnforceSizeLimit(1);

        Assert.DoesNotContain(oldest, Timestamps("machine_10m"));
        Assert.True(Count("machine_10m") > 0,
            "Only the oldest day should go, not the whole tier.");
    }

    [Fact]
    public void GetDatabaseSizeBytes_ReportsAPositiveSizeThatGrowsWithTheData()
    {
        long empty = Db.GetDatabaseSizeBytes();
        Assert.True(empty > 0, "A database with a schema in it cannot be zero bytes.");

        long id = Db.GetOrCreateProcessInstance(1, 100, "a.exe", null, null, Ts);
        for (int i = 0; i < 2_000; i++)
            Db.WriteSampleBatch(Ts + i, [new SampleRow(id, 1.0, 10, 20, 1, 1, 1)]);

        Assert.True(Db.GetDatabaseSizeBytes() > empty,
            "Writing two thousand samples should show up in the page count.");
    }

    [Fact]
    public void IncrementalVacuum_ReturnsPagesFreedByDeletionToTheFilesystem()
    {
        long id = Db.GetOrCreateProcessInstance(1, 100, "a.exe", null, null, Ts);
        for (int i = 0; i < 5_000; i++)
            Db.WriteSampleBatch(Ts + i, [new SampleRow(id, 1.0, 10, 20, 1, 1, 1)]);

        long full = Db.GetDatabaseSizeBytes();
        Db.DeleteOldData("sample", Ts + 5_000);
        long afterDelete = Db.GetDatabaseSizeBytes();

        Db.IncrementalVacuum();
        long afterVacuum = Db.GetDatabaseSizeBytes();

        // Deleting alone only marks pages free; the file keeps its high water mark.
        // This is the assertion that fails outright with the pre-#37 pragma order,
        // because auto_vacuum would be off and there would be no free list to drain.
        Assert.Equal(full, afterDelete);
        Assert.True(afterVacuum < afterDelete,
            $"Incremental vacuum reclaimed nothing: {afterDelete} bytes before, {afterVacuum} after. " +
            "That is the symptom of auto_vacuum being set after journal_mode.");
    }

    [Fact]
    public void WalCheckpoint_Runs()
    {
        long id = Db.GetOrCreateProcessInstance(1, 100, "a.exe", null, null, Ts);
        Db.WriteSampleBatch(Ts, [new SampleRow(id, 1.0, 10, 20, 1, 1, 1)]);

        Db.WalCheckpoint();

        Assert.Equal(1, Count("sample"));
    }

    private static MachineSample Machine() =>
        new(50.0, 8000, 12000, 0, 1.0, 2.0, 16000, 30.0, 1000, null);

    /// <summary>
    /// Seeds whole days of ten minute machine rollups. The size cap deletes a day at
    /// a time, so the data has to span more than one for the tier order to be visible.
    /// </summary>
    private void SeedTenMinuteRollups(int days)
    {
        for (int day = 0; day < days; day++)
        {
            for (int bucket = 0; bucket < 144; bucket++)
            {
                long ts = Ts + (day * 24 * HourMs) + (bucket * 600_000L);
                Execute($"""
                    INSERT INTO machine_10m
                        (ts, cpu_pct_avg, cpu_pct_max, memory_avail_mb_avg, memory_total_mb,
                         commit_mb_max, hard_faults_total, disk_read_ms_avg, disk_write_ms_avg,
                         disk_busy_pct_avg, disk_busy_pct_max, net_kbps_avg, gpu_busy_pct_avg, sample_count)
                    VALUES ({ts}, 50.0, 60.0, 8000, 16000, 12000, 0, 1.0, 2.0, 30.0, 40.0, 1000, NULL, 10)
                    """);
            }
        }
    }

    /// <summary>
    /// The phase breakdown is prunable by timestamp the same way the health row
    /// is, so the two halves of a tick's health record can be kept for the same
    /// span. That they are actually given the same cutoff is wiring in
    /// <c>RollupWorker</c>, which has no test harness, so this covers the half it
    /// can reach: that a cutoff applied to both tables leaves the same rows.
    /// </summary>
    [Fact]
    public void DeleteOldData_PrunesTheTickPhaseTableOnTheSameCutoffAsTheHealthRow()
    {
        Db.WriteCollectorHealth(Ts - HourMs, 1, 10, 5, 300, 100);
        Db.WriteTickPhases(Ts - HourMs, new TickPhaseTimings(1, 2, 3, 4, 5, 6, 7));
        Db.WriteCollectorHealth(Ts, 1, 10, 5, 300, 100);
        Db.WriteTickPhases(Ts, new TickPhaseTimings(1, 2, 3, 4, 5, 6, 7));

        Db.DeleteOldData("collector_health", Ts);
        Db.DeleteOldData("collector_tick_phase", Ts);

        Assert.Equal(1, Count("collector_health"));
        Assert.Equal(1, Count("collector_tick_phase"));
        Assert.Equal(Ts, Convert.ToInt64(Scalar("SELECT ts FROM collector_tick_phase")));
    }
}
