using System.Globalization;
using Telltale.Collector;

namespace Collector.Tests;

/// <summary>
/// Covers the rollup contract: a bucket is promoted exactly once, whole, and a
/// cycle whose cutoff falls inside a bucket leaves that bucket alone until it is
/// complete. Regression cover for issue #26, where a partly promoted bucket was
/// promoted again on the next cycle and wedged the pipeline permanently.
/// </summary>
public class RollupTests : SqliteTestBase
{
    private const long MinuteMs = 60_000L;

    /// <summary>An arbitrary timestamp sitting exactly on a ten minute boundary.</summary>
    private const long BucketStart = 1_700_000_000_000L / 600_000L * 600_000L;

    public RollupTests() : base("rollup")
    {
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
        Db.RollupSamples(straddling + 20_001, StorageTiers.Raw, StorageTiers.OneMinute, isMachine: true);

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
        Db.RollupSamples(bucket + 30_000, StorageTiers.Raw, StorageTiers.OneMinute, isMachine: true);

        // Second cycle: the bucket is complete. Before the fix this tried to insert
        // the same bucket timestamp a second time, the primary key rejected it, and
        // the whole transaction including the delete rolled back.
        Db.RollupSamples(bucket + MinuteMs + 30_000, StorageTiers.Raw, StorageTiers.OneMinute, isMachine: true);

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

        Db.RollupSamples(cutoff, StorageTiers.Raw, StorageTiers.OneMinute, isMachine: true);
        Db.RollupSamples(cutoff, StorageTiers.Raw, StorageTiers.OneMinute, isMachine: true);

        Assert.Equal([bucket], Timestamps("machine_1m"));
        Assert.Equal(2L, Scalar($"SELECT sample_count FROM machine_1m WHERE ts = {bucket}"));
    }

    [Fact]
    public void Rollup_RowLandingInAnAlreadyPromotedBucket_IsDiscardedNotRePromoted()
    {
        long bucket = BucketStart;
        WriteMachine(bucket, bucket + 30_000);
        Db.RollupSamples(bucket + MinuteMs, StorageTiers.Raw, StorageTiers.OneMinute, isMachine: true);

        // A row arriving late for a bucket that has already been promoted, which is
        // also the shape of a database left wedged by the previous behaviour.
        WriteMachine(bucket + 45_000);

        Db.RollupSamples(bucket + (2 * MinuteMs), StorageTiers.Raw, StorageTiers.OneMinute, isMachine: true);

        Assert.Equal([bucket], Timestamps("machine_1m"));
        Assert.Equal(2L, Scalar($"SELECT sample_count FROM machine_1m WHERE ts = {bucket}"));

        // The late row is still removed, so the raw table cannot grow without bound.
        Assert.Equal(0, Count("machine"));
    }

    [Fact]
    public void Rollup_ProcessSamples_DoNotDuplicateAnAlreadyPromotedBucket()
    {
        long bucket = BucketStart;
        long instanceId = Db.GetOrCreateProcessInstance(4321, 100, "test.exe", null, null, bucket);

        WriteSample(instanceId, bucket, bucket + 30_000);
        Db.RollupSamples(bucket + MinuteMs, StorageTiers.Raw, StorageTiers.OneMinute, isMachine: false);

        WriteSample(instanceId, bucket + 45_000);
        Db.RollupSamples(bucket + (2 * MinuteMs), StorageTiers.Raw, StorageTiers.OneMinute, isMachine: false);

        // Before issue #32 a repeated promotion inserted a second row for the same
        // minute here and silently double counted, because sample_1m had no key on
        // (ts, instance_id). That pair is unique now, so the same mistake would
        // fail outright. This still asserts on the row count rather than on the
        // failure, because the guard being tested is the one in the rollup that
        // stops the second promotion happening at all.
        Assert.Equal(1, Count("sample_1m", $"ts = {bucket} AND instance_id = {instanceId}"));
        Assert.Equal(2L, Scalar($"SELECT sample_count FROM sample_1m WHERE ts = {bucket}"));
        Assert.Equal(0, Count("sample"));
    }

    [Fact]
    public void Rollup_PromotesEveryProcessInABucket_NotJustTheFirst()
    {
        long bucket = BucketStart;
        long[] instances =
        [
            Db.GetOrCreateProcessInstance(1, 100, "one.exe", null, null, bucket),
            Db.GetOrCreateProcessInstance(2, 100, "two.exe", null, null, bucket),
            Db.GetOrCreateProcessInstance(3, 100, "three.exe", null, null, bucket),
        ];

        foreach (long instanceId in instances)
            WriteSample(instanceId, bucket, bucket + 30_000);

        Db.RollupSamples(bucket + MinuteMs, StorageTiers.Raw, StorageTiers.OneMinute, isMachine: false);

        // The bucket exclusion reads the table being inserted into, so a bucket that
        // is new for one process must not read as taken for the next.
        Assert.Equal(3, Count("sample_1m", $"ts = {bucket}"));
        foreach (long instanceId in instances)
            Assert.Equal(2L, Scalar(
                $"SELECT sample_count FROM sample_1m WHERE ts = {bucket} AND instance_id = {instanceId}"));
    }

    [Fact]
    public void Rollup_ProcessAppearingOnlyAfterItsBucketWasPromoted_IsStillPromoted()
    {
        long bucket = BucketStart;
        long early = Db.GetOrCreateProcessInstance(1, 100, "early.exe", null, null, bucket);
        WriteSample(early, bucket);
        Db.RollupSamples(bucket + MinuteMs, StorageTiers.Raw, StorageTiers.OneMinute, isMachine: false);

        // A process first sampled in the second half of a minute that was already
        // promoted. The exclusion is keyed on (ts, instance_id), so this process is
        // promoted rather than discarded along with the whole bucket.
        long late = Db.GetOrCreateProcessInstance(2, 100, "late.exe", null, null, bucket + 45_000);
        WriteSample(late, bucket + 45_000);

        Db.RollupSamples(bucket + (2 * MinuteMs), StorageTiers.Raw, StorageTiers.OneMinute, isMachine: false);

        Assert.Equal(2, Count("sample_1m", $"ts = {bucket}"));
        Assert.Equal(1L, Scalar(
            $"SELECT sample_count FROM sample_1m WHERE ts = {bucket} AND instance_id = {late}"));
        Assert.Equal(1L, Scalar(
            $"SELECT sample_count FROM sample_1m WHERE ts = {bucket} AND instance_id = {early}"));
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
        Db.RollupSamples(second + (4 * MinuteMs), StorageTiers.OneMinute, StorageTiers.TenMinute, isMachine: true);
        Db.RollupSamples(second + (4 * MinuteMs), StorageTiers.OneMinute, StorageTiers.TenMinute, isMachine: true);

        Assert.Equal([first], Timestamps("machine_10m"));
        Assert.Equal(10L, Scalar($"SELECT sample_count FROM machine_10m WHERE ts = {first}"));
        Assert.Equal(4, Count("machine_1m"));
    }

    [Theory]
    [InlineData(0L, 0L)]
    [InlineData(59_999L, 0L)]
    [InlineData(60_000L, 60_000L)]
    [InlineData(60_001L, 60_000L)]
    [InlineData(-1L, -60_000L)]
    [InlineData(-60_000L, -60_000L)]
    [InlineData(-60_001L, -120_000L)]
    public void FloorToBucket_RoundsDownEvenBelowZero(long timestamp, long expected)
    {
        // Rounding toward zero rather than down would put a negative cutoff later
        // than the caller asked for, which is how an incomplete bucket gets through.
        Assert.Equal(expected, Database.FloorToBucket(timestamp, MinuteMs));
    }

    [Fact]
    public void Rollup_RejectsATargetTierThatIsNotCoarserThanTheSource()
    {
        // A promotion only ever gives up detail. Into the same width it would
        // rewrite a tier into itself, and into a finer one it would claim a
        // resolution that was never recorded. The bucket width used to be passed
        // separately from the table names, so a caller could name any three of them
        // and get a statement that ran.
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Db.RollupSamples(BucketStart, StorageTiers.OneMinute, StorageTiers.OneMinute, isMachine: true));

        Assert.Throws<ArgumentOutOfRangeException>(
            () => Db.RollupSamples(BucketStart, StorageTiers.TenMinute, StorageTiers.OneMinute, isMachine: true));
    }

    [Fact]
    public void Rollup_OutOfASummarisedTier_WeightsBySampleCountRatherThanAveragingTheAverages()
    {
        // The shape of the source used to be inferred by testing its table name for
        // "_1m" or "_10m". sample_1h matches neither, so this promotion would have
        // been read as one out of raw: no exception, just an unweighted average over
        // columns that do not hold what that statement believes they hold. The tier
        // now carries its own shape, so the name is never consulted.
        const long HourMs = 3_600_000L;
        const long DayMs = 86_400_000L;

        long day = Database.FloorToBucket(BucketStart, DayMs);
        long id = Db.GetOrCreateProcessInstance(1, 100, "a.exe", null, null, day);

        // One hour holding a single reading of 100, and one holding 600 readings of
        // 0. Weighted by sample_count the day averages well under 1. Averaging the
        // two averages unweighted would give 50.
        WriteHourly(day, id, cpuAvg: 100.0, sampleCount: 1);
        WriteHourly(day + HourMs, id, cpuAvg: 0.0, sampleCount: 600);

        Db.RollupSamples(day + DayMs, StorageTiers.OneHour, StorageTiers.OneDay, isMachine: false);

        double average = Real("SELECT cpu_pct_avg FROM sample_1d");
        Assert.True(average < 1.0,
            $"Expected one reading of 100 to be outweighed by 600 of 0, got {average}.");
    }

    [Fact]
    public void Rollup_ThroughTheWholeLadder_LandsEveryReadingAtTheFloorWithNoneLost()
    {
        // Ageing gives up detail and only detail. Walking the full ladder, every
        // reading should end up accounted for in the coarsest tier rather than
        // deleted somewhere along the way, which is what used to happen to anything
        // older than rollup10mRetentionDays.
        const long WeekMs = 604_800_000L;

        long week = Database.FloorToBucket(BucketStart, WeekMs);
        long[] timestamps = [.. Enumerable.Range(0, 6).Select(i => week + (i * MinuteMs))];
        WriteMachine(timestamps);

        // Far enough past that every bucket at every width is complete.
        long cutoff = week + (2 * WeekMs);

        IReadOnlyList<StorageTier> tiers = StorageTiers.Ordered;
        for (int i = 0; i < tiers.Count - 1; i++)
            Db.RollupSamples(cutoff, tiers[i], tiers[i + 1], isMachine: true);

        foreach (StorageTier tier in tiers.Take(tiers.Count - 1))
            Assert.Equal(0, Count(tier.MachineTable));

        Assert.Equal([week], Timestamps(StorageTiers.OneWeek.MachineTable));
        Assert.Equal((long)timestamps.Length,
            Scalar($"SELECT sample_count FROM {StorageTiers.OneWeek.MachineTable} WHERE ts = {week}"));
    }

    private void WriteHourly(long ts, long instanceId, double cpuAvg, int sampleCount) =>
        Execute($"""
            INSERT INTO sample_1h
                (ts, instance_id, cpu_pct_avg, cpu_pct_max, private_mb_max,
                 working_set_mb_max, io_kb_total, sample_count)
            VALUES ({ts}, {instanceId}, {cpuAvg.ToString(CultureInfo.InvariantCulture)},
                    {cpuAvg.ToString(CultureInfo.InvariantCulture)}, 10, 20, 5, {sampleCount})
            """);

    private void WriteMachine(params long[] timestamps)
    {
        foreach (long ts in timestamps)
            Db.WriteMachineSample(ts,
                new MachineSample(50.0, 8000, 12000, 0, 1.0, 2.0, 16000, 30.0, 1000, null));
    }

    private void WriteSample(long instanceId, params long[] timestamps)
    {
        foreach (long ts in timestamps)
            Db.WriteSampleBatch(ts, [new SampleRow(instanceId, 5.0, 100, 200, 50, 10, 100)]);
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

}
