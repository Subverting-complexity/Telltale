using Microsoft.Extensions.DependencyInjection;
using Telltale.Collector;

namespace Collector.Tests;

/// <summary>
/// Covers who owns the collector's database connection once it has been
/// registered, because the startup refusal path depends on that and nothing else
/// checks it.
/// </summary>
/// <remarks>
/// <see cref="CollectorStartup.OpenAndCheckDatabase"/> releases the capture files
/// on a refusal by disposing the host, and that works only because
/// <see cref="Database"/> is a singleton the provider owns and disposes, and
/// because it no longer pools its connection. Registered as transient, or built
/// outside the provider, the refusal path would go back to leaving -wal and -shm
/// beside a database this build has just declined to touch, and the old
/// SqliteConnection.ClearAllPools call that used to cover for that has gone (#91).
/// </remarks>
public class CollectorServicesTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"telltale_services_{Guid.NewGuid():N}.db");

    [Fact]
    public void DisposingTheProvider_ReleasesTheDatabaseFiles()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTelltaleCollector(new TelltaleConfig { DatabasePath = _dbPath });

        var provider = services.BuildServiceProvider();

        // Resolved the way OpenAndCheckDatabase resolves it, so the connection is
        // open and the sidecars exist before anything is disposed.
        Assert.Equal(SchemaMigrations.LatestVersion, provider.GetRequiredService<Database>().SchemaVersion);
        Assert.True(File.Exists(_dbPath), "the database should have been created");

        provider.Dispose();

        Assert.False(File.Exists(_dbPath + "-wal"), "the write ahead log should have been removed");
        Assert.False(File.Exists(_dbPath + "-shm"), "the shared memory file should have been removed");
    }

    public void Dispose()
    {
        foreach (string suffix in new[] { "", "-wal", "-shm" })
        {
            try { File.Delete(_dbPath + suffix); } catch { /* best effort cleanup */ }
        }

        GC.SuppressFinalize(this);
    }
}
