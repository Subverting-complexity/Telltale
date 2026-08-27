using Microsoft.Data.Sqlite;

namespace Viewer.Tests;

/// <summary>
/// Opens the direct connections these tests use to seed and read back a temporary
/// capture database, separately from the viewer under test.
/// </summary>
/// <remarks>
/// Pooling is off, for the same reason the collector turns it off on its own
/// connection and the viewer now turns it off on its read connections (#177).
/// Microsoft.Data.Sqlite keeps a pooled handle open after <c>Dispose</c> returns,
/// so a factory that seeded a database through a pooled connection still held its
/// file afterwards and could not delete its own temporary directory on Windows.
///
/// Releasing a pooled handle means clearing its pool, and the call reached for was
/// <c>SqliteConnection.ClearAllPools</c>, which is process wide. xUnit runs test
/// classes in parallel, so clearing the pools on one thread can hand an already
/// disposed handle to a connection being opened on another, and the next statement
/// on it fails with an <see cref="ObjectDisposedException"/> naming
/// <c>SQLitePCL.sqlite3</c>. That is what failed about one collector migration test
/// run in twenty-five (#91), and #116 is the same exposure here.
///
/// <c>SqliteConnection.ClearPool(connection)</c> would have narrowed it to one
/// pool. It is not used because the pool key is the connection string, so a test
/// would have to rebuild the string the code under test builds and would stop
/// matching in silence if that ever changed. Not pooling has no key to get wrong.
/// </remarks>
internal static class TestConnection
{
    /// <summary>An open connection to <paramref name="dbPath"/>, owned by the caller.</summary>
    internal static SqliteConnection Open(string dbPath, SqliteOpenMode mode = SqliteOpenMode.ReadWriteCreate)
    {
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = mode,
            Pooling = false,
        }.ToString());
        conn.Open();

        return conn;
    }
}
