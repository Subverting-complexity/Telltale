using Microsoft.Data.Sqlite;
using Telltale.Viewer;

namespace Viewer.Tests;

/// <summary>
/// A database with no machine table at all, which is what an install that has
/// never recorded anything looks like.
///
/// The endpoint used to answer this by asking sqlite_master itself before
/// calling <see cref="TimelineQuery"/>, which asks the same question again to
/// read tier coverage. The check moved into the query so a timeline request
/// makes one such lookup rather than two, and these pin that the answer did not
/// move with it.
/// </summary>
public class TimelineMissingTableTests : IDisposable
{
    readonly SqliteConnection _conn;

    public TimelineMissingTableTests()
    {
        _conn = new SqliteConnection("Data Source=:memory:");
        _conn.Open();
    }

    public void Dispose() => _conn.Dispose();

    void Execute(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public void NoMachineTable_AnswersEmptyRatherThanThrowing()
    {
        // Querying a table that is not there is an error, not an empty result, so
        // without the check this throws rather than answering.
        var result = TimelineQuery.Execute(_conn, 0, 86_400_000);

        Assert.Empty(result.Points);
        Assert.Equal("machine", result.Resolution);
        Assert.Equal(0, result.BucketMs);
        Assert.Null(result.BucketRequestMs);
        Assert.Equal(0, result.MinBucketMs);
        Assert.Equal(0, result.TierFloorMs);
    }

    [Fact]
    public void NoMachineTable_StillEchoesTheWidthThatWasAskedFor()
    {
        // The frontend compares what it asked for against what came back to decide
        // whether to explain a widening, so the echo has to survive this path too.
        var result = TimelineQuery.Execute(_conn, 0, 86_400_000, 600_000);

        Assert.Equal(600_000, result.BucketRequestMs);
        Assert.Equal(0, result.BucketMs);
        Assert.Empty(result.Points);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-5L)]
    public void NoMachineTable_NormalisesANonRequestTheSameWayAPlanDoes(long requested)
    {
        // A width of zero or less is no request rather than a narrower one, and
        // TierPlan normalises it to null. This path has to agree, or a caller
        // could tell the two apart by what the response echoed back.
        var result = TimelineQuery.Execute(_conn, 0, 86_400_000, requested);

        Assert.Null(result.BucketRequestMs);
    }

    [Fact]
    public void MachineTablePresentButEmpty_IsNotTreatedAsMissing()
    {
        // The distinction the coverage set exists for. Neither case has coverage,
        // but one can be queried and the other cannot, and only the second is the
        // one being short-circuited.
        Execute("""
            CREATE TABLE machine (
                ts INTEGER PRIMARY KEY, cpu_pct REAL, memory_avail_mb REAL, commit_mb REAL,
                hard_faults INTEGER, disk_read_ms REAL, disk_write_ms REAL, memory_total_mb REAL,
                disk_busy_pct REAL, net_kbps REAL, gpu_busy_pct REAL)
            """);

        var result = TimelineQuery.Execute(_conn, 0, 86_400_000);

        Assert.Empty(result.Points);
        Assert.Equal("machine", result.Resolution);
    }

    [Fact]
    public void CoverageSet_ReportsAPresentButEmptyTableAsPresent()
    {
        Execute("CREATE TABLE machine (ts INTEGER PRIMARY KEY, cpu_pct REAL)");

        TierCoverageSet tiers = TierCoverageReader.ReadSet(_conn, isMachine: true);

        Assert.True(tiers.Has("machine"));
        Assert.False(tiers.Has("machine_1m"));
        // Present, but with nothing in it, so it contributes no coverage.
        Assert.DoesNotContain("machine", tiers.Coverage.Keys);
    }

    [Fact]
    public void CoverageSet_AgreesWithTheDictionaryItWraps()
    {
        Execute("CREATE TABLE machine (ts INTEGER PRIMARY KEY, cpu_pct REAL)");
        Execute("INSERT INTO machine (ts, cpu_pct) VALUES (100, 1.0), (500, 2.0)");

        TierCoverageSet tiers = TierCoverageReader.ReadSet(_conn, isMachine: true);

        Assert.Equal(TierCoverageReader.Read(_conn, isMachine: true), tiers.Coverage);
        Assert.Equal(new TierCoverage(100, 500), tiers.Coverage["machine"]);
    }
}
