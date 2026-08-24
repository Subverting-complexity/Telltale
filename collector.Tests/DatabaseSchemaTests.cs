using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Telltale.Collector;

namespace Collector.Tests;

/// <summary>
/// Covers what a freshly created collector database looks like: the pragmas that
/// are only settable at creation time, the tables and indexes, and whether the
/// schema still agrees with <c>schema.sql</c>.
///
/// The auto_vacuum cases are regression cover for issue #37, where
/// <c>PRAGMA journal_mode = WAL</c> ran first, wrote the database header, and left
/// the auto_vacuum request with nothing to do. Nothing failed: the pragma reported
/// success, incremental vacuum quietly became a no-op, and the file never gave
/// pages back.
/// </summary>
public class DatabaseSchemaTests() : SqliteTestBase("schema")
{
    private const long AutoVacuumNone = 0;
    private const long AutoVacuumIncremental = 2;

    private static readonly string[] ExpectedTables =
    [
        "collector_health", "machine", "machine_10m", "machine_1m",
        "process_instance", "sample", "sample_10m", "sample_1m", "schema_version",
    ];

    private static readonly string[] ExpectedIndexes =
    [
        "ix_s10m_inst", "ix_s1m_inst", "ix_sample_inst", "ix_sample_ts",
        "ux_s10m_ts_inst", "ux_s1m_ts_inst",
    ];

    [Fact]
    public void FreshDatabase_TurnsOnIncrementalAutoVacuum()
    {
        // The whole point of the ordering fix. Reversed, this reports 0.
        Assert.Equal(AutoVacuumIncremental, Convert.ToInt64(Scalar("PRAGMA auto_vacuum")));
    }

    [Fact]
    public void FreshDatabase_IsInWalMode()
    {
        // Setting auto_vacuum first must not cost us WAL.
        Assert.Equal("wal", ((string)Scalar("PRAGMA journal_mode")!).ToLowerInvariant());
    }

    [Fact]
    public void FreshDatabase_CreatesEveryExpectedTable()
    {
        Assert.Equal(ExpectedTables, ObjectNames(DbPath, "table"));
    }

    [Fact]
    public void FreshDatabase_CreatesEveryExpectedIndex()
    {
        Assert.Equal(ExpectedIndexes, ObjectNames(DbPath, "index"));
    }

    [Fact]
    public void FreshDatabase_RecordsTheLatestSchemaVersion()
    {
        Assert.Equal(SchemaMigrations.LatestVersion,
            Convert.ToInt32(Scalar("SELECT version FROM schema_version")));
        Assert.Equal(SchemaMigrations.LatestVersion, Db.SchemaVersion);
    }

    [Fact]
    public void ReopeningADatabase_KeepsTheSchemaAndTheDataAlreadyInIt()
    {
        Db.GetOrCreateProcessInstance(1234, 100, "kept.exe", null, null, 1_000);
        Db.Dispose();

        using var reopened = new Database(DbPath, new CapturingLogger());

        Assert.Equal(1, Count("process_instance"));
        Assert.Equal(ExpectedTables, ObjectNames(DbPath, "table"));
    }

    /// <summary>
    /// schema.sql is the only contract between the collector and the viewer, and
    /// Database carries its own copy of the same statements. Nothing forces the two
    /// to agree, so this compares them structurally: same tables, same columns with
    /// the same declared types and constraints, same indexes over the same columns.
    /// Formatting differences are ignored, since the two are laid out differently.
    /// </summary>
    [Fact]
    public void SchemaBuiltByDatabase_MatchesTheSchemaSqlContract()
    {
        string contractPath = Path.Combine(Path.GetTempPath(), $"telltale_contract_{Guid.NewGuid()}.db");
        try
        {
            RunScript(contractPath, File.ReadAllText(SchemaSqlPath()));

            Assert.Equal(ObjectNames(contractPath, "table"), ObjectNames(DbPath, "table"));
            Assert.Equal(ObjectNames(contractPath, "index"), ObjectNames(DbPath, "index"));

            foreach (string table in ObjectNames(contractPath, "table"))
                Assert.Equal(Describe(contractPath, $"PRAGMA table_info({table})"),
                             Describe(DbPath, $"PRAGMA table_info({table})"));

            foreach (string index in ObjectNames(contractPath, "index"))
                Assert.Equal(Describe(contractPath, $"PRAGMA index_info({index})"),
                             Describe(DbPath, $"PRAGMA index_info({index})"));
        }
        finally
        {
            Cleanup(contractPath);
        }
    }

    [Fact]
    public void SchemaSql_AlsoAsksForAutoVacuumBeforeItIsTooLate()
    {
        string path = Path.Combine(Path.GetTempPath(), $"telltale_contract_{Guid.NewGuid()}.db");
        try
        {
            RunScript(path, File.ReadAllText(SchemaSqlPath()));

            // The viewer's test databases are built from this file, so the ordering
            // has to be right here too, not only in Database.
            Assert.Equal(AutoVacuumIncremental, Convert.ToInt64(ScalarOn(path, "PRAGMA auto_vacuum")));
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void DatabaseCreatedBeforeTheFix_IsLeftAloneAndSaysSo()
    {
        string path = CreateLegacyDatabase();
        try
        {
            var logger = new CapturingLogger();
            using (new Database(path, logger))
            {
                // Converting means rewriting the whole file, so it does not happen
                // just because the collector started.
                Assert.Equal(AutoVacuumNone, Convert.ToInt64(ScalarOn(path, "PRAGMA auto_vacuum")));
            }

            var warning = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
            Assert.Contains("vacuumOnStartup", warning.Message);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void DatabaseCreatedBeforeTheFix_IsConvertedWhenTheOperatorOptsIn()
    {
        string path = CreateLegacyDatabase();
        try
        {
            var logger = new CapturingLogger();
            using (new Database(path, logger, vacuumOnStartup: true))
            {
                Assert.Equal(AutoVacuumIncremental, Convert.ToInt64(ScalarOn(path, "PRAGMA auto_vacuum")));
            }

            Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Warning);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void ConvertedDatabase_KeepsTheDataItAlreadyHeld()
    {
        string path = CreateLegacyDatabase();
        try
        {
            RunScript(path, """
                INSERT INTO process_instance (pid, create_time, name, first_seen, last_seen)
                VALUES (77, 100, 'survivor.exe', 1000, 2000);
                INSERT INTO machine (ts, cpu_pct) VALUES (1000, 42.0);
                """);

            using (new Database(path, new CapturingLogger(), vacuumOnStartup: true)) { }

            // A full VACUUM rewrites every page, so the data surviving it matters as
            // much as the pragma taking effect.
            Assert.Equal("survivor.exe", ScalarOn(path, "SELECT name FROM process_instance WHERE pid = 77"));
            Assert.Equal(42.0, Convert.ToDouble(ScalarOn(path, "SELECT cpu_pct FROM machine WHERE ts = 1000")));
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// Builds a database the way the collector did before issue #37 was fixed: WAL
    /// first, which writes the header, so the auto_vacuum request that follows has
    /// no effect. The assertion at the end means the tests above are exercising the
    /// real legacy shape rather than one this helper only claims to produce.
    /// </summary>
    private static string CreateLegacyDatabase()
    {
        string path = Path.Combine(Path.GetTempPath(), $"telltale_legacy_{Guid.NewGuid()}.db");

        using (var conn = new SqliteConnection($"Data Source={path}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA journal_mode = WAL;";
            cmd.ExecuteNonQuery();
            cmd.CommandText = "PRAGMA auto_vacuum = INCREMENTAL;";
            cmd.ExecuteNonQuery();
            cmd.CommandText = DdlOnly(File.ReadAllText(SchemaSqlPath()));
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        Assert.Equal(AutoVacuumNone, Convert.ToInt64(ScalarOn(path, "PRAGMA auto_vacuum")));
        return path;
    }

    /// <summary>schema.sql without its pragma header, so a caller can choose its own.</summary>
    private static string DdlOnly(string script) => string.Join('\n', script
        .Split('\n')
        .Where(line => !line.TrimStart().StartsWith("PRAGMA", StringComparison.OrdinalIgnoreCase)));

    private static string SchemaSqlPath() => Path.Combine(AppContext.BaseDirectory, "schema.sql");

    private static void RunScript(string path, string script)
    {
        using (var conn = new SqliteConnection($"Data Source={path}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = script;
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();
    }

    private static string[] ObjectNames(string path, string type) =>
        Strings(path, $"SELECT name FROM sqlite_master WHERE type = '{type}' " +
                      "AND name NOT LIKE 'sqlite_%' ORDER BY name");

    /// <summary>Flattens a pragma result into comparable text, one row per line.</summary>
    private static string Describe(string path, string pragma)
    {
        using var conn = new SqliteConnection($"Data Source={path}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = pragma;
        using var reader = cmd.ExecuteReader();

        var rows = new List<string>();
        while (reader.Read())
        {
            var fields = new List<string>();
            for (int i = 0; i < reader.FieldCount; i++)
                fields.Add($"{reader.GetName(i)}={reader.GetValue(i)}");
            rows.Add(string.Join(", ", fields));
        }
        return string.Join('\n', rows);
    }

    private static string[] Strings(string path, string sql)
    {
        using var conn = new SqliteConnection($"Data Source={path}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader();

        var results = new List<string>();
        while (reader.Read())
            results.Add(reader.GetString(0));
        return [.. results];
    }

    private static object? ScalarOn(string path, string sql)
    {
        using var conn = new SqliteConnection($"Data Source={path}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    private static void Cleanup(string path)
    {
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { File.Delete(path + suffix); } catch { /* best effort cleanup */ }
        }
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}
