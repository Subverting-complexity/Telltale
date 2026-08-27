using Microsoft.Data.Sqlite;
using Telltale.Viewer;

namespace Viewer.Tests;

/// <summary>
/// Covers how the window tells the size limit taking detail away from the
/// retention settings simply not keeping it. Left unexplained the two look the
/// same, and the first reads as a setting being ignored.
/// </summary>
public class TierPressureReaderTests : IDisposable
{
    readonly SqliteConnection _conn;

    public TierPressureReaderTests()
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

    void CreateTable() =>
        Execute("CREATE TABLE tier_pressure (tier TEXT PRIMARY KEY, retention_ms INTEGER NOT NULL)");

    [Fact]
    public void DatabaseWrittenBeforeTheTableExisted_ReadsAsNoPressure()
    {
        // The viewer reads databases older builds wrote. A missing table is a
        // capture that has never had detail taken off it, not a failure.
        Assert.False(TierPressureReader.Read(_conn));
    }

    [Fact]
    public void EmptyTable_ReadsAsNoPressure()
    {
        CreateTable();

        Assert.False(TierPressureReader.Read(_conn));
    }

    [Fact]
    public void AnyRow_ReadsAsPressureApplied()
    {
        // A row exists only because a tier gave something up, so its presence is the
        // whole signal. The viewer cannot compare against what was configured:
        // telltale.json belongs to the collector and the two do not reference
        // each other.
        CreateTable();
        Execute("INSERT INTO tier_pressure (tier, retention_ms) VALUES ('sample_1d', 15552000000)");

        Assert.True(TierPressureReader.Read(_conn));
    }
}
