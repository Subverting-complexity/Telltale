using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Telltale.Collector;

namespace Collector.Tests;

/// <summary>
/// Covers the migration path added for issue #32: a database created before the
/// path existed is stepped up to the current shape, the duplicate process rollup
/// rows such a database already holds are merged the way a single correct rollup
/// would have written them, and the pair those tables are keyed on becomes
/// unique so the same mistake cannot repeat silently.
/// </summary>
public class SchemaMigrationTests : IDisposable
{
    /// <summary>An arbitrary timestamp sitting exactly on a minute boundary.</summary>
    private const long BucketStart = 1_700_000_000_000L / 60_000L * 60_000L;

    /// <summary>Values <c>PRAGMA auto_vacuum</c> reports for off and for incremental.</summary>
    private const long AutoVacuumOff = 0;
    private const long AutoVacuumIncremental = 2;

    private readonly List<string> _dbPaths = [];

    [Fact]
    public void FreshDatabase_IsCreatedAtTheLatestVersion()
    {
        using var db = OpenCollectorDatabase(NewDbPath());

        Assert.Equal(SchemaMigrations.LatestVersion, db.SchemaVersion);
    }

    [Fact]
    public void LegacyDatabase_IsSteppedUpToTheLatestVersion()
    {
        string path = CreateLegacyDatabase();

        using (var conn = Connect(path))
            Assert.Equal(1, SchemaMigrations.ReadVersion(conn));

        using var db = OpenCollectorDatabase(path);

        Assert.Equal(SchemaMigrations.LatestVersion, db.SchemaVersion);
    }

    [Theory]
    [InlineData("sample_1m")]
    [InlineData("sample_10m")]
    public void Migration_MergesDuplicateRows_WeightingAveragesBySampleCount(string table)
    {
        string path = CreateLegacyDatabase();

        using (var conn = Connect(path))
        {
            long instanceId = InsertInstance(conn, pid: 4242);

            // The two halves a repeated promotion of the same bucket left behind.
            // The sample counts differ so that a weighted merge and a plain
            // average of the two rows give different answers.
            InsertRollupRow(conn, table, BucketStart, instanceId,
                cpuAvg: 10, cpuMax: 20, privateMax: 100, workingSetMax: 150, ioTotal: 5, sampleCount: 12);
            InsertRollupRow(conn, table, BucketStart, instanceId,
                cpuAvg: 40, cpuMax: 50, privateMax: 80, workingSetMax: 200, ioTotal: 7, sampleCount: 4);
        }

        using (OpenCollectorDatabase(path)) { }

        using var check = Connect(path);

        Assert.Equal(1L, Scalar(check, $"SELECT COUNT(*) FROM {table}"));

        // (10*12 + 40*4) / 16 = 17.5. A plain average of the two rows would be 25.
        Assert.Equal(17.5, ScalarDouble(check, $"SELECT cpu_pct_avg FROM {table}"), 6);
        Assert.Equal(50.0, ScalarDouble(check, $"SELECT cpu_pct_max FROM {table}"), 6);
        Assert.Equal(100.0, ScalarDouble(check, $"SELECT private_mb_max FROM {table}"), 6);
        Assert.Equal(200.0, ScalarDouble(check, $"SELECT working_set_mb_max FROM {table}"), 6);
        Assert.Equal(12.0, ScalarDouble(check, $"SELECT io_kb_total FROM {table}"), 6);
        Assert.Equal(16L, Scalar(check, $"SELECT sample_count FROM {table}"));
    }

    [Theory]
    [InlineData("sample_1m")]
    [InlineData("sample_10m")]
    public void Migration_MergesDuplicateRows_LeavingOutHalvesWhereCpuWasNeverMeasured(string table)
    {
        string path = CreateLegacyDatabase();

        using (var conn = Connect(path))
        {
            long instanceId = InsertInstance(conn, pid: 4243);

            // The collector stores a sample precisely when CPU could not be
            // computed, so a bucket whose raw samples all lacked a CPU figure
            // rolls up to a NULL average over a real sample count. The unmeasured
            // half is given the larger weight here, because that is the case that
            // separates a correct merge from one that counts it.
            InsertRollupRow(conn, table, BucketStart, instanceId,
                cpuAvg: null, cpuMax: 0, privateMax: 100, workingSetMax: 150, ioTotal: 5, sampleCount: 12);
            InsertRollupRow(conn, table, BucketStart, instanceId,
                cpuAvg: 40, cpuMax: 50, privateMax: 80, workingSetMax: 200, ioTotal: 7, sampleCount: 4);
        }

        using (OpenCollectorDatabase(path)) { }

        using var check = Connect(path);

        Assert.Equal(1L, Scalar(check, $"SELECT COUNT(*) FROM {table}"));

        // A single correct promotion averages the raw samples that carried a CPU
        // figure and ignores the rest, giving 40. Counting the unmeasured half's
        // weight against it would give 160 / 16 = 10.
        Assert.Equal(40.0, ScalarDouble(check, $"SELECT cpu_pct_avg FROM {table}"), 6);

        // The count still covers every raw sample the bucket held, the same
        // pairing the raw to one minute rollup makes between AVG and COUNT(*).
        Assert.Equal(16L, Scalar(check, $"SELECT sample_count FROM {table}"));
    }

    [Theory]
    [InlineData("sample_1m")]
    [InlineData("sample_10m")]
    public void Migration_LeavesRowsThatAreNotDuplicatedUntouched(string table)
    {
        string path = CreateLegacyDatabase();

        using (var conn = Connect(path))
        {
            long first = InsertInstance(conn, pid: 1);
            long second = InsertInstance(conn, pid: 2);

            // Same bucket, different instances, so neither is a duplicate of the
            // other and both must survive exactly as written.
            InsertRollupRow(conn, table, BucketStart, first,
                cpuAvg: 11, cpuMax: 21, privateMax: 101, workingSetMax: 151, ioTotal: 6, sampleCount: 12);
            InsertRollupRow(conn, table, BucketStart, second,
                cpuAvg: 33, cpuMax: 44, privateMax: 55, workingSetMax: 66, ioTotal: 8, sampleCount: 7);
        }

        using (OpenCollectorDatabase(path)) { }

        using var check = Connect(path);

        Assert.Equal(2L, Scalar(check, $"SELECT COUNT(*) FROM {table}"));
        Assert.Equal(11.0, ScalarDouble(check, $"SELECT cpu_pct_avg FROM {table} WHERE instance_id = 1"), 6);
        Assert.Equal(12L, Scalar(check, $"SELECT sample_count FROM {table} WHERE instance_id = 1"));
        Assert.Equal(33.0, ScalarDouble(check, $"SELECT cpu_pct_avg FROM {table} WHERE instance_id = 2"), 6);
        Assert.Equal(7L, Scalar(check, $"SELECT sample_count FROM {table} WHERE instance_id = 2"));
    }

    [Theory]
    [InlineData("sample_1m")]
    [InlineData("sample_10m")]
    public void AfterMigration_ASecondRowForTheSameBucketAndInstanceIsRejected(string table)
    {
        string path = CreateLegacyDatabase();

        using (var conn = Connect(path))
            InsertInstance(conn, pid: 7);

        using (OpenCollectorDatabase(path)) { }

        using var conn2 = Connect(path);
        InsertRollupRow(conn2, table, BucketStart, instanceId: 1,
            cpuAvg: 1, cpuMax: 2, privateMax: 3, workingSetMax: 4, ioTotal: 5, sampleCount: 6);

        var duplicate = Assert.Throws<SqliteException>(() =>
            InsertRollupRow(conn2, table, BucketStart, instanceId: 1,
                cpuAvg: 1, cpuMax: 2, privateMax: 3, workingSetMax: 4, ioTotal: 5, sampleCount: 6));

        Assert.Contains("UNIQUE", duplicate.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DatabaseWithNoRecordedVersion_HasEveryMigrationReplayedSafely()
    {
        string path = CreateLegacyDatabase();

        using (var conn = Connect(path))
        {
            long instanceId = InsertInstance(conn, pid: 8);
            InsertRollupRow(conn, "sample_1m", BucketStart, instanceId,
                cpuAvg: 10, cpuMax: 20, privateMax: 30, workingSetMax: 40, ioTotal: 50, sampleCount: 3);
            InsertRollupRow(conn, "sample_1m", BucketStart, instanceId,
                cpuAvg: 20, cpuMax: 25, privateMax: 35, workingSetMax: 45, ioTotal: 55, sampleCount: 1);

            // A schema_version table that exists but records nothing. ReadVersion
            // reports 0 for it, which replays every migration, so this is the case
            // that the guarded statements in each migration exist to survive.
            Execute(conn, "DELETE FROM schema_version");
            Assert.Equal(0, SchemaMigrations.ReadVersion(conn));
        }

        using (OpenCollectorDatabase(path)) { }

        using var check = Connect(path);

        // (10*3 + 20*1) / 4 = 12.5.
        Assert.Equal(1L, Scalar(check, "SELECT COUNT(*) FROM sample_1m"));
        Assert.Equal(12.5, ScalarDouble(check, "SELECT cpu_pct_avg FROM sample_1m"), 6);
        Assert.Equal(SchemaMigrations.LatestVersion, SchemaMigrations.ReadVersion(check));

        using var fresh = Connect(OpenCollectorDatabase(NewDbPath()).Path);
        Assert.Equal(Shape(fresh), Shape(check));
    }

    [Fact]
    public void MigratedDatabase_EndsAtTheSameShapeAsAFreshOne()
    {
        string legacyPath = CreateLegacyDatabase();

        using (var conn = Connect(legacyPath))
        {
            long instanceId = InsertInstance(conn, pid: 99);
            InsertRollupRow(conn, "sample_1m", BucketStart, instanceId,
                cpuAvg: 10, cpuMax: 20, privateMax: 30, workingSetMax: 40, ioTotal: 50, sampleCount: 3);
            InsertRollupRow(conn, "sample_1m", BucketStart, instanceId,
                cpuAvg: 20, cpuMax: 25, privateMax: 35, workingSetMax: 45, ioTotal: 55, sampleCount: 1);
        }

        using (OpenCollectorDatabase(legacyPath)) { }

        using var freshDb = OpenCollectorDatabase(NewDbPath());
        string schemaFilePath = CreateDatabaseFromSchemaFile("schema.sql");

        using var migrated = Connect(legacyPath);
        using var fresh = Connect(freshDb.Path);
        using var fromSchemaFile = Connect(schemaFilePath);

        // All three routes to a current database must agree, statement for
        // statement: the collector creating one, schema.sql creating one, and an
        // old one being migrated up.
        Assert.Equal(Shape(fresh), Shape(migrated));
        Assert.Equal(Shape(fresh), Shape(fromSchemaFile));

        Assert.Equal(SchemaMigrations.LatestVersion, SchemaMigrations.ReadVersion(migrated));
        Assert.Equal(SchemaMigrations.LatestVersion, SchemaMigrations.ReadVersion(fresh));
        Assert.Equal(SchemaMigrations.LatestVersion, SchemaMigrations.ReadVersion(fromSchemaFile));
    }

    [Fact]
    public void ReopeningAMigratedDatabase_ChangesNothing()
    {
        string path = CreateLegacyDatabase();

        using (var conn = Connect(path))
        {
            long instanceId = InsertInstance(conn, pid: 5);
            InsertRollupRow(conn, "sample_1m", BucketStart, instanceId,
                cpuAvg: 10, cpuMax: 20, privateMax: 30, workingSetMax: 40, ioTotal: 50, sampleCount: 2);
            InsertRollupRow(conn, "sample_1m", BucketStart, instanceId,
                cpuAvg: 30, cpuMax: 40, privateMax: 20, workingSetMax: 60, ioTotal: 10, sampleCount: 2);
        }

        using (OpenCollectorDatabase(path)) { }

        string shapeAfterFirstOpen;
        double cpuAfterFirstOpen;
        using (var conn = Connect(path))
        {
            shapeAfterFirstOpen = Shape(conn);
            cpuAfterFirstOpen = ScalarDouble(conn, "SELECT cpu_pct_avg FROM sample_1m");
        }

        using (OpenCollectorDatabase(path)) { }

        using var check = Connect(path);
        Assert.Equal(shapeAfterFirstOpen, Shape(check));
        Assert.Equal(cpuAfterFirstOpen, ScalarDouble(check, "SELECT cpu_pct_avg FROM sample_1m"), 6);
        Assert.Equal(1L, Scalar(check, "SELECT COUNT(*) FROM sample_1m"));
    }

    [Fact]
    public void DatabaseWrittenByANewerBuild_IsLeftAloneAndRefused()
    {
        string path = NewDbPath();
        using (OpenCollectorDatabase(path)) { }

        int newerVersion = SchemaMigrations.LatestVersion + 1;
        string shapeBefore;
        using (var conn = Connect(path))
        {
            Execute(conn, $"INSERT INTO schema_version (version) VALUES ({newerVersion})");
            shapeBefore = Shape(conn);
        }

        var logger = new CapturingLogger();
        using (var db = new Database(path, logger))
        {
            // Opening still reports the version it found and changes nothing.
            // Migrating cannot help, and rewriting the schema backwards would
            // lose whatever the newer build added.
            Assert.Equal(newerVersion, db.SchemaVersion);

            // What is new is that the collector then declines to run against it
            // rather than recording into a shape it does not understand.
            Assert.NotNull(StartupDatabaseCheck.RefusalForNewerDatabase(
                db.SchemaVersion, SchemaMigrations.LatestVersion, path));
        }

        using var check = Connect(path);
        Assert.Equal(shapeBefore, Shape(check));
        Assert.Equal(newerVersion, SchemaMigrations.ReadVersion(check));
        Assert.Contains(logger.Warnings, w => w.Contains("newer than the version"));
    }

    [Fact]
    public void DatabaseWrittenByANewerBuild_IsNotVacuumedEvenWhenOptedIn()
    {
        // The collector is about to refuse this database, so opening it to read
        // the version must not write to it. The auto_vacuum conversion is the
        // largest write there is: it rewrites every page of the file.
        string path = CreateLegacyDatabase();
        using (var conn = Connect(path))
            Execute(conn, $"INSERT INTO schema_version (version) VALUES ({SchemaMigrations.LatestVersion + 1})");

        using (new Database(path, new CapturingLogger(), vacuumOnStartup: true)) { }

        using var check = Connect(path);
        Assert.Equal(AutoVacuumOff, Scalar(check, "PRAGMA auto_vacuum"));
    }

    [Fact]
    public void DatabaseThisBuildUnderstands_IsStillVacuumedWhenOptedIn()
    {
        // The control for the test above. Without it that one would still pass
        // if the conversion had simply stopped working altogether.
        string path = CreateLegacyDatabase();

        using (var conn = Connect(path))
            Assert.Equal(AutoVacuumOff, Scalar(conn, "PRAGMA auto_vacuum"));

        using (new Database(path, new CapturingLogger(), vacuumOnStartup: true)) { }

        using var check = Connect(path);
        Assert.Equal(AutoVacuumIncremental, Scalar(check, "PRAGMA auto_vacuum"));
    }

    [Fact]
    public void MigrationThatFails_LeavesTheVersionAndTheShapeUnchanged()
    {
        string path = NewDbPath();
        using (OpenCollectorDatabase(path)) { }

        using var conn = Connect(path);
        string shapeBefore = Shape(conn);

        // The first statement succeeds and the second does not, so there is a
        // half finished change for the transaction to undo. A migration that
        // fell over on its very first statement would leave nothing behind and
        // would prove nothing about rolling back.
        var failing = new SchemaMigrations.Migration(
            SchemaMigrations.LatestVersion + 1,
            "create a table, then fail",
            """
            CREATE TABLE half_applied (x INTEGER);
            INSERT INTO no_such_table (x) VALUES (1);
            """);

        Assert.Throws<SqliteException>(
            () => SchemaMigrations.Apply(conn, new CapturingLogger(), [failing]));

        // The shape assertion is the half that does the work. The version
        // assertion alone would pass even with the transaction removed, because
        // a row written after the failing statement never gets to run either
        // way; only the shape shows the half applied table left behind.
        //
        // Verified by mutation: dropping the transaction from Apply fails this
        // test. Note that moving the cmd.Transaction assignment does not, and
        // cannot, because a SQLite transaction belongs to the connection rather
        // than to the command.
        Assert.Equal(SchemaMigrations.LatestVersion, SchemaMigrations.ReadVersion(conn));
        Assert.Equal(shapeBefore, Shape(conn));
    }

    [Fact]
    public void Migrations_AreAppliedInVersionOrder()
    {
        string path = NewDbPath();
        using (OpenCollectorDatabase(path)) { }

        using var conn = Connect(path);

        // Created here rather than by either migration, so that both orders run
        // without error and the order they ran in shows up in the rows. Left to
        // one of the migrations, the wrong order would fail instead, which
        // proves the same point only by accident.
        Execute(conn, "CREATE TABLE applied_order (step INTEGER)");

        int first = SchemaMigrations.LatestVersion + 1;
        int second = SchemaMigrations.LatestVersion + 2;

        // Handed over highest first, so a build that walked the list as given
        // would record them the wrong way round.
        SchemaMigrations.Apply(conn, new CapturingLogger(), [RecordStep(second), RecordStep(first)]);

        Assert.Equal(2L, Scalar(conn, "SELECT COUNT(*) FROM applied_order"));
        Assert.Equal(first, Scalar(conn, "SELECT step FROM applied_order ORDER BY rowid LIMIT 1"));
        Assert.Equal(second, Scalar(conn, "SELECT step FROM applied_order ORDER BY rowid DESC LIMIT 1"));
        Assert.Equal(second, SchemaMigrations.ReadVersion(conn));
    }

    [Fact]
    public void MigrationTheDatabaseHasAlreadyPassed_IsNotAppliedAgain()
    {
        string path = NewDbPath();
        using (OpenCollectorDatabase(path)) { }

        using var conn = Connect(path);
        Execute(conn, "CREATE TABLE applied_order (step INTEGER)");

        int first = SchemaMigrations.LatestVersion + 1;
        int second = SchemaMigrations.LatestVersion + 2;
        Execute(conn, $"INSERT INTO schema_version (version) VALUES ({first})");

        SchemaMigrations.Apply(conn, new CapturingLogger(), [RecordStep(first), RecordStep(second)]);

        Assert.Equal(1L, Scalar(conn, "SELECT COUNT(*) FROM applied_order"));
        Assert.Equal(second, Scalar(conn, "SELECT step FROM applied_order"));
        Assert.Equal(second, SchemaMigrations.ReadVersion(conn));
    }

    /// <summary>
    /// A migration that does nothing but write down that it ran, so that the
    /// order a set of them ran in can be read back afterwards.
    /// </summary>
    private static SchemaMigrations.Migration RecordStep(int version) =>
        new(version, $"record step {version}",
            $"INSERT INTO applied_order (step) VALUES ({version})");


    /// <summary>
    /// Every object in the database, as the text that created it. Comparing this
    /// between two databases is what proves they are the same shape.
    /// </summary>
    private static string Shape(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT type, name, sql FROM sqlite_master WHERE sql IS NOT NULL ORDER BY type, name";

        var entries = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            entries.Add($"{reader.GetString(0)} {reader.GetString(1)}\n{Normalise(reader.GetString(2))}");

        return string.Join("\n\n", entries);
    }

    /// <summary>
    /// SQLite stores each definition as the statement that produced it, so the
    /// line endings of the file that statement came from land in the comparison.
    /// Those are normalised away: schema.sql is read from disk while the
    /// collector keeps its copy compiled into the assembly, and the build runs on
    /// both Windows and Linux, so the convention can differ without the shape
    /// differing at all.
    /// </summary>
    private static string Normalise(string sql) => sql.Replace("\r\n", "\n");

    private string CreateLegacyDatabase() => CreateDatabaseFromSchemaFile("schema-v1.sql");

    private string CreateDatabaseFromSchemaFile(string fileName)
    {
        string path = NewDbPath();
        using var conn = Connect(path);
        Execute(conn, File.ReadAllText(Path.Combine(AppContext.BaseDirectory, fileName)));

        return path;
    }

    private static long InsertInstance(SqliteConnection conn, int pid)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "INSERT INTO process_instance (pid, create_time, name, first_seen, last_seen) " +
            "VALUES (@pid, @pid, 'test.exe', 0, 0); SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("@pid", pid);

        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private static void InsertRollupRow(SqliteConnection conn, string table, long ts, long instanceId,
        double? cpuAvg, double cpuMax, double privateMax, double workingSetMax, double ioTotal, int sampleCount)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            $"INSERT INTO {table} (ts, instance_id, cpu_pct_avg, cpu_pct_max, private_mb_max, " +
            "working_set_mb_max, io_kb_total, sample_count) " +
            "VALUES (@ts, @instance, @cpuAvg, @cpuMax, @privateMax, @workingSetMax, @ioTotal, @count)";
        cmd.Parameters.AddWithValue("@ts", ts);
        cmd.Parameters.AddWithValue("@instance", instanceId);
        cmd.Parameters.AddWithValue("@cpuAvg", (object?)cpuAvg ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@cpuMax", cpuMax);
        cmd.Parameters.AddWithValue("@privateMax", privateMax);
        cmd.Parameters.AddWithValue("@workingSetMax", workingSetMax);
        cmd.Parameters.AddWithValue("@ioTotal", ioTotal);
        cmd.Parameters.AddWithValue("@count", sampleCount);
        cmd.ExecuteNonQuery();
    }

    private static void Execute(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static long Scalar(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;

        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private static double ScalarDouble(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;

        return Convert.ToDouble(cmd.ExecuteScalar());
    }

    private static SqliteConnection Connect(string path)
    {
        var conn = new SqliteConnection($"Data Source={path}");
        conn.Open();

        return conn;
    }

    private TrackedDatabase OpenCollectorDatabase(string path) => new(path, new CapturingLogger());

    private string NewDbPath()
    {
        string path = Path.Combine(Path.GetTempPath(), $"telltale_migration_{Guid.NewGuid():N}.db");
        _dbPaths.Add(path);

        return path;
    }

    public void Dispose()
    {
        // The pool keeps the WAL sidecar files open, which makes the deletes
        // below fail on Windows.
        SqliteConnection.ClearAllPools();

        foreach (string path in _dbPaths)
        {
            foreach (string suffix in new[] { "", "-wal", "-shm" })
            {
                try { File.Delete(path + suffix); } catch { /* best effort cleanup */ }
            }
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>A <see cref="Database"/> that remembers the path it was opened on.</summary>
    private sealed class TrackedDatabase(string path, ILogger logger) : IDisposable
    {
        private readonly Database _db = new(path, logger);

        public string Path { get; } = path;

        public int SchemaVersion => _db.SchemaVersion;

        public void Dispose() => _db.Dispose();
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning) Warnings.Add(formatter(state, exception));
        }
    }
}
