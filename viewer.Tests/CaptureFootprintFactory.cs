namespace Viewer.Tests;

/// <summary>
/// A viewer host pointed at a capture file that has a write ahead log beside it,
/// both of a known length.
/// </summary>
/// <remarks>
/// Neither file needs to be a real database. The size the health endpoint reports
/// comes from a file probe rather than a query, so what is inside them does not
/// reach the figure under test, and a fixture that recorded real data could not
/// state the expected number without measuring the same files the code measures.
/// </remarks>
public class CaptureFootprintFactory : TelltaleTestFactory
{
    /// <summary>How long the capture file itself is.</summary>
    public const int DatabaseBytes = 2 * 1024 * 1024;

    /// <summary>How long the log beside it is. Different from the database, so a
    /// figure that counted one file twice would not pass by coincidence.</summary>
    public const int LogBytes = 5 * 1024 * 1024;

    public CaptureFootprintFactory() : base(CreateCaptureWithLog())
    {
    }

    static string CreateCaptureWithLog()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"telltale-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "telltale.db");

        File.WriteAllBytes(path, new byte[DatabaseBytes]);
        File.WriteAllBytes(path + "-wal", new byte[LogBytes]);

        return path;
    }

    // Cleanup handled by TelltaleTestFactory.Dispose.
}
