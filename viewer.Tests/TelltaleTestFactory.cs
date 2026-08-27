using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Viewer.Tests;

public class TelltaleTestFactory : WebApplicationFactory<Program>
{
    public string DbPath { get; }

    public TelltaleTestFactory() : this(CreateNonexistentDbPath())
    {
    }

    protected TelltaleTestFactory(string dbPath)
    {
        DbPath = dbPath;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TELLTALE_DB"] = DbPath,
            }));
    }

    /// <remarks>
    /// This used to call <c>SqliteConnection.ClearAllPools</c> before deleting the
    /// directory, because a pooled handle keeps the capture file open on Windows and
    /// an open file cannot be deleted. That call is process wide, and xUnit runs test
    /// classes in parallel, so clearing the pools here could hand an already disposed
    /// handle to a connection being opened in another class, whose next statement
    /// then failed with an <see cref="ObjectDisposedException"/> naming
    /// <c>SQLitePCL.sqlite3</c>. That is what made about one collector migration test
    /// run in twenty-five fail before #91, and #116 is the same exposure here: the
    /// viewer opened a pooled connection per request and every test class disposing a
    /// factory cleared every pool in the process.
    ///
    /// Nothing pools any more, so there is nothing to clear. The viewer's read
    /// connections turned pooling off in #177 and this project's own seeding
    /// connections go through <see cref="TestConnection"/>, which does the same, so
    /// the file is genuinely closed by the time this runs. <c>ReadConnectionPoolingTests</c>
    /// is what pins that, by deleting a capture file after a request rather than
    /// swallowing the failure the way the loop below does.
    ///
    /// The catches stay. They cover a file briefly held by something outside the test
    /// run, a virus scanner being the usual one, and a temporary directory left
    /// behind is not worth failing a passing test over.
    /// </remarks>
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing) return;

        var dir = Path.GetDirectoryName(DbPath);
        if (dir != null && Directory.Exists(dir))
        {
            try { Directory.Delete(dir, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static string CreateNonexistentDbPath() =>
        Path.Combine(Path.GetTempPath(), $"telltale-test-{Guid.NewGuid():N}", "telltale.db");
}
