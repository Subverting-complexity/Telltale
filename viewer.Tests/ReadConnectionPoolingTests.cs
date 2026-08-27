namespace Viewer.Tests;

/// <summary>
/// The viewer's read connections are not pooled, so a finished request leaves no
/// handle on the capture file.
/// </summary>
/// <remarks>
/// Microsoft.Data.Sqlite keeps a pooled handle open after <c>Dispose</c> returns.
/// SQLite folds the write ahead log back into the database and removes it when the
/// last connection to a file closes, and in the single application build the
/// recorder and this listener are one process, so a pooled read handle meant the
/// recorder was never the last connection and its close tidied nothing away
/// (#177). It is also what made <c>viewer.Tests</c> reach for the process wide
/// <c>SqliteConnection.ClearAllPools</c> to delete its own temporary files (#116).
///
/// A held handle is not directly observable, so this asserts the consequence that
/// matters on Windows: an open file cannot be deleted. That is the same consequence
/// the test factories were working around, which is why removing the workaround and
/// pinning this belong together.
/// </remarks>
public class ReadConnectionPoolingTests
{
    [Fact]
    public async Task AFinishedRequest_LeavesNoHandleOnTheCaptureFile()
    {
        // Its own factory rather than a class fixture, because the assertion deletes
        // the capture file and every other test wants one that is still there.
        using var factory = new SeededTelltaleTestFactory();
        using (var client = factory.CreateClient())
        {
            var response = await client.GetAsync("/api/range");
            response.EnsureSuccessStatusCode();
        }

        // Pooled, this throws IOException naming the file as in use by another
        // process. It is the whole of what turning pooling off buys.
        File.Delete(factory.DbPath);

        Assert.False(File.Exists(factory.DbPath));
    }

    [Fact]
    public async Task SeveralFinishedRequests_LeaveNoHandleEither()
    {
        // A window asks for several charts at once, so the pool would have more than
        // one handle in it to hand back. Reusing them was the thing being given up.
        using var factory = new SeededTelltaleTestFactory();
        using (var client = factory.CreateClient())
        {
            foreach (var url in new[] { "/api/range", "/api/health", "/api/range" })
                (await client.GetAsync(url)).EnsureSuccessStatusCode();
        }

        File.Delete(factory.DbPath);

        Assert.False(File.Exists(factory.DbPath));
    }

    [Fact]
    public async Task ADisposedFactory_ActuallyRemovesItsTemporaryDirectory()
    {
        // The factory's own cleanup, which is what used to need ClearAllPools (#116).
        // It swallows a failed delete, so on its own it can leave a directory behind
        // in silence and nothing notices. This is the assertion the swallow hides.
        var factory = new SeededTelltaleTestFactory();
        var dir = Path.GetDirectoryName(factory.DbPath)!;

        using (var client = factory.CreateClient())
            (await client.GetAsync("/api/range")).EnsureSuccessStatusCode();

        factory.Dispose();

        Assert.False(Directory.Exists(dir),
            "The factory should be able to delete its own temporary directory without "
            + "clearing every SQLite pool in the process.");
    }
}
