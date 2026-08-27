using System.Net;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Viewer.Tests;

/// <summary>
/// Seeds only the one minute rollup tier, so the newest reading in the window is
/// a bucket rather than a sample, and the two instances of the group carry
/// different sample counts inside it.
///
/// That combination is where a latest-reading query is easiest to get wrong. The
/// weight a rollup row carries is how many samples went into it, so an instance
/// that started part way through the bucket weighs less than one that was there
/// the whole time, and a query that simply added the two averages together would
/// report a group busier than it was.
/// </summary>
public class RollupLatestTestFactory : TelltaleTestFactory
{
    public const string GroupName = "group.exe";

    public const long BucketMs = 60_000;

    /// <summary>The whole-bucket instance: present throughout, twelve samples.</summary>
    public const double FullCpuPct = 20.0;
    public const int FullSamples = 12;
    public const double FullPrivateMb = 300.0;
    public const double FullIoKb = 40.0;

    /// <summary>The late arrival: busier, but there for a quarter of the bucket.</summary>
    public const double PartialCpuPct = 50.0;
    public const int PartialSamples = 3;
    public const double PartialPrivateMb = 100.0;
    public const double PartialIoKb = 10.0;

    /// <summary>What the group used through the earlier bucket, which the range average includes.</summary>
    public const double EarlierCpuPct = 1.0;

    static readonly long _newest = 1_700_000_000_000 - 1_700_000_000_000 % BucketMs;

    /// <summary>The newest bucket, which a latest-reading query has to land on.</summary>
    public static long NewestTs => _newest;

    public static long EarliestTs => _newest - 4 * BucketMs;

    /// <summary>
    /// The group's CPU at the newest bucket: each instance's average scaled by the
    /// share of the bucket it was actually there for.
    /// </summary>
    public static double ExpectedLatestCpu =>
        (FullCpuPct * FullSamples + PartialCpuPct * PartialSamples) / FullSamples;

    public RollupLatestTestFactory() : base(CreateDb())
    {
    }

    private static string CreateDb()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"telltale-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "telltale.db");

        using var conn = new SqliteConnection($"Data Source={path}");
        conn.Open();

        using (var schemaCmd = conn.CreateCommand())
        {
            schemaCmd.CommandText = File.ReadAllText(
                Path.Combine(AppContext.BaseDirectory, "schema.sql"));
            schemaCmd.ExecuteNonQuery();
        }

        SeedInstances(conn);
        SeedRollup(conn);

        // Deliberately nothing in `sample`. An empty tier reports no coverage, so
        // tier selection has to fall to sample_1m, which is the case under test.
        return path;
    }

    static void SeedInstances(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO process_instance (id, pid, create_time, name, path, first_seen, last_seen)
            VALUES (1, 2001, @start, @name, NULL, @start, @end),
                   (2, 2002, @start, @name, NULL, @start, @end)
            """;
        cmd.Parameters.AddWithValue("@start", EarliestTs);
        cmd.Parameters.AddWithValue("@end", NewestTs);
        cmd.Parameters.AddWithValue("@name", GroupName);
        cmd.ExecuteNonQuery();
    }

    static void SeedRollup(SqliteConnection conn)
    {
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;

        cmd.CommandText = """
            INSERT INTO sample_1m (ts, instance_id, cpu_pct_avg, cpu_pct_max,
                                   private_mb_max, working_set_mb_max, io_kb_total, sample_count)
            VALUES (@ts, @instance, @cpu, @cpu, @mem, @mem, @io, @count)
            """;

        var ts = cmd.Parameters.Add("@ts", SqliteType.Integer);
        var instance = cmd.Parameters.Add("@instance", SqliteType.Integer);
        var cpu = cmd.Parameters.Add("@cpu", SqliteType.Real);
        var mem = cmd.Parameters.Add("@mem", SqliteType.Real);
        var io = cmd.Parameters.Add("@io", SqliteType.Real);
        var count = cmd.Parameters.Add("@count", SqliteType.Integer);

        // Quiet buckets before the newest one, so the range average and the newest
        // reading cannot agree by accident.
        for (long at = EarliestTs; at < NewestTs; at += BucketMs)
        {
            ts.Value = at;
            instance.Value = 1;
            cpu.Value = EarlierCpuPct;
            mem.Value = FullPrivateMb;
            io.Value = FullIoKb;
            count.Value = FullSamples;
            cmd.ExecuteNonQuery();
        }

        ts.Value = NewestTs;
        instance.Value = 1;
        cpu.Value = FullCpuPct;
        mem.Value = FullPrivateMb;
        io.Value = FullIoKb;
        count.Value = FullSamples;
        cmd.ExecuteNonQuery();

        ts.Value = NewestTs;
        instance.Value = 2;
        cpu.Value = PartialCpuPct;
        mem.Value = PartialPrivateMb;
        io.Value = PartialIoKb;
        count.Value = PartialSamples;
        cmd.ExecuteNonQuery();

        tx.Commit();
    }

    // Cleanup handled by TelltaleTestFactory.Dispose.
}

/// <summary>
/// The claim the latest form rests on is that it changes only the time predicate:
/// the aggregate expressions are the range form's own, and applied to a single
/// timestamp they reduce to that timestamp's totals. These tests hold it to that
/// on the awkward case, a rollup bucket whose instances carry different weights.
/// </summary>
public class RollupLatestReadingTests : IClassFixture<RollupLatestTestFactory>
{
    private readonly HttpClient _client;

    public RollupLatestReadingTests(RollupLatestTestFactory factory)
    {
        _client = factory.CreateClient();
    }

    static string WholeWindow =>
        $"from={RollupLatestTestFactory.EarliestTs}&to={RollupLatestTestFactory.NewestTs}";

    /// <summary>The same window narrowed to the newest bucket alone.</summary>
    static string NewestBucketOnly =>
        $"from={RollupLatestTestFactory.NewestTs}&to={RollupLatestTestFactory.NewestTs}";

    [Fact]
    public async Task Latest_LandsOnTheNewestBucketWhenNoRawTierCoversTheWindow()
    {
        var root = await Get($"/api/processes?{WholeWindow}&latest=true");

        Assert.Equal(RollupLatestTestFactory.NewestTs, root.GetProperty("latestTs").GetInt64());
    }

    [Fact]
    public async Task Latest_WeightsEachInstanceByTheShareOfTheBucketItWasPresentFor()
    {
        var root = await Get($"/api/processes?{WholeWindow}&latest=true");
        double cpu = Group(root).GetProperty("cpuPct").GetDouble();

        // 32.5, not 70. Adding the two averages together would claim the group
        // held most of a core for the whole minute when one instance was only
        // there for the last quarter of it.
        Assert.Equal(RollupLatestTestFactory.ExpectedLatestCpu, cpu, 6);
        Assert.NotEqual(
            RollupLatestTestFactory.FullCpuPct + RollupLatestTestFactory.PartialCpuPct,
            cpu, 6);
    }

    [Fact]
    public async Task Latest_TotalsMemoryAndIoAcrossTheInstancesInThatBucket()
    {
        var group = Group(await Get($"/api/processes?{WholeWindow}&latest=true"));

        Assert.Equal(
            RollupLatestTestFactory.FullPrivateMb + RollupLatestTestFactory.PartialPrivateMb,
            group.GetProperty("privateMb").GetDouble(), 6);
        Assert.Equal(
            RollupLatestTestFactory.FullIoKb + RollupLatestTestFactory.PartialIoKb,
            group.GetProperty("ioKb").GetDouble(), 6);
        Assert.Equal(2, group.GetProperty("instanceCount").GetInt32());
    }

    [Fact]
    public async Task Latest_AgreesWithTheRangeFormAskedForThatOneBucket()
    {
        // The invariant behind the whole design. If these two ever disagree, the
        // latest form has stopped being the range form with a narrower predicate
        // and has become a second, separately maintained way of adding a group up.
        var latest = Group(await Get($"/api/processes?{WholeWindow}&latest=true"));
        var narrowed = Group(await Get($"/api/processes?{NewestBucketOnly}"));

        Assert.Equal(narrowed.GetProperty("cpuPct").GetDouble(), latest.GetProperty("cpuPct").GetDouble(), 6);
        Assert.Equal(narrowed.GetProperty("privateMb").GetDouble(), latest.GetProperty("privateMb").GetDouble(), 6);
        Assert.Equal(narrowed.GetProperty("ioKb").GetDouble(), latest.GetProperty("ioKb").GetDouble(), 6);
    }

    [Fact]
    public async Task Range_OverTheWholeWindowIsMuchQuieterThanTheNewestBucket()
    {
        // Guards the test above from passing because the window happens to be one
        // bucket wide: the two questions must have visibly different answers here.
        double range = Group(await Get($"/api/processes?{WholeWindow}")).GetProperty("cpuPct").GetDouble();

        Assert.True(range < RollupLatestTestFactory.ExpectedLatestCpu / 2,
            $"Expected the range average well below the newest bucket, got {range}");
    }

    static JsonElement Group(JsonElement root) =>
        root.GetProperty("processes").EnumerateArray()
            .First(p => p.GetProperty("name").GetString() == RollupLatestTestFactory.GroupName);

    async Task<JsonElement> Get(string url)
    {
        var response = await _client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(json).RootElement.Clone();
    }
}
