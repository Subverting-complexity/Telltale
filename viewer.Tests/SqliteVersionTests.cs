using Microsoft.Data.Sqlite;

namespace Viewer.Tests;

/// <summary>
/// Guards the native SQLite build that ships inside SQLitePCLRaw.lib.e_sqlite3.
/// CVE-2025-6965 is a memory-corruption defect in SQLite before 3.50.2, and the
/// package version is pinned in several project files, so a partial downgrade
/// could quietly put a vulnerable build back on the shipping binary. This test
/// checks the library actually loaded at runtime rather than the declared
/// package version, which is the thing that matters.
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
            "CVE-2025-6965. Raise the Microsoft.Data.Sqlite version in every project file so " +
            "SQLitePCLRaw.lib.e_sqlite3 resolves to a patched build.");
    }
}
