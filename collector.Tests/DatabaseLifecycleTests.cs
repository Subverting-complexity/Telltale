using Telltale.Collector;

namespace Collector.Tests;

/// <summary>
/// Covers what <see cref="Database"/> does once it has been disposed.
///
/// The connection is shared by two hosted services, and the host stops those
/// before it disposes the provider, so a call arriving after disposal should not
/// happen. If it does, the useful outcome is a name and a reason rather than an
/// opaque failure from a closed connection, and disposing twice has to stay
/// harmless because that is what a using block around a field can produce.
/// </summary>
public class DatabaseLifecycleTests() : SqliteTestBase("lifecycle")
{
    [Fact]
    public void Dispose_CalledTwice_IsHarmless()
    {
        Db.Dispose();
        Db.Dispose();

        // The fixture disposes it a third time on teardown, so this test would fail
        // there too if disposal ever stopped being repeatable. A tripwire for a
        // future Dispose rather than cover for the current guard: SqliteConnection
        // is itself idempotent, so removing the guard alone would not fail this.
    }

    [Fact]
    public void Dispose_ReleasesTheDatabaseFileAndItsSidecars()
    {
        // The collector opens its connection with pooling off so that disposing it
        // closes the file rather than parking the handle in a pool. Everything that
        // used to reach for SqliteConnection.ClearAllPools to get the file back now
        // relies on this, including the startup refusal path, which must not leave
        // -wal and -shm beside a database it has declined to touch (#91).
        Db.Dispose();

        Assert.False(File.Exists(DbPath + "-wal"), "the write ahead log should have been removed");
        Assert.False(File.Exists(DbPath + "-shm"), "the shared memory file should have been removed");

        // Deleting the database itself is the stronger claim: on Windows this
        // throws while any handle to the file is still open. It retries briefly,
        // because a scanner or indexer can hold a handle for a few milliseconds
        // after SQLite closes the file, and this suite is not the place to add
        // another intermittent failure.
        DeleteWithShortRetry(DbPath);
        Assert.False(File.Exists(DbPath));
    }

    private static void DeleteWithShortRetry(string path)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                File.Delete(path);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 20)
            {
                Thread.Sleep(25);
            }
        }
    }

    [Fact]
    public void UseAfterDispose_SaysSoRatherThanFailingOpaquely()
    {
        Db.Dispose();

        Assert.Throws<ObjectDisposedException>(() => Db.GetDatabaseSizeBytes());
        Assert.Throws<ObjectDisposedException>(() =>
            Db.GetOrCreateProcessInstance(1, 100, "a.exe", null, null, 1_000));
        Assert.Throws<ObjectDisposedException>(() =>
            Db.WriteSampleBatch(1_000, [new SampleRow(1, 1.0, 10, 20, 1, 1, 1)]));
        Assert.Throws<ObjectDisposedException>(() => Db.IncrementalVacuum());
        Assert.Throws<ObjectDisposedException>(() =>
            Db.RollupSamples(1_000, StorageTiers.Raw, StorageTiers.OneMinute, isMachine: false));

        // Every gated method, not a sample of them. The guard rides the same
        // convention as the lock, so a method added later without either is the
        // failure this suite is here to notice.
        Assert.Throws<ObjectDisposedException>(() =>
            Db.WriteMachineSample(1_000, new MachineSample(1, 1, 1, 0, 1, 1, 1, 1, 1, null)));
        Assert.Throws<ObjectDisposedException>(() =>
            Db.WriteCollectorHealth(1_000, 1, 1, 1, 1, 1));
        Assert.Throws<ObjectDisposedException>(() => Db.DeleteOldData("sample", 1_000));
        Assert.Throws<ObjectDisposedException>(() => Db.DeleteOrphanedProcessInstances());
        Assert.Throws<ObjectDisposedException>(() => Db.WalCheckpoint());
        Assert.Throws<ObjectDisposedException>(() => Db.EnforceSizeLimit(long.MaxValue));
    }
}
