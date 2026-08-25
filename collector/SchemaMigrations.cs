using Microsoft.Data.Sqlite;

namespace Telltale.Collector;

/// <summary>
/// Brings an existing database up to the schema shape this build expects.
///
/// A database records every schema version it has reached as a row in
/// <c>schema_version</c>, so the version it is currently at is the largest of
/// them. A database created by this build is written at <see cref="LatestVersion"/>
/// directly and records only that version; one created by an older build is
/// stepped forward through each migration in turn. Both routes end at the same
/// schema and the same <c>MAX(version)</c>.
/// </summary>
public static class SchemaMigrations
{
    /// <summary>
    /// The schema version this build writes and reads. Raise this whenever a
    /// migration is added below, and change <c>schema.sql</c> to match, so a
    /// database created from scratch and one migrated up end at the same shape.
    /// </summary>
    public const int LatestVersion = 3;

    /// <summary>
    /// One step, taking a database from the version before it to
    /// <see cref="Version"/>. Public so a test can hand <see cref="Apply"/> a list
    /// of its own: the real list holds a single entry that succeeds, which can
    /// demonstrate neither rollback nor ordering.
    /// </summary>
    /// <param name="IsAlreadyApplied">
    /// Asked, before <paramref name="Sql"/> runs, whether the database already
    /// carries this step's effect. A step that says yes is skipped and recorded
    /// as reached, which is what lets a migration whose statements cannot be
    /// written to survive a second run be replayed safely anyway.
    ///
    /// SQLite has no conditional form of <c>ALTER TABLE</c> or <c>CREATE TABLE</c>
    /// that both tolerates a repeat and leaves the definition it stores identical
    /// to the one in <c>schema.sql</c>. <c>IF NOT EXISTS</c> gives the first and
    /// loses the second, and the shape of a migrated database is compared against
    /// a fresh one statement by statement. Asking in C# gives both.
    ///
    /// Null means the statements are safe to run again on their own, which is how
    /// the rollup merge in version 2 is written.
    /// </param>
    public sealed record Migration(
        int Version,
        string Description,
        string Sql,
        Func<SqliteConnection, bool>? IsAlreadyApplied = null);

    private static readonly IReadOnlyList<Migration> Ordered =
    [
        new(2, "merge duplicate process rollup rows and make (ts, instance_id) unique",
            MergeDuplicateProcessRollupsSql),
        new(3, "record how long each phase of a sampling tick takes",
            AddTickPhaseTableSql,
            conn => HasTable(conn, "collector_tick_phase")),
    ];

    /// <summary>
    /// Whether <paramref name="table"/> exists. Used as a migration's
    /// <see cref="Migration.IsAlreadyApplied"/> check when the step creates one.
    /// </summary>
    private static bool HasTable(SqliteConnection conn, string table)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = @name";
        cmd.Parameters.AddWithValue("@name", table);

        return cmd.ExecuteScalar() is not null;
    }

    /// <summary>
    /// Reads the version a database is currently at. Zero means the version has
    /// not been recorded, which is treated as older than every migration so the
    /// guarded statements below get a chance to put it right.
    /// </summary>
    public static int ReadVersion(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT MAX(version) FROM schema_version";
        var value = cmd.ExecuteScalar();

        return value is null or DBNull ? 0 : Convert.ToInt32(value);
    }

    /// <summary>
    /// Applies every migration the database has not reached yet, and returns the
    /// version it ends at. A database already at or beyond the highest version in
    /// <paramref name="migrations"/> is left untouched.
    /// </summary>
    /// <param name="migrations">
    /// The steps to consider, defaulting to the ones this build ships. A test
    /// passes its own, because neither the promise that a failed step rolls back
    /// nor the order steps run in can be shown against a list holding one entry
    /// that succeeds.
    /// </param>
    public static int Apply(SqliteConnection conn, ILogger logger,
                            IReadOnlyList<Migration>? migrations = null)
    {
        var steps = migrations ?? Ordered;

        // Read from the steps in hand rather than from LatestVersion, so a caller
        // supplying its own list is measured against that list instead of against
        // a constant it knows nothing about. For the shipped list the two are the
        // same number, because LatestVersion is raised with every migration added
        // below, and LegacyDatabase_IsSteppedUpToTheLatestVersion fails if they
        // ever drift apart.
        int target = steps.Count == 0 ? LatestVersion : steps.Max(m => m.Version);

        int current = ReadVersion(conn);

        if (current > target)
        {
            // Written by a newer build. Migrating cannot help and rewriting the
            // schema backwards would lose whatever that build added, so the
            // database is left as it is and the mismatch is reported instead.
            // Whether to then run against it at all is the caller's decision:
            // see StartupDatabaseCheck.
            // LatestVersion here, not target, and deliberately so. This sentence
            // is about what the build knows, which is the constant; the
            // "Migrating to" line below is about where this call is heading,
            // which is the list in hand. They are the same number in production.
            // The placeholder is named apart from that one so a single logging
            // key does not carry two different meanings across one method.
            logger.LogWarning(
                "Database is at schema version {Current}, newer than the version {BuildVersion} this build knows. " +
                "Leaving it unchanged.", current, LatestVersion);

            return current;
        }

        var pending = steps.Where(m => m.Version > current).OrderBy(m => m.Version).ToList();
        if (pending.Count == 0) return current;

        logger.LogInformation("Database is at schema version {Current}. Migrating to {Latest}.",
            current, target);

        foreach (var migration in pending)
        {
            // One transaction per migration, covering the schema change and the
            // row that records it. A failure part way through rolls the whole
            // step back, so the database stays at the version it came in at
            // rather than at a shape no version describes.
            // A migration rewrites whole tables, and the rollup tables can hold
            // months of rows, so this can take a noticeable time on a database
            // that has been recording for a while. It runs before the collector
            // takes its first sample, so time it and say so rather than leaving an
            // unexplained pause at startup.
            var started = System.Diagnostics.Stopwatch.StartNew();

            using (var tx = conn.BeginTransaction())
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;

                // Asked inside the transaction, so a step cannot be judged against
                // a database some other writer changes before its statements run.
                // A step that is already carried still records its version below:
                // the point of the migration is the shape, not the statements, and
                // the shape is already there.
                if (migration.IsAlreadyApplied?.Invoke(conn) != true)
                {
                    cmd.CommandText = migration.Sql;
                    cmd.ExecuteNonQuery();
                }

                cmd.CommandText = "INSERT OR IGNORE INTO schema_version (version) VALUES (@version)";
                cmd.Parameters.AddWithValue("@version", migration.Version);
                cmd.ExecuteNonQuery();

                tx.Commit();
            }

            logger.LogInformation("Applied schema migration {Version} in {ElapsedMs} ms: {Description}.",
                migration.Version, started.ElapsedMilliseconds, migration.Description);
        }

        // The rewrite above sits in the write ahead log until something checkpoints
        // it, which would otherwise be the first rollup cycle minutes later. Until
        // then the sidecar holds a second copy of everything the migration touched,
        // which can push a database well past the size cap it is meant to keep to.
        using (var checkpoint = conn.CreateCommand())
        {
            checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
            checkpoint.ExecuteNonQuery();
        }

        // Read back rather than assuming LatestVersion, so that raising it without
        // adding the matching migration reports the version the database is really
        // at instead of the one this build wishes it were at.
        return ReadVersion(conn);
    }

    /// <summary>
    /// Version 2. Merges the duplicate <c>(ts, instance_id)</c> rows that
    /// existing databases hold in the process rollup tables, then makes that pair
    /// unique so the same mistake fails loudly instead of double counting.
    ///
    /// The duplicates come from the rollup wedge fixed in PR #30: the same bucket
    /// was promoted more than once, which the machine tables rejected on their
    /// primary key but the process tables silently accepted.
    ///
    /// Rows are combined so that a repaired bucket holds what a single correct
    /// promotion would have written: the maxima and totals exactly as the tier two
    /// re-rollup combines them, and the averages weighted by sample_count.
    ///
    /// A half whose average is NULL is left out of the weighting altogether rather
    /// than only out of the numerator. The collector stores a sample precisely when
    /// CPU could not be computed, so a bucket can carry a NULL average over a real
    /// sample count, and charging that count against a value nobody measured would
    /// drag the repaired average toward zero. This matches the weighting the live
    /// re-rollup uses in <see cref="Database"/> and the viewer uses on the read
    /// side, so a repaired bucket and a freshly promoted one are computed the same
    /// way. It matters more here than in either of those: this migration repairs
    /// history in place and cannot be run again once the duplicates are gone, so
    /// anything it gets wrong is permanent.
    ///
    /// Every statement is written to survive being run again. The version row goes
    /// in inside the same transaction, so a half applied migration cannot happen,
    /// but <see cref="ReadVersion"/> reports 0 for a <c>schema_version</c> table
    /// that exists and is empty, and that replays every migration against a
    /// database which may already carry its effect.
    /// </summary>
    private const string MergeDuplicateProcessRollupsSql = """
        DROP TABLE IF EXISTS sample_1m_dedupe;
        CREATE TABLE sample_1m_dedupe AS
        SELECT ts,
               instance_id,
               SUM(cpu_pct_avg * sample_count)
                   / NULLIF(SUM(CASE WHEN cpu_pct_avg IS NULL THEN 0 ELSE sample_count END), 0)
                                                                   AS cpu_pct_avg,
               MAX(cpu_pct_max)                                    AS cpu_pct_max,
               MAX(private_mb_max)                                 AS private_mb_max,
               MAX(working_set_mb_max)                             AS working_set_mb_max,
               SUM(io_kb_total)                                    AS io_kb_total,
               SUM(sample_count)                                   AS sample_count
        FROM sample_1m
        GROUP BY ts, instance_id
        HAVING COUNT(*) > 1;

        -- The delete below probes this table once per row of sample_1m. SQLite
        -- would most likely build an automatic index for that, but this is the
        -- largest table in the database and the cost of being wrong is quadratic,
        -- so the index is not left to chance.
        CREATE UNIQUE INDEX sample_1m_dedupe_key ON sample_1m_dedupe(ts, instance_id);

        DELETE FROM sample_1m
        WHERE EXISTS (SELECT 1 FROM sample_1m_dedupe d
                      WHERE d.ts = sample_1m.ts AND d.instance_id = sample_1m.instance_id);

        INSERT INTO sample_1m
            (ts, instance_id, cpu_pct_avg, cpu_pct_max, private_mb_max,
             working_set_mb_max, io_kb_total, sample_count)
        SELECT ts, instance_id, cpu_pct_avg, cpu_pct_max, private_mb_max,
               working_set_mb_max, io_kb_total, sample_count
        FROM sample_1m_dedupe;

        DROP TABLE sample_1m_dedupe;

        DROP TABLE IF EXISTS sample_10m_dedupe;
        CREATE TABLE sample_10m_dedupe AS
        SELECT ts,
               instance_id,
               SUM(cpu_pct_avg * sample_count)
                   / NULLIF(SUM(CASE WHEN cpu_pct_avg IS NULL THEN 0 ELSE sample_count END), 0)
                                                                   AS cpu_pct_avg,
               MAX(cpu_pct_max)                                    AS cpu_pct_max,
               MAX(private_mb_max)                                 AS private_mb_max,
               MAX(working_set_mb_max)                             AS working_set_mb_max,
               SUM(io_kb_total)                                    AS io_kb_total,
               SUM(sample_count)                                   AS sample_count
        FROM sample_10m
        GROUP BY ts, instance_id
        HAVING COUNT(*) > 1;

        CREATE UNIQUE INDEX sample_10m_dedupe_key ON sample_10m_dedupe(ts, instance_id);

        DELETE FROM sample_10m
        WHERE EXISTS (SELECT 1 FROM sample_10m_dedupe d
                      WHERE d.ts = sample_10m.ts AND d.instance_id = sample_10m.instance_id);

        INSERT INTO sample_10m
            (ts, instance_id, cpu_pct_avg, cpu_pct_max, private_mb_max,
             working_set_mb_max, io_kb_total, sample_count)
        SELECT ts, instance_id, cpu_pct_avg, cpu_pct_max, private_mb_max,
               working_set_mb_max, io_kb_total, sample_count
        FROM sample_10m_dedupe;

        DROP TABLE sample_10m_dedupe;

        -- Dropped and recreated rather than created with IF NOT EXISTS so that the
        -- statement stored in sqlite_master is character for character the one in
        -- schema.sql. A migrated database and a fresh one are then provably the
        -- same shape, which is what the migration tests compare.
        DROP INDEX IF EXISTS ix_s1m_ts;
        DROP INDEX IF EXISTS ux_s1m_ts_inst;
        CREATE UNIQUE INDEX ux_s1m_ts_inst ON sample_1m(ts, instance_id);

        DROP INDEX IF EXISTS ix_s10m_ts;
        DROP INDEX IF EXISTS ux_s10m_ts_inst;
        CREATE UNIQUE INDEX ux_s10m_ts_inst ON sample_10m(ts, instance_id);
        """;

    /// <summary>
    /// Version 3. Adds the table that records where a sampling tick spent its
    /// time. Existing rows in <c>collector_health</c> keep their whole tick cost
    /// and simply have no breakdown, which is the truth about them.
    ///
    /// Written out in full rather than with <c>IF NOT EXISTS</c> so the definition
    /// SQLite stores is character for character the one in <c>schema.sql</c>, and a
    /// migrated database is provably the same shape as a fresh one. Running it a
    /// second time is prevented by the step's
    /// <see cref="Migration.IsAlreadyApplied"/> check instead.
    /// </summary>
    private const string AddTickPhaseTableSql = """
        CREATE TABLE collector_tick_phase (
            ts                INTEGER PRIMARY KEY,
            sampler_ms        REAL,
            machine_sample_ms REAL,
            identity_ms       REAL,
            instance_ms       REAL,
            sample_write_ms   REAL,
            machine_write_ms  REAL
        );
        """;
}
