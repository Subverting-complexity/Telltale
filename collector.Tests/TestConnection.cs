using Microsoft.Data.Sqlite;

namespace Collector.Tests;

/// <summary>
/// Opens the direct connections tests use to read a temporary database back,
/// separately from the <see cref="Telltale.Collector.Database"/> under test.
/// </summary>
/// <remarks>
/// Pooling is off, for the same reason the collector turns it off. A pooled
/// handle is only released by <c>SqliteConnection.ClearAllPools</c>, which is
/// process wide, and xUnit runs test classes in parallel: clearing the pools on
/// one thread can hand an already disposed handle to a connection being opened
/// on another, and the next statement on it fails with an
/// <see cref="ObjectDisposedException"/> naming <c>SQLitePCL.sqlite3</c>. That is
/// what failed about one migration test run in twenty-five (#91).
///
/// Without a pool, closing a connection closes the file and removes its -wal and
/// -shm sidecars, so a test can delete what it created without clearing anything
/// process wide.
/// </remarks>
internal static class TestConnection
{
    internal static string StringFor(string dbPath) =>
        new SqliteConnectionStringBuilder { DataSource = dbPath, Pooling = false }.ToString();

    /// <summary>An open connection to <paramref name="dbPath"/>, owned by the caller.</summary>
    internal static SqliteConnection Open(string dbPath)
    {
        var conn = new SqliteConnection(StringFor(dbPath));
        conn.Open();

        return conn;
    }
}
