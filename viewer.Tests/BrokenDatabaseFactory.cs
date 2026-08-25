using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Viewer.Tests;

/// <summary>
/// A viewer host pointed at a file that exists but is not a SQLite database.
///
/// This is what makes the endpoints fail on demand. The endpoints open the capture
/// database read-only when the file is present, so the first query against this one
/// raises SqliteException. A database that is merely absent will not do: the viewer
/// creates an empty one and every endpoint then succeeds with no rows, which is a
/// success path rather than the failure the handlers are written for.
/// </summary>
public class BrokenDatabaseFactory : TelltaleTestFactory
{
    /// <summary>
    /// How much filler the unreadable file holds. Large enough that /api/health
    /// reports a non-zero size in megabytes, which is what lets a test tell a
    /// working file probe from one that failed and left the size at its default.
    /// SQLite rejects the file on its header regardless of length, so the failure
    /// this fixture exists to cause is unaffected.
    /// </summary>
    public const int FillerBytes = 3 * 1024 * 1024;

    /// <summary>
    /// What the viewer logged while serving requests.
    ///
    /// The viewer reports a given failure once and collapses identical repeats, so a
    /// test that expects a fresh warning must hit an endpoint no other test in the
    /// same class has already hit. Each test class gets its own instance of this
    /// fixture, so the collapsing is scoped to one class.
    /// </summary>
    public RecordingLoggerProvider Logs { get; } = new();

    public BrokenDatabaseFactory() : base(CreateUnreadableDb())
    {
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureLogging(logging => logging.AddProvider(Logs));
    }

    static string CreateUnreadableDb()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"telltale-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "telltale.db");

        var contents = new byte[FillerBytes];
        "This file is deliberately not a SQLite database."u8.CopyTo(contents);
        File.WriteAllBytes(path, contents);

        return path;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (!disposing) return;

        // The pool holds the connections the endpoints opened, which keeps the file
        // locked and the delete below failing on Windows.
        SqliteConnection.ClearAllPools();

        try
        {
            Directory.Delete(Path.GetDirectoryName(DbPath)!, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
