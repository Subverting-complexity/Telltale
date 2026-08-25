using Microsoft.AspNetCore.Hosting;
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
    /// <summary>What the viewer logged while serving requests.</summary>
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
        File.WriteAllText(path, "This file is deliberately not a SQLite database.");
        return path;
    }
}
