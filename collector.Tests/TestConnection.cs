using Microsoft.Data.Sqlite;

namespace Collector.Tests;

/// <summary>
/// Opens the direct connections tests use to read a temporary database back,
/// separately from the <see cref="Telltale.Collector.Database"/> under test.
/// </summary>
/// <remarks>
/// Pooling is off, for the same reason the collector turns it off. Releasing a
/// pooled handle means clearing its pool, and the call these tests reached for
/// was <c>SqliteConnection.ClearAllPools</c>, which is process wide. xUnit runs
/// test classes in parallel, so clearing the pools on one thread can hand an
/// already disposed handle to a connection being opened on another, and the next
/// statement on it fails with an <see cref="ObjectDisposedException"/> naming
/// <c>SQLitePCL.sqlite3</c>. That is what failed about one migration test run in
/// twenty-five (#91).
///
/// <c>SqliteConnection.ClearPool(connection)</c> would have narrowed that to one
/// pool rather than every pool in the process. It is not used here because the
/// pool key is the connection string, so a test would have to build the same
/// string the code under test builds and would stop matching in silence if that
/// ever changed. Not pooling has no key to get wrong.
///
/// Without a pool, closing a connection closes the file and removes its -wal and
/// -shm sidecars, so a test can delete what it created without clearing anything
/// process wide.
/// </remarks>
internal static class TestConnection
{
    /// <summary>An open connection to <paramref name="dbPath"/>, owned by the caller.</summary>
    internal static SqliteConnection Open(string dbPath)
    {
        var conn = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = dbPath, Pooling = false }.ToString());
        conn.Open();

        return conn;
    }
}
