using Microsoft.Data.Sqlite;

namespace Viewer.Tests;

public class SeededTelltaleTestFactory : TelltaleTestFactory
{
    public const long MinTs = 1_700_000_000_000;
    public const long MaxTs = 1_700_000_060_000;

    public SeededTelltaleTestFactory() : base(CreateSeededDb())
    {
    }

    private static string CreateSeededDb()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"telltale-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "telltale.db");

        using var conn = TestConnection.Open(path);

        using (var schemaCmd = conn.CreateCommand())
        {
            schemaCmd.CommandText = File.ReadAllText(
                Path.Combine(AppContext.BaseDirectory, "schema.sql"));
            schemaCmd.ExecuteNonQuery();
        }

        using (var insertCmd = conn.CreateCommand())
        {
            insertCmd.CommandText = """
                INSERT INTO machine (ts, cpu_pct) VALUES (@minTs, 5.0), (@maxTs, 8.0)
                """;
            insertCmd.Parameters.AddWithValue("@minTs", MinTs);
            insertCmd.Parameters.AddWithValue("@maxTs", MaxTs);
            insertCmd.ExecuteNonQuery();
        }

        return path;
    }

    // Cleanup handled by TelltaleTestFactory.Dispose.
}
