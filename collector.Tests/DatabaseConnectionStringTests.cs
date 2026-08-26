using Telltale.Collector;

namespace Collector.Tests;

/// <summary>
/// Covers paths whose characters mean something to a SQLite connection string.
///
/// The database path comes from the user through <c>telltale.json</c>, so it is
/// text the collector does not control. Pasting it into a connection string let
/// a perfectly ordinary folder name change what the rest of the string meant.
/// </summary>
public class DatabaseConnectionStringTests
{
    [Fact]
    public void PathContainingASemicolon_OpensNormally()
    {
        // A semicolon is legal in a Windows filename and is also the separator
        // between keywords in a connection string. Interpolating the path put
        // those two facts in conflict and produced an ArgumentException, which
        // is not one of the types the collector startup path catches, so the
        // user got an unhandled exception and no explanation at all.
        string dir = Path.Combine(Path.GetTempPath(), $"telltale;semi_{Guid.NewGuid():N}");
        string path = Path.Combine(dir, "telltale.db");

        try
        {
            using (var db = new Database(path, new RecordingLogger()))
                Assert.Equal(SchemaMigrations.LatestVersion, db.SchemaVersion);

            Assert.True(File.Exists(path), "the database should have been created at the semicolon path");
        }
        finally
        {
            // Nothing to release first: Database does not pool, so disposing it
            // above closed the file and removed its WAL sidecars.
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort cleanup */ }
        }
    }

    [Fact]
    public void PathContainingAnEqualsSign_OpensNormally()
    {
        // Harmless on its own, but it is the other character that carries meaning
        // in a connection string, so it is worth pinning alongside the semicolon
        // rather than left to be discovered later.
        string dir = Path.Combine(Path.GetTempPath(), $"telltale=eq_{Guid.NewGuid():N}");
        string path = Path.Combine(dir, "telltale.db");

        try
        {
            using (var db = new Database(path, new RecordingLogger()))
                Assert.Equal(SchemaMigrations.LatestVersion, db.SchemaVersion);

            Assert.True(File.Exists(path), "the database should have been created at the equals path");
        }
        finally
        {
            // Nothing to release first: Database does not pool, so disposing it
            // above closed the file and removed its WAL sidecars.
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort cleanup */ }
        }
    }
}
