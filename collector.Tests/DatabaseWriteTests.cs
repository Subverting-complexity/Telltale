using Telltale.Collector;

namespace Collector.Tests;

/// <summary>
/// Covers the collector's write path: process identity, sample batches, machine
/// samples and health rows. These are the statements every capture depends on and
/// nothing exercised them before, which is what issue #38 was about.
/// </summary>
public class DatabaseWriteTests() : SqliteTestBase("write")
{
    private const long Ts = 1_700_000_000_000L;


    [Fact]
    public void GetOrCreateProcessInstance_InsertsTheProcessTheFirstTime()
    {
        long id = Db.GetOrCreateProcessInstance(4321, 100, "app.exe", @"C:\app.exe", "app --run", Ts);

        Assert.Equal(1, Count("process_instance"));
        Assert.Equal("app.exe", Scalar($"SELECT name FROM process_instance WHERE id = {id}"));
        Assert.Equal(@"C:\app.exe", Scalar($"SELECT path FROM process_instance WHERE id = {id}"));
        Assert.Equal(Ts, Convert.ToInt64(Scalar($"SELECT first_seen FROM process_instance WHERE id = {id}")));
    }

    [Fact]
    public void GetOrCreateProcessInstance_ReturnsTheSameRowAndMovesLastSeenOnRepeat()
    {
        long first = Db.GetOrCreateProcessInstance(4321, 100, "app.exe", null, null, Ts);
        long second = Db.GetOrCreateProcessInstance(4321, 100, "app.exe", null, null, Ts + 5_000);

        Assert.Equal(first, second);
        Assert.Equal(1, Count("process_instance"));
        Assert.Equal(Ts, Convert.ToInt64(Scalar($"SELECT first_seen FROM process_instance WHERE id = {first}")));
        Assert.Equal(Ts + 5_000, Convert.ToInt64(Scalar($"SELECT last_seen FROM process_instance WHERE id = {first}")));
    }

    [Fact]
    public void GetOrCreateProcessInstance_TreatsAReusedPidWithANewStartTimeAsADifferentProcess()
    {
        long original = Db.GetOrCreateProcessInstance(4321, 100, "app.exe", null, null, Ts);
        long recycled = Db.GetOrCreateProcessInstance(4321, 200, "other.exe", null, null, Ts + 1_000);

        // Windows reuses process ids, so identity is (pid, create_time). Collapsing
        // these two would attribute one process's samples to the other.
        Assert.NotEqual(original, recycled);
        Assert.Equal(2, Count("process_instance"));
    }

    [Fact]
    public void GetOrCreateProcessInstance_StoresNullPathAndCommandLineAsNull()
    {
        long id = Db.GetOrCreateProcessInstance(1, 100, "protected.exe", null, null, Ts);

        // A command line the collector could not read, or was configured not to
        // record, must land as NULL rather than an empty string.
        Assert.Equal(1, Count("process_instance", $"id = {id} AND path IS NULL AND command_line IS NULL"));
    }

    [Fact]
    public void WriteSampleBatch_PersistsEveryRowItIsGiven()
    {
        long a = Db.GetOrCreateProcessInstance(1, 100, "a.exe", null, null, Ts);
        long b = Db.GetOrCreateProcessInstance(2, 100, "b.exe", null, null, Ts);

        Db.WriteSampleBatch(Ts,
        [
            new SampleRow(a, 12.5, 100.0, 200.0, 30.0, 8, 120),
            new SampleRow(b, 7.5, 50.0, 75.0, 10.0, 4, 60),
        ]);

        Assert.Equal(2, Count("sample", $"ts = {Ts}"));
        Assert.Equal(12.5, Real($"SELECT cpu_pct FROM sample WHERE instance_id = {a}"));
        Assert.Equal(200.0, Real($"SELECT working_set_mb FROM sample WHERE instance_id = {a}"));
        Assert.Equal(60L, Convert.ToInt64(Scalar($"SELECT handles FROM sample WHERE instance_id = {b}")));
    }

    [Fact]
    public void WriteSampleBatch_StoresMissingCpuAndIoAsNullRatherThanZero()
    {
        long id = Db.GetOrCreateProcessInstance(1, 100, "a.exe", null, null, Ts);

        Db.WriteSampleBatch(Ts, [new SampleRow(id, null, 100.0, 200.0, null, 8, 120)]);

        // A reading the sampler could not take is not the same as a reading of zero,
        // and averaging the two differs.
        Assert.Equal(1, Count("sample", "cpu_pct IS NULL AND io_kb IS NULL"));
    }

    [Fact]
    public void WriteSampleBatch_AppendsOnRepeatedCallsRatherThanReplacing()
    {
        long id = Db.GetOrCreateProcessInstance(1, 100, "a.exe", null, null, Ts);

        Db.WriteSampleBatch(Ts, [new SampleRow(id, 1.0, 10, 20, 1, 1, 1)]);
        Db.WriteSampleBatch(Ts + 5_000, [new SampleRow(id, 2.0, 10, 20, 1, 1, 1)]);
        Db.WriteSampleBatch(Ts + 10_000, [new SampleRow(id, 3.0, 10, 20, 1, 1, 1)]);

        Assert.Equal(3, Count("sample"));
        Assert.Equal([Ts, Ts + 5_000, Ts + 10_000], Timestamps("sample"));
    }

    [Fact]
    public void WriteSampleBatch_WithNoRows_IsHarmless()
    {
        // The sampler filters by threshold, so an interval where nothing qualified
        // reaches this with an empty list.
        Db.WriteSampleBatch(Ts, []);

        Assert.Equal(0, Count("sample"));
    }

    [Fact]
    public void WriteMachineSample_PersistsEveryColumn()
    {
        Db.WriteMachineSample(Ts, new MachineSample(25.0, 8000, 12000, 3, 1.5, 2.5, 16000, 30.0, 1024, 55.0));

        Assert.Equal(25.0, Real($"SELECT cpu_pct FROM machine WHERE ts = {Ts}"));
        Assert.Equal(16000.0, Real($"SELECT memory_total_mb FROM machine WHERE ts = {Ts}"));
        Assert.Equal(55.0, Real($"SELECT gpu_busy_pct FROM machine WHERE ts = {Ts}"));
        Assert.Equal(3L, Convert.ToInt64(Scalar($"SELECT hard_faults FROM machine WHERE ts = {Ts}")));
    }

    [Fact]
    public void WriteMachineSample_ForATimestampAlreadyPresent_ReplacesRatherThanDuplicates()
    {
        Db.WriteMachineSample(Ts, new MachineSample(25.0, 8000, 12000, 0, 1.0, 2.0, 16000, 30.0, 1000, null));
        Db.WriteMachineSample(Ts, new MachineSample(80.0, 4000, 14000, 9, 3.0, 4.0, 16000, 90.0, 2000, null));

        // machine keys on ts, so a repeated write is the later reading winning, not
        // a second row that would double count in the rollup.
        Assert.Equal(1, Count("machine"));
        Assert.Equal(80.0, Real($"SELECT cpu_pct FROM machine WHERE ts = {Ts}"));
    }

    [Fact]
    public void WriteMachineSample_StoresCountersTheSamplerCouldNotReadAsNull()
    {
        Db.WriteMachineSample(Ts, new MachineSample(25.0, 8000, 12000, 0, 1.0, 2.0, 16000, 30.0, 1000, null));

        // A machine with no GPU counter available is the normal case, not an error.
        Assert.Equal(1, Count("machine", "gpu_busy_pct IS NULL"));
    }

    [Fact]
    public void WriteCollectorHealth_PersistsAndReplacesOnTheSameTimestamp()
    {
        Db.WriteCollectorHealth(Ts, 1.5, 40.0, 12.0, 300, 120);
        Db.WriteCollectorHealth(Ts, 2.5, 45.0, 15.0, 310, 130);

        Assert.Equal(1, Count("collector_health"));
        Assert.Equal(2.5, Real($"SELECT cpu_pct FROM collector_health WHERE ts = {Ts}"));
        Assert.Equal(310L, Convert.ToInt64(Scalar($"SELECT process_count FROM collector_health WHERE ts = {Ts}")));
    }

    [Fact]
    public void UpsertProcessInstances_InsertsEveryProcessTheFirstTime()
    {
        var ids = Db.UpsertProcessInstances(
        [
            new ProcessInstanceUpsert(1, 100, "a.exe", @"C:\a.exe", "a --run"),
            new ProcessInstanceUpsert(2, 100, "b.exe", null, null),
        ], Ts);

        Assert.Equal(2, Count("process_instance"));
        Assert.Equal(2, ids.Count);
        Assert.NotEqual(ids[(1, 100)], ids[(2, 100)]);
        Assert.Equal(@"C:\a.exe", Scalar("SELECT path FROM process_instance WHERE pid = 1"));
        Assert.Equal(DBNull.Value, Scalar("SELECT path FROM process_instance WHERE pid = 2"));
    }

    [Fact]
    public void UpsertProcessInstances_ReturnsTheSameRowAndMovesLastSeenOnASecondTick()
    {
        var first = Db.UpsertProcessInstances(
            [new ProcessInstanceUpsert(1, 100, "a.exe", null, null)], Ts);
        var second = Db.UpsertProcessInstances(
            [new ProcessInstanceUpsert(1, 100, "a.exe", null, null)], Ts + 5_000);

        Assert.Equal(1, Count("process_instance"));
        Assert.Equal(first[(1, 100)], second[(1, 100)]);
        Assert.Equal(Ts, Convert.ToInt64(Scalar("SELECT first_seen FROM process_instance")));
        Assert.Equal(Ts + 5_000, Convert.ToInt64(Scalar("SELECT last_seen FROM process_instance")));
    }

    [Fact]
    public void UpsertProcessInstances_TreatsAReusedPidWithANewStartTimeAsADifferentProcess()
    {
        var ids = Db.UpsertProcessInstances(
        [
            new ProcessInstanceUpsert(4321, 100, "app.exe", null, null),
            new ProcessInstanceUpsert(4321, 200, "other.exe", null, null),
        ], Ts);

        Assert.Equal(2, Count("process_instance"));
        Assert.NotEqual(ids[(4321, 100)], ids[(4321, 200)]);
    }

    [Fact]
    public void UpsertProcessInstances_ResolvesToTheRowTheSingleProcessPathWouldHaveUsed()
    {
        long expected = Db.GetOrCreateProcessInstance(7, 100, "a.exe", null, null, Ts);

        var ids = Db.UpsertProcessInstances(
            [new ProcessInstanceUpsert(7, 100, "a.exe", null, null)], Ts + 5_000);

        Assert.Equal(1, Count("process_instance"));
        Assert.Equal(expected, ids[(7, 100)]);
    }

    [Fact]
    public void UpsertProcessInstances_CountsARepeatedKeyWithinOneTickOnlyOnce()
    {
        var ids = Db.UpsertProcessInstances(
        [
            new ProcessInstanceUpsert(1, 100, "a.exe", null, null),
            new ProcessInstanceUpsert(1, 100, "a.exe", null, null),
        ], Ts);

        Assert.Equal(1, Count("process_instance"));
        Assert.Single(ids);
    }

    [Fact]
    public void UpsertProcessInstances_WritesNothingWhenTheTickSawNoProcesses()
    {
        var ids = Db.UpsertProcessInstances([], Ts);

        Assert.Empty(ids);
        Assert.Equal(0, Count("process_instance"));
    }

    [Fact]
    public void WriteTickPhases_PersistsAndReplacesOnTheSameTimestamp()
    {
        Db.WriteTickPhases(Ts, new TickPhaseTimings(1, 2, 3, 4, 5, 6));
        Db.WriteTickPhases(Ts, new TickPhaseTimings(10, 20, 30, 40, 50, 60));

        Assert.Equal(1, Count("collector_tick_phase"));
        Assert.Equal(10, Real($"SELECT sampler_ms FROM collector_tick_phase WHERE ts = {Ts}"));
        Assert.Equal(20, Real($"SELECT machine_sample_ms FROM collector_tick_phase WHERE ts = {Ts}"));
        Assert.Equal(30, Real($"SELECT identity_ms FROM collector_tick_phase WHERE ts = {Ts}"));
        Assert.Equal(40, Real($"SELECT instance_ms FROM collector_tick_phase WHERE ts = {Ts}"));
        Assert.Equal(50, Real($"SELECT sample_write_ms FROM collector_tick_phase WHERE ts = {Ts}"));
        Assert.Equal(60, Real($"SELECT machine_write_ms FROM collector_tick_phase WHERE ts = {Ts}"));
    }

    /// <summary>
    /// The first tick of a run has one reading of the collector's processor time
    /// and needs two, so there is no rate to record. That has to reach the column
    /// as null: writing zero is what made this field useless before issue #93,
    /// because a reader cannot tell an idle recorder from an unmeasured one.
    /// </summary>
    [Fact]
    public void WriteCollectorHealth_StoresAnUnmeasuredCpuAsNullRatherThanZero()
    {
        Db.WriteCollectorHealth(Ts, null, 40.0, 12.0, 300, 120);

        Assert.Equal(1, Count("collector_health", "cpu_pct IS NULL"));
    }

    [Fact]
    public void WriteCollectorHealth_StoresAMeasuredCpuOfZeroAsZero()
    {
        Db.WriteCollectorHealth(Ts, 0.0, 40.0, 12.0, 300, 120);

        Assert.Equal(1, Count("collector_health", "cpu_pct = 0"));
    }
}
