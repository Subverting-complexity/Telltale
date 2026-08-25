using Microsoft.Data.Sqlite;
using Telltale.Collector;

namespace Collector.Tests;

/// <summary>
/// Covers what <see cref="Database"/> throws when it cannot open the file it was
/// given.
///
/// The collector catches three exception types at startup so that an unopenable
/// database produces an explanation rather than an unhandled exception. That
/// catch is only worth anything while those are the types actually thrown, and
/// nothing else in the suite would notice if that stopped being true.
/// </summary>
public class DatabaseOpenFailureTests
{
    [Fact]
    public void PathThatCannotBeOpened_ThrowsSomethingTheStartupPathCatches()
    {
        // A directory standing where the database file should be. It is the one
        // unopenable path that behaves the same on Windows and on Linux; a
        // locked file, the case that prompted this, is not reproducible on both.
        string path = Path.Combine(Path.GetTempPath(), $"telltale_open_{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);

        try
        {
            var error = Assert.ThrowsAny<Exception>(
                () => new Database(path, new RecordingLogger()));

            Assert.True(
                error is SqliteException or IOException or UnauthorizedAccessException,
                $"Program.cs catches only those three types, and this was "
                + $"{error.GetType().Name}: {error.Message}");
        }
        finally
        {
            try { Directory.Delete(path, recursive: true); } catch { /* best effort cleanup */ }
        }
    }

    [Fact]
    public void FileThatIsNotADatabase_ThrowsSomethingTheStartupPathCatches()
    {
        // A different code path from the test above, and worth having for that
        // reason. The file opens without complaint and the failure comes later,
        // out of the first pragma in InitSchema, so this covers the corrupt
        // database case rather than the unopenable one.
        string path = Path.Combine(Path.GetTempPath(), $"telltale_corrupt_{Guid.NewGuid():N}.db");
        File.WriteAllText(path, "this is not a database");

        try
        {
            var error = Assert.ThrowsAny<Exception>(
                () => new Database(path, new RecordingLogger()));

            Assert.True(
                error is SqliteException or IOException or UnauthorizedAccessException,
                $"Program.cs catches only those three types, and this was "
                + $"{error.GetType().Name}: {error.Message}");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (string suffix in new[] { "", "-wal", "-shm" })
            {
                try { File.Delete(path + suffix); } catch { /* best effort cleanup */ }
            }
        }
    }
}
