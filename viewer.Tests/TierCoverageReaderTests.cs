using Microsoft.Data.Sqlite;
using Telltale.Viewer;

namespace Viewer.Tests;

public class TierCoverageReaderTests : IDisposable
{
    readonly SqliteConnection _conn;

    public TierCoverageReaderTests()
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
    public void DatabaseWithNoTierTables_ReturnsEmptyCoverage()
    {
        var coverage = TierCoverageReader.Read(_conn, isMachine: true);

        Assert.Empty(coverage);
    }

    [Fact]
    public void OlderDatabaseMissingRollupTables_ReadsWhatIsThere()
    {
        Execute("CREATE TABLE machine (ts INTEGER PRIMARY KEY, cpu_pct REAL)");
        Execute("INSERT INTO machine (ts, cpu_pct) VALUES (100, 1.0), (500, 2.0)");

        var coverage = TierCoverageReader.Read(_conn, isMachine: true);

        Assert.Equal(new TierCoverage(100, 500), Assert.Contains("machine", coverage));
        Assert.DoesNotContain("machine_1m", coverage.Keys);
    }

    [Fact]
    public void EmptyTable_IsOmittedRatherThanReportedAsZeroCoverage()
    {
        Execute("CREATE TABLE machine (ts INTEGER PRIMARY KEY, cpu_pct REAL)");
        Execute("CREATE TABLE machine_1m (ts INTEGER PRIMARY KEY, cpu_pct_avg REAL)");
        Execute("INSERT INTO machine (ts, cpu_pct) VALUES (100, 1.0)");

        var coverage = TierCoverageReader.Read(_conn, isMachine: true);

        Assert.Contains("machine", coverage.Keys);
        Assert.DoesNotContain("machine_1m", coverage.Keys);
    }

    [Fact]
    public void PopulatedTiers_AreAllReported()
    {
        Execute("CREATE TABLE sample (ts INTEGER, instance_id INTEGER)");
        Execute("CREATE TABLE sample_1m (ts INTEGER, instance_id INTEGER)");
        Execute("INSERT INTO sample (ts, instance_id) VALUES (900, 1), (1000, 1)");
        Execute("INSERT INTO sample_1m (ts, instance_id) VALUES (100, 1), (800, 1)");

        var coverage = TierCoverageReader.Read(_conn, isMachine: false);

        Assert.Equal(new TierCoverage(900, 1000), Assert.Contains("sample", coverage));
        Assert.Equal(new TierCoverage(100, 800), Assert.Contains("sample_1m", coverage));
    }

    [Fact]
    public void MachineAndProcessFamilies_AreReadIndependently()
    {
        Execute("CREATE TABLE machine (ts INTEGER PRIMARY KEY)");
        Execute("CREATE TABLE sample (ts INTEGER, instance_id INTEGER)");
        Execute("INSERT INTO machine (ts) VALUES (100)");
        Execute("INSERT INTO sample (ts, instance_id) VALUES (200, 1)");

        Assert.Contains("machine", TierCoverageReader.Read(_conn, isMachine: true).Keys);
        Assert.DoesNotContain("sample", TierCoverageReader.Read(_conn, isMachine: true).Keys);
        Assert.Contains("sample", TierCoverageReader.Read(_conn, isMachine: false).Keys);
    }

    [Fact]
    public void CoverageQuerySeeksTheTimestampIndexRatherThanScanning()
    {
        Execute("CREATE TABLE sample (ts INTEGER, instance_id INTEGER)");
        Execute("CREATE INDEX ix_sample_ts ON sample(ts)");
        Execute("INSERT INTO sample (ts, instance_id) VALUES (100, 1), (200, 1)");

        List<string> plan = QueryPlan(
            "SELECT 'sample' AS tier, (SELECT MIN(ts) FROM sample) AS min_ts, (SELECT MAX(ts) FROM sample) AS max_ts");

        // The form this replaced, MIN(ts), MAX(ts) in one SELECT, scans the
        // whole table on every request. SQLite spells a scan "SCAN sample" or
        // "SCAN TABLE sample" depending on version, so match on the verb.
        Assert.DoesNotContain(plan, IsTableScan);
        Assert.Contains(plan, step => step.Contains("SEARCH", StringComparison.Ordinal)
                                      && step.Contains("sample", StringComparison.Ordinal));
    }

    [Fact]
    public void CombinedMinMaxFormWouldScanTheTable()
    {
        Execute("CREATE TABLE sample (ts INTEGER, instance_id INTEGER)");
        Execute("CREATE INDEX ix_sample_ts ON sample(ts)");
        Execute("INSERT INTO sample (ts, instance_id) VALUES (100, 1), (200, 1)");

        List<string> plan = QueryPlan("SELECT 'sample' AS tier, MIN(ts), MAX(ts) FROM sample");

        // Guards the reason the reader is written the way it is: if a future
        // SQLite optimises this form too, the comment there can be relaxed.
        Assert.Contains(plan, IsTableScan);
    }

    static bool IsTableScan(string step) =>
        step.StartsWith("SCAN", StringComparison.Ordinal)
        && step.Contains("sample", StringComparison.Ordinal);

    List<string> QueryPlan(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "EXPLAIN QUERY PLAN " + sql;

        var plan = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) plan.Add(reader.GetString(3));
        return plan;
    }
}
