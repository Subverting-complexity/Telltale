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
public class DatabaseLifecycleTests
{
    [Fact]
    public void Dispose_CalledTwice_IsHarmless()
    {
        using var temp = new TempDatabase("lifecycle");

        temp.Db.Dispose();
        temp.Db.Dispose();
    }

    [Fact]
    public void UseAfterDispose_SaysSoRatherThanFailingOpaquely()
    {
        using var temp = new TempDatabase("lifecycle");
        temp.Db.Dispose();

        Assert.Throws<ObjectDisposedException>(() => temp.Db.GetDatabaseSizeBytes());
        Assert.Throws<ObjectDisposedException>(() =>
            temp.Db.GetOrCreateProcessInstance(1, 100, "a.exe", null, null, 1_000));
        Assert.Throws<ObjectDisposedException>(() =>
            temp.Db.WriteSampleBatch(1_000, [new SampleRow(1, 1.0, 10, 20, 1, 1, 1)]));
        Assert.Throws<ObjectDisposedException>(() => temp.Db.IncrementalVacuum());
        Assert.Throws<ObjectDisposedException>(() =>
            temp.Db.RollupSamples(1_000, "sample", "sample_1m", 1, isMachine: false));
    }
}
