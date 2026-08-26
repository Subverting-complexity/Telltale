using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
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

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing) return;

        SqliteConnection.ClearAllPools();

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
