using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;

namespace Viewer.Tests;

/// <summary>
/// A viewer host configured with an empty capture database path.
///
/// This is a misconfiguration rather than a broken database, and it is reachable:
/// the viewer reads TELLTALE_DB and falls back to a default only when the value is
/// null, so an environment variable set to an empty string passes straight through.
/// The health endpoint probes the file size with FileInfo, which rejects an empty
/// path, and that is a different failure from the SqliteException the rest of the
/// handlers are written for.
/// </summary>
public class UnusablePathFactory : TelltaleTestFactory
{
    /// <summary>What the viewer logged while serving requests.</summary>
    public RecordingLoggerProvider Logs { get; } = new();

    public UnusablePathFactory() : base(string.Empty)
    {
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureLogging(logging => logging.AddProvider(Logs));
    }
}
