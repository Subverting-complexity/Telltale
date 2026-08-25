using Microsoft.Data.Sqlite;

namespace Viewer.Tests;

/// <summary>
/// Guards the native SQLite build that reaches the viewer through
/// SQLitePCLRaw.lib.e_sqlite3. CVE-2025-6965 is a memory-corruption defect in
/// SQLite before 3.50.2, so the version that matters is the one the engine
/// reports at runtime, not the version declared in a project file.
///
/// The collector has its own copy of this test. The two are deliberately not
/// shared: each executable resolves its own dependency chain, and
/// <c>collector/</c> and <c>viewer/</c> must not reference each other.
/// </summary>
public class SqliteVersionTests
{
    /// <summary>First SQLite release carrying the CVE-2025-6965 fix.</summary>
    private static readonly Version MinimumSqliteVersion = new(3, 50, 2);

    [Fact]
    public void NativeSqliteIsAtOrAboveTheCve20256965FixVersion()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT sqlite_version();";
        var reported = (string)command.ExecuteScalar()!;

        Assert.True(
            Version.TryParse(reported, out var actual),
            $"Could not parse the SQLite version string '{reported}'.");

        Assert.True(
            actual >= MinimumSqliteVersion,
            $"SQLite {reported} is older than {MinimumSqliteVersion}, which is vulnerable to " +
            "CVE-2025-6965. Raise the Microsoft.Data.Sqlite version in Directory.Packages.props " +
            "so SQLitePCLRaw.lib.e_sqlite3 resolves to a patched build.");
    }
}
