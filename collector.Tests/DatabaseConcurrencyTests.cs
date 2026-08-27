using Telltale.Collector;

namespace Collector.Tests;

/// <summary>
/// Regression cover for issue #34. One Database instance is shared by two hosted
/// services running on independent timers, so the sampler writing while a rollup
/// cycle is in progress is the normal case rather than a rare one.
///
/// Before the fix both drove the same SqliteConnection with nothing serialising
/// them. A connection is not thread safe and carries at most one transaction, so
/// two overlapping writes could throw, read back garbage, or apply half of a batch.
/// These tests fail loudly on the old code and are quiet on the new.
/// </summary>
public class DatabaseConcurrencyTests() : SqliteTestBase("concurrency")
{
    private const long Ts = 1_700_000_000_000L;
    private const long MinuteMs = 60_000L;


    [Fact]
    public async Task SamplingWhileARollupRuns_CompletesWithoutErrorAndKeepsEveryRow()
    {
        long id = Db.GetOrCreateProcessInstance(1, 100, "a.exe", null, null, Ts);

        // Two minutes of samples already on disk for the rollup to chew through,
        // plus a sampler writing into the current minute while it does.
        for (int i = 0; i < 240; i++)
            Db.WriteSampleBatch(Ts + (i * 500), [new SampleRow(id, 1.0, 10, 20, 1, 1, 1)]);

        long liveStart = Ts + (3 * MinuteMs);
        const int liveWrites = 300;

        var sampler = Task.Run(() =>
        {
            for (int i = 0; i < liveWrites; i++)
                Db.WriteSampleBatch(liveStart + i, [new SampleRow(id, 2.0, 10, 20, 1, 1, 1)]);
        });

        var rollup = Task.Run(() =>
        {
            for (int i = 0; i < 20; i++)
            {
                Db.RollupSamples(Ts + (2 * MinuteMs), StorageTiers.Raw, StorageTiers.OneMinute, isMachine: false);
                Db.RollupSamples(Ts + (2 * MinuteMs), StorageTiers.Raw, StorageTiers.OneMinute, isMachine: true);
                Db.DeleteOrphanedProcessInstances();
                Db.GetDatabaseSizeBytes();
            }
        });

        // Both tasks are awaited through the same call so a failure in either
        // surfaces here rather than as an unobserved exception.
        await Task.WhenAll(sampler, rollup);

        // Nothing the sampler wrote falls inside the rollup's cutoff, so every live
        // write must still be there. A lost or half applied batch shows up here.
        Assert.Equal(liveWrites, Count("sample", $"ts >= {liveStart}"));
    }

    [Fact]
    public void ManyThreadsWritingAtOnce_PersistEveryRowExactlyOnce()
    {
        const int threads = 8;
        const int perThread = 200;

        long[] ids = new long[threads];
        for (int t = 0; t < threads; t++)
            ids[t] = Db.GetOrCreateProcessInstance(t + 1, 100, $"p{t}.exe", null, null, Ts);

        Parallel.For(0, threads, t =>
        {
            for (int i = 0; i < perThread; i++)
                Db.WriteSampleBatch(Ts + i, [new SampleRow(ids[t], t, 10, 20, 1, 1, 1)]);
        });

        Assert.Equal(threads * perThread, Count("sample"));
        for (int t = 0; t < threads; t++)
            Assert.Equal(perThread, Count("sample", $"instance_id = {ids[t]}"));
    }

    [Fact]
    public void ConcurrentProcessLookups_ProduceOneRowPerProcessNotOnePerCaller()
    {
        const int threads = 8;
        var ids = new long[threads];

        // Every thread reports the same process, which is what happens when the
        // sampler sees a long lived process on consecutive intervals.
        Parallel.For(0, threads, t => ids[t] = Db.GetOrCreateProcessInstance(
            4321, 100, "shared.exe", null, null, Ts + t));

        Assert.Equal(1, Count("process_instance"));
        Assert.Single(ids.Distinct());
    }

    [Fact]
    public async Task RetentionRunningAgainstAnActiveSampler_LeavesTheDatabaseConsistent()
    {
        long id = Db.GetOrCreateProcessInstance(1, 100, "a.exe", null, null, Ts);

        var sampler = Task.Run(() =>
        {
            for (int i = 0; i < 400; i++)
                Db.WriteSampleBatch(Ts + MinuteMs + i, [new SampleRow(id, 1.0, 10, 20, 1, 1, 1)]);
        });

        var housekeeping = Task.Run(() =>
        {
            for (int i = 0; i < 30; i++)
            {
                Db.DeleteOldData("sample", Ts);
                Db.ReadTierPressure();
                Db.IncrementalVacuum();
                Db.WalCheckpoint();
            }
        });

        await Task.WhenAll(sampler, housekeeping);

        // Retention only ever deletes below Ts and the sampler only writes above it,
        // so the two must not have interfered with each other at all.
        Assert.Equal(400, Count("sample"));
    }

}
