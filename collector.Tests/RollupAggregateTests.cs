using System.Globalization;
using Telltale.Collector;

namespace Collector.Tests;

/// <summary>
/// Covers the values the rollup writes, rather than which buckets it promotes.
///
/// Two defects are pinned here. A weighted average used to charge an unmeasured
/// sample's weight against a value that was never taken, so any ten minute
/// bucket containing a minute with nothing measured was averaged low (issue
/// #42). And the machine tables' memory_total_mb used to be read by a
/// correlated subquery that has been rewritten as a join, so the value each
/// bucket receives needs pinning to the last reading in that bucket
/// specifically (issue #33).
/// </summary>
public class RollupAggregateTests : SqliteTestBase
{
    private const long MinuteMs = 60_000L;
    private const long TenMinutesMs = 600_000L;

    /// <summary>An arbitrary timestamp sitting exactly on a ten minute boundary.</summary>
    private const long BucketStart = 1_700_000_000_000L / TenMinutesMs * TenMinutesMs;

    public RollupAggregateTests() : base("agg")
    {
    }

    // ---- memory_total_mb: the last reading in each bucket (issue #33) ----

    [Fact]
    public void Rollup_GivesEachBucketTheLastTotalMemoryReadingFromThatBucket()
    {
        long first = BucketStart;
        long second = BucketStart + MinuteMs;

        WriteMachine(first, memoryTotalMb: 16_000);
        WriteMachine(first + 30_000, memoryTotalMb: 16_001);
        WriteMachine(second, memoryTotalMb: 32_000);
        WriteMachine(second + 30_000, memoryTotalMb: 32_002);

        Db.RollupSamples(second + MinuteMs, StorageTiers.Raw, StorageTiers.OneMinute, isMachine: true);

        // Each bucket takes its own last reading. A lookup that resolved once for
        // the whole statement rather than once per bucket would give both buckets
        // the same value.
        Assert.Equal(16_001d, Scalar($"SELECT memory_total_mb FROM machine_1m WHERE ts = {first}"));
        Assert.Equal(32_002d, Scalar($"SELECT memory_total_mb FROM machine_1m WHERE ts = {second}"));
    }

    [Fact]
    public void Rollup_KeepsANullWhenTheLastReadingInTheBucketHasNoTotalMemory()
    {
        long bucket = BucketStart;

        WriteMachine(bucket, memoryTotalMb: 16_000);
        WriteMachine(bucket + 30_000, memoryTotalMb: null);

        Db.RollupSamples(bucket + MinuteMs, StorageTiers.Raw, StorageTiers.OneMinute, isMachine: true);

        // The rule is "the last reading in the bucket", not "the last reading that
        // happened to carry a value", so a NULL final reading stays NULL.
        Assert.Equal(DBNull.Value, Scalar($"SELECT memory_total_mb FROM machine_1m WHERE ts = {bucket}"));
    }

    [Fact]
    public void ReRollup_GivesEachBucketTheLastTotalMemoryReadingFromThatBucket()
    {
        long first = BucketStart;
        long second = BucketStart + TenMinutesMs;

        for (int i = 0; i < 10; i++)
        {
            WriteMachineRollup(first + (i * MinuteMs), memoryTotalMb: 16_000 + i);
            WriteMachineRollup(second + (i * MinuteMs), memoryTotalMb: 32_000 + i);
        }

        Db.RollupSamples(second + TenMinutesMs, StorageTiers.OneMinute, StorageTiers.TenMinute, isMachine: true);

        Assert.Equal(16_009d, Scalar($"SELECT memory_total_mb FROM machine_10m WHERE ts = {first}"));
        Assert.Equal(32_009d, Scalar($"SELECT memory_total_mb FROM machine_10m WHERE ts = {second}"));
    }

    // ---- weighted averages: unmeasured minutes are excluded (issue #42) ----

    [Fact]
    public void ReRollup_MachineAverage_LeavesAnUnmeasuredMinuteOutOfTheDivisor()
    {
        long bucket = BucketStart;

        WriteMachineRollup(bucket, cpuPctAvg: 80, sampleCount: 12);
        WriteMachineRollup(bucket + MinuteMs, cpuPctAvg: 20, sampleCount: 12);

        // A minute in which CPU could never be computed. The collector stores such a
        // sample deliberately, so this row carries a real sample_count and no average.
        WriteMachineRollup(bucket + (2 * MinuteMs), cpuPctAvg: null, sampleCount: 12);

        Db.RollupSamples(bucket + TenMinutesMs, StorageTiers.OneMinute, StorageTiers.TenMinute, isMachine: true);

        // The average of what was actually measured. Counting the unmeasured
        // minute's twelve samples in the divisor alone would give 33.3.
        Assert.Equal(50d, (double)Scalar($"SELECT cpu_pct_avg FROM machine_10m WHERE ts = {bucket}")!, 6);

        // The sample count is the number of raw samples behind the bucket and is
        // unaffected: the samples were taken, they just could not be measured.
        Assert.Equal(36L, Scalar($"SELECT sample_count FROM machine_10m WHERE ts = {bucket}"));
    }

    [Fact]
    public void ReRollup_MachineBucketWithNothingMeasured_StaysNullRatherThanZero()
    {
        long bucket = BucketStart;

        WriteMachineRollup(bucket, cpuPctAvg: null, sampleCount: 12);
        WriteMachineRollup(bucket + MinuteMs, cpuPctAvg: null, sampleCount: 12);

        Db.RollupSamples(bucket + TenMinutesMs, StorageTiers.OneMinute, StorageTiers.TenMinute, isMachine: true);

        // Dividing by a zero weight would report a busy machine as idle. NULL says
        // "not measured", which is what happened.
        Assert.Equal(DBNull.Value, Scalar($"SELECT cpu_pct_avg FROM machine_10m WHERE ts = {bucket}"));
        Assert.Equal(24L, Scalar($"SELECT sample_count FROM machine_10m WHERE ts = {bucket}"));
    }

    [Fact]
    public void ReRollup_MachineAverage_AppliesToEveryWeightedColumnNotJustCpu()
    {
        long bucket = BucketStart;

        WriteMachineRollup(bucket, cpuPctAvg: 80, netKbpsAvg: 900, sampleCount: 12);
        WriteMachineRollup(bucket + MinuteMs, cpuPctAvg: 20, netKbpsAvg: 300, sampleCount: 12);
        WriteMachineRollup(bucket + (2 * MinuteMs), cpuPctAvg: null, netKbpsAvg: null, sampleCount: 12);

        Db.RollupSamples(bucket + TenMinutesMs, StorageTiers.OneMinute, StorageTiers.TenMinute, isMachine: true);

        Assert.Equal(600d, (double)Scalar($"SELECT net_kbps_avg FROM machine_10m WHERE ts = {bucket}")!, 6);
    }

    [Fact]
    public void ReRollup_ProcessAverage_LeavesAnUnmeasuredMinuteOutOfTheDivisor()
    {
        long bucket = BucketStart;
        long instanceId = Db.GetOrCreateProcessInstance(4321, 100, "test.exe", null, null, bucket);

        WriteSampleRollup(bucket, instanceId, cpuPctAvg: 40, sampleCount: 12);
        WriteSampleRollup(bucket + MinuteMs, instanceId, cpuPctAvg: 10, sampleCount: 12);
        WriteSampleRollup(bucket + (2 * MinuteMs), instanceId, cpuPctAvg: null, sampleCount: 12);

        Db.RollupSamples(bucket + TenMinutesMs, StorageTiers.OneMinute, StorageTiers.TenMinute, isMachine: false);

        // A short-lived process routinely produces a minute with nothing measurable,
        // so on the process side this is the common case rather than the rare one.
        Assert.Equal(25d, (double)Scalar($"SELECT cpu_pct_avg FROM sample_10m WHERE ts = {bucket}")!, 6);
        Assert.Equal(36L, Scalar($"SELECT sample_count FROM sample_10m WHERE ts = {bucket}"));
    }

    // ---- helpers ----

    private void WriteMachine(long ts, double? memoryTotalMb) =>
        Db.WriteMachineSample(ts,
            new MachineSample(50.0, 8000, 12000, 0, 1.0, 2.0, memoryTotalMb, 30.0, 1000, null));

    /// <summary>
    /// Seeds a one minute machine rollup row directly. The re-rollup reads this
    /// table rather than the raw one, and no collector API writes it.
    /// </summary>
    private void WriteMachineRollup(long ts, double? cpuPctAvg = 50, double? memoryTotalMb = 16_000,
        double? netKbpsAvg = 1000, long sampleCount = 1) => Execute($"""
        INSERT INTO machine_1m
            (ts, cpu_pct_avg, cpu_pct_max, memory_avail_mb_avg, memory_total_mb,
             commit_mb_max, hard_faults_total, disk_read_ms_avg, disk_write_ms_avg,
             disk_busy_pct_avg, disk_busy_pct_max, net_kbps_avg, gpu_busy_pct_avg, sample_count)
        VALUES ({ts}, {Sql(cpuPctAvg)}, 60.0, 8000, {Sql(memoryTotalMb)}, 12000, 0, 1.0, 2.0,
                30.0, 40.0, {Sql(netKbpsAvg)}, NULL, {sampleCount})
        """);

    /// <summary>Seeds a one minute process rollup row directly, for the same reason.</summary>
    private void WriteSampleRollup(long ts, long instanceId, double? cpuPctAvg, long sampleCount) => Execute($"""
        INSERT INTO sample_1m
            (ts, instance_id, cpu_pct_avg, cpu_pct_max, private_mb_max,
             working_set_mb_max, io_kb_total, sample_count)
        VALUES ({ts}, {instanceId}, {Sql(cpuPctAvg)}, 60.0, 100, 200, 50, {sampleCount})
        """);

    private static string Sql(double? value) =>
        value?.ToString(CultureInfo.InvariantCulture) ?? "NULL";

    // ---- cpu_pct_sustained_max: busy for a while, not busy for an instant ----

    [Fact]
    public void PromotingIntoTheHourlyTier_TakesTheSustainedMaxFromTheTenMinuteAverages()
    {
        // A quiet ten minutes that briefly touched 99, and a busy ten minutes that
        // sat at 80. The plain maximum reports the 99 and says the hour was flat out.
        // The sustained figure reports the 80, which is the one that describes the
        // hour rather than one reading inside it.
        long hour = Database.FloorToBucket(BucketStart, HourMs);

        WriteTenMinuteRollup(hour, cpuAvg: 20, cpuMax: 99);
        WriteTenMinuteRollup(hour + TenMinutesMs, cpuAvg: 80, cpuMax: 85);

        Db.RollupSamples(hour + HourMs, StorageTiers.TenMinute, StorageTiers.OneHour, isMachine: true);

        Assert.Equal(99, Real("SELECT cpu_pct_max FROM machine_1h"));
        Assert.Equal(80, Real("SELECT cpu_pct_sustained_max FROM machine_1h"));
    }

    [Fact]
    public void PromotingBetweenCoarseTiers_CarriesTheSustainedMaxThroughAsAMaximum()
    {
        // Above the hourly tier it is already the widest window it is allowed to
        // describe, so it composes as a plain maximum of maxima rather than being
        // recomputed from averages.
        long day = Database.FloorToBucket(BucketStart, DayMs);

        WriteHourlyRollup(day, sustainedMax: 30);
        WriteHourlyRollup(day + HourMs, sustainedMax: 75);
        WriteHourlyRollup(day + (2 * HourMs), sustainedMax: 40);

        Db.RollupSamples(day + DayMs, StorageTiers.OneHour, StorageTiers.OneDay, isMachine: true);

        Assert.Equal(75, Real("SELECT cpu_pct_sustained_max FROM machine_1d"));
    }

    [Fact]
    public void PromotingRowsRecordedBeforeTheSustainedMaxExisted_LeavesItNull()
    {
        // It cannot be worked back from an average and a maximum, so a bucket that
        // predates the column keeps nothing there rather than a figure invented from
        // two that cannot produce it.
        long day = Database.FloorToBucket(BucketStart, DayMs);
        WriteHourlyRollup(day, sustainedMax: null);

        Db.RollupSamples(day + DayMs, StorageTiers.OneHour, StorageTiers.OneDay, isMachine: true);

        Assert.Null(Scalar("SELECT cpu_pct_sustained_max FROM machine_1d") as double?);
    }

    private const long HourMs = 3_600_000L;
    private const long DayMs = 24 * HourMs;

    private void WriteTenMinuteRollup(long ts, double cpuAvg, double cpuMax) => Execute($"""
        INSERT INTO machine_10m
            (ts, cpu_pct_avg, cpu_pct_max, memory_avail_mb_avg, memory_total_mb,
             commit_mb_max, hard_faults_total, disk_read_ms_avg, disk_write_ms_avg,
             disk_busy_pct_avg, disk_busy_pct_max, net_kbps_avg, gpu_busy_pct_avg, sample_count)
        VALUES ({ts}, {Sql(cpuAvg)}, {Sql(cpuMax)}, 8000, 16000, 12000, 0, 1.0, 2.0,
                30.0, 40.0, 1000, NULL, 120)
        """);

    private void WriteHourlyRollup(long ts, double? sustainedMax) => Execute($"""
        INSERT INTO machine_1h
            (ts, cpu_pct_avg, cpu_pct_max, memory_avail_mb_avg, memory_total_mb,
             commit_mb_max, hard_faults_total, disk_read_ms_avg, disk_write_ms_avg,
             disk_busy_pct_avg, disk_busy_pct_max, net_kbps_avg, gpu_busy_pct_avg,
             sample_count, cpu_pct_sustained_max)
        VALUES ({ts}, 50.0, 60.0, 8000, 16000, 12000, 0, 1.0, 2.0,
                30.0, 40.0, 1000, NULL, 720, {Sql(sustainedMax)})
        """);
}
