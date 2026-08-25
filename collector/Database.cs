using Microsoft.Data.Sqlite;

namespace Telltale.Collector;

/// <summary>
/// Owns the collector's SQLite connection and every statement run against it.
///
/// One instance is shared by the two hosted services, which run on independent
/// timers and therefore overlap regularly. A <see cref="SqliteConnection"/> is not
/// thread safe and carries at most one transaction, so every public method here
/// runs under <c>_gate</c>. The lock is taken per call rather than per rollup
/// cycle: a cycle makes about a dozen calls, so a sampler write waits for one
/// statement group rather than for the whole cycle.
///
/// That is one wait, not one lost sample. <see cref="PeriodicTimer"/> does not
/// queue missed ticks, so a statement group slow enough to span several sampling
/// intervals costs every tick it spans. The readings that do land stay correct,
/// because CollectorWorker measures CPU from a stopwatch delta rather than
/// assuming a fixed interval.
/// </summary>
public sealed class Database : IDisposable
{
    /// <summary>Value <c>PRAGMA auto_vacuum</c> reports when incremental vacuum is on.</summary>
    private const long AutoVacuumIncremental = 2;

    /// <summary>
    /// Serialises access to <c>_conn</c>. Held by every public method; private
    /// helpers named <c>*Locked</c> assume the caller already holds it.
    /// </summary>
    private readonly Lock _gate = new();

    private readonly SqliteConnection _conn;
    private readonly ILogger _logger;

    /// <summary>Read and written under <c>_gate</c>.</summary>
    private bool _disposed;

    /// <summary>
    /// The schema version this database is at once it has been opened, which is
    /// <see cref="SchemaMigrations.LatestVersion"/> unless a newer build wrote it.
    /// </summary>
    public int SchemaVersion { get; private set; }

    /// <param name="vacuumOnStartup">
    /// Whether a database created before the auto_vacuum ordering was fixed may be
    /// converted on open. The conversion is a full <c>VACUUM</c>, so it is opt in.
    /// </param>
    public Database(string dbPath, ILogger logger, bool vacuumOnStartup = false)
    {
        _logger = logger;
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        // Built rather than interpolated. A semicolon is a legal character in a
        // Windows filename and is also the separator in a connection string, so
        // interpolating a path that contains one produced a malformed string and
        // an ArgumentException, which is not among the types the collector
        // startup path catches and explains.
        _conn = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString());
        _conn.Open();

        try
        {
            // No lock in the constructor: nothing else can reach this instance yet.
            InitSchema();

            // A database at a version this build does not understand is one the
            // collector is about to refuse to run against, so nothing here may
            // write to it. The conversion below is a full VACUUM that rewrites
            // every page, which is the largest write the collector ever makes.
            if (SchemaVersion <= SchemaMigrations.LatestVersion)
                ReconcileAutoVacuum(vacuumOnStartup);
        }
        catch
        {
            // InitSchema now runs migrations against whatever an older build left
            // behind, so it has a real chance of throwing. Without this the
            // connection would stay open on an unreachable object and keep the
            // database file and its write ahead log locked until finalization.
            _conn.Dispose();
            throw;
        }
    }

    private void InitSchema()
    {
        using var cmd = _conn.CreateCommand();

        // The order is load bearing. auto_vacuum can only be chosen before anything
        // writes the database header, and switching the journal mode to WAL writes
        // it. Set the other way round the auto_vacuum statement still succeeds but
        // changes nothing, leaving IncrementalVacuum a no-op and the file unable to
        // release pages it has freed.
        cmd.CommandText = "PRAGMA auto_vacuum = INCREMENTAL;";
        cmd.ExecuteNonQuery();

        cmd.CommandText = "PRAGMA journal_mode = WAL;";
        cmd.ExecuteNonQuery();

        cmd.CommandText = "PRAGMA synchronous = NORMAL;";
        cmd.ExecuteNonQuery();

        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='schema_version'";
        var exists = cmd.ExecuteScalar();
        if (exists != null)
        {
            // The database already exists, and an earlier build may have created
            // it. Step it forward to the shape this build expects instead of
            // assuming it is already there.
            SchemaVersion = SchemaMigrations.Apply(_conn, _logger);
            return;
        }

        // One transaction for the whole schema. Interrupted half way without it,
        // the database would keep a schema_version row saying 2 over tables that
        // were never created, and every later start would read that version, skip
        // the migrations and fail with no way back.
        using (var tx = _conn.BeginTransaction())
        {
            cmd.Transaction = tx;
            cmd.CommandText = CreateSchemaSql;
            cmd.ExecuteNonQuery();
            tx.Commit();
        }

        cmd.Transaction = null;
        SchemaVersion = SchemaMigrations.LatestVersion;
        _logger.LogInformation("Database schema created (version {Version}).", SchemaVersion);
    }

    /// <summary>
    /// The schema a brand new database is created with. This is a copy of
    /// <c>schema.sql</c>, the file the viewer treats as the contract, minus the
    /// pragmas at the top of it which <see cref="InitSchema"/> applies itself.
    /// The two are kept identical, and a test creates a database each way and
    /// compares the result so that they cannot quietly drift apart.
    /// </summary>
    /// <summary>
    /// Handles a database created before the pragma ordering was corrected, where
    /// auto_vacuum is off and cannot be turned on without rewriting the whole file.
    ///
    /// Converting needs a full <c>VACUUM</c>, which rewrites every page and wants
    /// roughly twice the file size in free disk while it runs. Telltale is meant to
    /// sit in the background unnoticed, so spending minutes of disk on every start
    /// without being asked is the wrong default. Doing nothing silently is also
    /// wrong, since the capture would never reclaim space and nothing would say so.
    /// The database is therefore left alone and the state is logged, unless the
    /// operator has opted in.
    /// </summary>
    private void ReconcileAutoVacuum(bool vacuumOnStartup)
    {
        using var cmd = _conn.CreateCommand();
        if (ReadAutoVacuum(cmd) == AutoVacuumIncremental) return;

        if (!vacuumOnStartup)
        {
            _logger.LogWarning(
                "This database was created with auto_vacuum off, so pages freed by "
                + "rollup and retention are never returned to the filesystem and the "
                + "file keeps its high water mark. Set \"vacuumOnStartup\": true in "
                + "telltale.json to convert it on the next start. The conversion "
                + "rewrites the whole file and needs roughly twice its size in free "
                + "disk while it runs, so it is not done automatically.");
            return;
        }

        _logger.LogInformation(
            "Converting the database to incremental auto_vacuum. This rewrites the "
            + "whole file and may take some time.");

        try
        {
            cmd.CommandText = "PRAGMA auto_vacuum = INCREMENTAL;";
            cmd.ExecuteNonQuery();

            cmd.CommandText = "VACUUM;";
            cmd.ExecuteNonQuery();
        }
        catch (SqliteException ex)
        {
            // Carry on with the database unconverted. It behaves exactly as it did
            // before, it simply cannot hand freed pages back. Letting this reach the
            // host would end the process, and it would do so again on every start
            // until somebody noticed and edited telltale.json, which turns "space is
            // not reclaimed" into "nothing is recorded at all". Running out of disk
            // part way through a rewrite is a recoverable condition, not a reason to
            // stop capturing.
            _logger.LogError(ex,
                "Could not convert the database to incremental auto_vacuum, so it is "
                + "being used unconverted. The likeliest cause is too little free "
                + "disk, since the rewrite needs roughly twice the current file size "
                + "while it runs. Capture is unaffected apart from the file keeping "
                + "its high water mark.");
            return;
        }

        long after = ReadAutoVacuum(cmd);
        if (after == AutoVacuumIncremental)
        {
            // Separate from the conversion above, and deliberately after it has been
            // confirmed. VACUUM pushes the rewrite through the write ahead log, so
            // without this the transient space stays claimed until the first rollup
            // cycle checkpoints it five minutes later. Failing to reclaim it early is
            // not a failed conversion, so it must not be reported as one.
            try
            {
                cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                cmd.ExecuteNonQuery();
            }
            catch (SqliteException ex)
            {
                _logger.LogWarning(ex,
                    "Converted the database, but could not check the write ahead log "
                    + "back into it. The space will be reclaimed by the next rollup "
                    + "cycle instead.");
            }

            _logger.LogInformation("Database converted to incremental auto_vacuum.");
        }
        else
        {
            _logger.LogWarning(
                "Conversion to incremental auto_vacuum did not take effect; "
                + "auto_vacuum still reports {Mode}.", after);
        }
    }

    private static long ReadAutoVacuum(SqliteCommand cmd)
    {
        cmd.CommandText = "PRAGMA auto_vacuum;";
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
    }

    private const string CreateSchemaSql = """
        CREATE TABLE schema_version (
            version INTEGER PRIMARY KEY
        );
        INSERT INTO schema_version VALUES (4);

        CREATE TABLE process_instance (
            id           INTEGER PRIMARY KEY,
            pid          INTEGER NOT NULL,
            create_time  INTEGER NOT NULL,
            name         TEXT    NOT NULL,
            path         TEXT,
            command_line TEXT,
            first_seen   INTEGER NOT NULL,
            last_seen    INTEGER NOT NULL,
            UNIQUE(pid, create_time)
        );

        CREATE TABLE sample (
            ts           INTEGER NOT NULL,
            instance_id  INTEGER NOT NULL REFERENCES process_instance(id),
            cpu_pct      REAL,
            private_mb   REAL,
            working_set_mb REAL,
            io_kb        REAL,
            threads      INTEGER,
            handles      INTEGER
        );
        CREATE INDEX ix_sample_ts ON sample(ts);
        CREATE INDEX ix_sample_inst ON sample(instance_id, ts);

        CREATE TABLE sample_1m (
            ts           INTEGER NOT NULL,
            instance_id  INTEGER NOT NULL REFERENCES process_instance(id),
            cpu_pct_avg  REAL,
            cpu_pct_max  REAL,
            private_mb_max REAL,
            working_set_mb_max REAL,
            io_kb_total  REAL,
            sample_count INTEGER
        );
        -- (ts, instance_id) is the natural key: the rollup writes one row per bucket
        -- per process instance. Uniqueness is a named index rather than a table
        -- constraint so that an existing database can reach the same shape, which
        -- SQLite cannot do for a UNIQUE constraint without rebuilding the table.
        -- It also covers lookups by ts alone, so no separate ts index is needed.
        CREATE UNIQUE INDEX ux_s1m_ts_inst ON sample_1m(ts, instance_id);
        CREATE INDEX ix_s1m_inst ON sample_1m(instance_id, ts);

        CREATE TABLE sample_10m (
            ts           INTEGER NOT NULL,
            instance_id  INTEGER NOT NULL REFERENCES process_instance(id),
            cpu_pct_avg  REAL,
            cpu_pct_max  REAL,
            private_mb_max REAL,
            working_set_mb_max REAL,
            io_kb_total  REAL,
            sample_count INTEGER
        );
        CREATE UNIQUE INDEX ux_s10m_ts_inst ON sample_10m(ts, instance_id);
        CREATE INDEX ix_s10m_inst ON sample_10m(instance_id, ts);

        -- The machine the recording was made on. One row, rewritten whenever the
        -- collector starts, because a recording describes one machine.
        --
        -- logical_processors is here so the viewer can convert a per process CPU figure
        -- without asking the machine it happens to be running on. Every cpu_pct in
        -- sample and its rollups is a share of one core, so a process spread over four
        -- of them reads 400, while the machine gauge is a share of all of them and stops
        -- at 100. Converting between the two needs this number, and reading it live is
        -- wrong the moment a capture is opened somewhere else.
        CREATE TABLE machine_info (
            id                 INTEGER PRIMARY KEY CHECK (id = 1),
            logical_processors INTEGER NOT NULL
        );

        CREATE TABLE machine (
            ts              INTEGER PRIMARY KEY,
            cpu_pct         REAL,
            memory_avail_mb REAL,
            commit_mb       REAL,
            hard_faults     INTEGER,
            disk_read_ms    REAL,
            disk_write_ms   REAL,
            memory_total_mb REAL,
            disk_busy_pct   REAL,
            net_kbps        REAL,
            gpu_busy_pct    REAL
        );

        CREATE TABLE machine_1m (
            ts                  INTEGER PRIMARY KEY,
            cpu_pct_avg         REAL,
            cpu_pct_max         REAL,
            memory_avail_mb_avg REAL,
            memory_total_mb     REAL,
            commit_mb_max       REAL,
            hard_faults_total   INTEGER,
            disk_read_ms_avg    REAL,
            disk_write_ms_avg   REAL,
            disk_busy_pct_avg   REAL,
            disk_busy_pct_max   REAL,
            net_kbps_avg        REAL,
            gpu_busy_pct_avg    REAL,
            sample_count        INTEGER
        );

        CREATE TABLE machine_10m (
            ts                  INTEGER PRIMARY KEY,
            cpu_pct_avg         REAL,
            cpu_pct_max         REAL,
            memory_avail_mb_avg REAL,
            memory_total_mb     REAL,
            commit_mb_max       REAL,
            hard_faults_total   INTEGER,
            disk_read_ms_avg    REAL,
            disk_write_ms_avg   REAL,
            disk_busy_pct_avg   REAL,
            disk_busy_pct_max   REAL,
            net_kbps_avg        REAL,
            gpu_busy_pct_avg    REAL,
            sample_count        INTEGER
        );

        -- What the recorder cost the machine, one row per tick. cpu_pct is the
        -- collector's own CPU on the same scale as sample.cpu_pct, a share of one core
        -- rather than of the whole machine, and it is null when no rate could be
        -- measured, which is the case on the first tick of a run. Both figures cover the
        -- whole of Telltale.exe, which is the recorder plus the viewer window when it is
        -- open, because they are one process.
        CREATE TABLE collector_health (
            ts              INTEGER PRIMARY KEY,
            cpu_pct         REAL,
            private_mb      REAL,
            sample_cost_ms  REAL,
            process_count   INTEGER,
            stored_count    INTEGER
        );

        -- collector_health.sample_cost_ms is the whole tick as one number, which says
        -- that a tick ran long but not where the time went. This table breaks the same
        -- tick into the phases it is spent in, one row per tick, sharing the health
        -- row's timestamp. The phases do not overlap and together they account for the
        -- tick, so each column can be read as its share of the cost beside it:
        --
        --   sampler_ms         enumerating every running process
        --   machine_sample_ms  reading the machine wide performance counters
        --   identity_ms        finding the tick's distinct processes and resolving
        --                      the paths of any not seen before
        --   instance_ms        resolving a database row id for each process
        --   row_build_ms       working out each process's CPU and I/O since last tick
        --   sample_write_ms    writing the tick's sample rows, and forgetting the
        --                      processes that have since gone
        --   machine_write_ms   writing the tick's machine row
        --
        -- It is a separate table rather than more columns on collector_health because a
        -- migration can add a table whose definition is written out in full, and cannot
        -- add a column without SQLite rewriting the stored definition into a shape this
        -- file cannot reproduce.
        CREATE TABLE collector_tick_phase (
            ts                INTEGER PRIMARY KEY,
            sampler_ms        REAL,
            machine_sample_ms REAL,
            identity_ms       REAL,
            instance_ms       REAL,
            row_build_ms      REAL,
            sample_write_ms   REAL,
            machine_write_ms  REAL
        );
        """;

    /// <summary>
    /// Resolves one process instance row, creating it if it is new.
    ///
    /// The sampling loop does not call this: it resolves a whole tick at once
    /// through <see cref="UpsertProcessInstances"/>, because doing it a row at a
    /// time was what made a tick take tens of seconds. This is kept as the
    /// single-row statement of the same behaviour, and the batch path is tested
    /// against it, so a change to one that does not match the other fails. It has
    /// no production caller by design rather than by neglect.
    /// </summary>
    public long GetOrCreateProcessInstance(int pid, long createTime, string name, string? path,
        string? commandLine, long timestamp)
    {
        lock (_gate)
        {
            ThrowIfDisposed();

            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT id FROM process_instance WHERE pid = @pid AND create_time = @ct";
            cmd.Parameters.AddWithValue("@pid", pid);
            cmd.Parameters.AddWithValue("@ct", createTime);

            var result = cmd.ExecuteScalar();
            if (result != null)
            {
                var id = (long)result;
                using var update = _conn.CreateCommand();
                update.CommandText = "UPDATE process_instance SET last_seen = @ls WHERE id = @id";
                update.Parameters.AddWithValue("@ls", timestamp);
                update.Parameters.AddWithValue("@id", id);
                update.ExecuteNonQuery();
                return id;
            }

            using var insert = _conn.CreateCommand();
            insert.CommandText = """
                INSERT INTO process_instance (pid, create_time, name, path, command_line, first_seen, last_seen)
                VALUES (@pid, @ct, @name, @path, @cmd, @fs, @ls)
                """;
            insert.Parameters.AddWithValue("@pid", pid);
            insert.Parameters.AddWithValue("@ct", createTime);
            insert.Parameters.AddWithValue("@name", name);
            insert.Parameters.AddWithValue("@path", (object?)path ?? DBNull.Value);
            insert.Parameters.AddWithValue("@cmd", (object?)commandLine ?? DBNull.Value);
            insert.Parameters.AddWithValue("@fs", timestamp);
            insert.Parameters.AddWithValue("@ls", timestamp);
            insert.ExecuteNonQuery();

            return GetLastInsertRowIdLocked();
        }
    }

    /// <summary>
    /// Resolves every process instance seen in one tick, creating the rows that
    /// are new and moving <c>last_seen</c> on the rows that are not, and returns
    /// the row id for each.
    ///
    /// This is <see cref="GetOrCreateProcessInstance"/> for a whole tick at once,
    /// and it exists because calling that method per process was the reason ticks
    /// took tens of seconds. Every statement it runs is its own implicit
    /// transaction, so a tick covering some 670 processes asked SQLite to commit
    /// well over a thousand times, each one a write to the log and a wait on the
    /// gate. Here the same work is one lock and one commit.
    /// </summary>
    public Dictionary<(int Pid, long CreateTime), long> UpsertProcessInstances(
        IReadOnlyCollection<ProcessInstanceUpsert> instances, long timestamp)
    {
        var ids = new Dictionary<(int Pid, long CreateTime), long>(instances.Count);
        if (instances.Count == 0) return ids;

        lock (_gate)
        {
            ThrowIfDisposed();

            using var tx = _conn.BeginTransaction();

            // Three commands prepared once and rebound per process, rather than a
            // command built per process. The parameter objects are held so the
            // loop assigns values instead of rebuilding a collection each time.
            using var select = _conn.CreateCommand();
            select.Transaction = tx;
            select.CommandText = "SELECT id FROM process_instance WHERE pid = @pid AND create_time = @ct";
            var sPid = select.Parameters.Add("@pid", SqliteType.Integer);
            var sCt = select.Parameters.Add("@ct", SqliteType.Integer);

            using var update = _conn.CreateCommand();
            update.Transaction = tx;
            update.CommandText = "UPDATE process_instance SET last_seen = @ls WHERE id = @id";
            var uLs = update.Parameters.Add("@ls", SqliteType.Integer);
            var uId = update.Parameters.Add("@id", SqliteType.Integer);

            using var insert = _conn.CreateCommand();
            insert.Transaction = tx;
            insert.CommandText = """
                INSERT INTO process_instance (pid, create_time, name, path, command_line, first_seen, last_seen)
                VALUES (@pid, @ct, @name, @path, @cmd, @fs, @ls)
                RETURNING id
                """;
            var iPid = insert.Parameters.Add("@pid", SqliteType.Integer);
            var iCt = insert.Parameters.Add("@ct", SqliteType.Integer);
            var iName = insert.Parameters.Add("@name", SqliteType.Text);
            var iPath = insert.Parameters.Add("@path", SqliteType.Text);
            var iCmd = insert.Parameters.Add("@cmd", SqliteType.Text);
            var iFs = insert.Parameters.Add("@fs", SqliteType.Integer);
            var iLs = insert.Parameters.Add("@ls", SqliteType.Integer);

            foreach (var instance in instances)
            {
                var key = (instance.Pid, instance.CreateTime);
                if (ids.ContainsKey(key)) continue;

                sPid.Value = instance.Pid;
                sCt.Value = instance.CreateTime;

                var existing = select.ExecuteScalar();
                if (existing is not null)
                {
                    long id = (long)existing;
                    uLs.Value = timestamp;
                    uId.Value = id;
                    update.ExecuteNonQuery();
                    ids[key] = id;
                    continue;
                }

                iPid.Value = instance.Pid;
                iCt.Value = instance.CreateTime;
                iName.Value = instance.Name;
                iPath.Value = (object?)instance.Path ?? DBNull.Value;
                iCmd.Value = (object?)instance.CommandLine ?? DBNull.Value;
                iFs.Value = timestamp;
                iLs.Value = timestamp;

                // RETURNING rather than last_insert_rowid(), so the id belongs to
                // this statement and not to whatever ran most recently on the
                // connection.
                ids[key] = (long)insert.ExecuteScalar()!;
            }

            tx.Commit();
        }

        return ids;
    }

    public void WriteSampleBatch(long timestamp, List<SampleRow> rows)
    {
        lock (_gate)
        {
            ThrowIfDisposed();

            using var tx = _conn.BeginTransaction();

            using var cmd = _conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO sample (ts, instance_id, cpu_pct, private_mb, working_set_mb, io_kb, threads, handles)
                VALUES (@ts, @iid, @cpu, @pm, @ws, @io, @th, @ha)
                """;

            var pTs = cmd.Parameters.Add("@ts", SqliteType.Integer);
            var pIid = cmd.Parameters.Add("@iid", SqliteType.Integer);
            var pCpu = cmd.Parameters.Add("@cpu", SqliteType.Real);
            var pPm = cmd.Parameters.Add("@pm", SqliteType.Real);
            var pWs = cmd.Parameters.Add("@ws", SqliteType.Real);
            var pIo = cmd.Parameters.Add("@io", SqliteType.Real);
            var pTh = cmd.Parameters.Add("@th", SqliteType.Integer);
            var pHa = cmd.Parameters.Add("@ha", SqliteType.Integer);

            foreach (var row in rows)
            {
                pTs.Value = timestamp;
                pIid.Value = row.InstanceId;
                pCpu.Value = row.CpuPct.HasValue ? row.CpuPct.Value : DBNull.Value;
                pPm.Value = row.PrivateMb;
                pWs.Value = row.WorkingSetMb;
                pIo.Value = row.IoKb.HasValue ? row.IoKb.Value : DBNull.Value;
                pTh.Value = row.Threads;
                pHa.Value = row.Handles;
                cmd.ExecuteNonQuery();
            }

            tx.Commit();
        }
    }

    public void WriteMachineSample(long timestamp, MachineSample sample)
    {
        lock (_gate)
        {
            ThrowIfDisposed();

            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                INSERT OR REPLACE INTO machine
                    (ts, cpu_pct, memory_avail_mb, commit_mb, hard_faults, disk_read_ms, disk_write_ms,
                     memory_total_mb, disk_busy_pct, net_kbps, gpu_busy_pct)
                VALUES (@ts, @cpu, @mem, @com, @hf, @dr, @dw, @mt, @db, @net, @gpu)
                """;
            cmd.Parameters.AddWithValue("@ts", timestamp);
            cmd.Parameters.AddWithValue("@cpu", (object?)sample.CpuPct ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@mem", (object?)sample.MemoryAvailMb ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@com", (object?)sample.CommitMb ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@hf", (object?)sample.HardFaults ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@dr", (object?)sample.DiskReadMs ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@dw", (object?)sample.DiskWriteMs ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@mt", (object?)sample.MemoryTotalMb ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@db", (object?)sample.DiskBusyPct ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@net", (object?)sample.NetKbps ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@gpu", (object?)sample.GpuBusyPct ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
    }

    /// <param name="cpuPct">
    /// The collector's own CPU as a share of one core, or null when no rate could
    /// be measured, which is the case on the first tick of a run. Null is written
    /// as null: this column read a hardcoded zero for its whole life before, and a
    /// zero nobody measured is the thing that made it useless.
    /// </param>
    public void WriteCollectorHealth(long timestamp, double? cpuPct, double privateMb,
        double sampleCostMs, int processCount, int storedCount)
    {
        lock (_gate)
        {
            ThrowIfDisposed();

            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                INSERT OR REPLACE INTO collector_health (ts, cpu_pct, private_mb, sample_cost_ms, process_count, stored_count)
                VALUES (@ts, @cpu, @pm, @cost, @pc, @sc)
                """;
            cmd.Parameters.AddWithValue("@ts", timestamp);
            cmd.Parameters.AddWithValue("@cpu", (object?)cpuPct ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@pm", privateMb);
            cmd.Parameters.AddWithValue("@cost", sampleCostMs);
            cmd.Parameters.AddWithValue("@pc", processCount);
            cmd.Parameters.AddWithValue("@sc", storedCount);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Records the machine the capture is being made on. Called once at startup,
    /// and it replaces whatever was there, because the row describes the machine
    /// rather than a moment in the recording.
    ///
    /// A capture that spans a change to the machine, a virtual machine given more
    /// cores between runs, ends up describing the machine as it was on the last
    /// start. Recording the count against every sample would be exact and would
    /// cost a column on the largest table in the database to answer a question
    /// almost nobody has.
    /// </summary>
    public void WriteMachineInfo(int logicalProcessors)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(logicalProcessors, 1);

        lock (_gate)
        {
            ThrowIfDisposed();

            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                INSERT OR REPLACE INTO machine_info (id, logical_processors)
                VALUES (1, @count)
                """;
            cmd.Parameters.AddWithValue("@count", logicalProcessors);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Records where one sampling tick spent its time, against the same timestamp
    /// as the health row for that tick.
    /// </summary>
    public void WriteTickPhases(long timestamp, TickPhaseTimings phases)
    {
        lock (_gate)
        {
            ThrowIfDisposed();

            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                INSERT OR REPLACE INTO collector_tick_phase
                    (ts, sampler_ms, machine_sample_ms, identity_ms, instance_ms,
                     row_build_ms, sample_write_ms, machine_write_ms)
                VALUES (@ts, @sampler, @machineSample, @identity, @instance,
                        @rowBuild, @sampleWrite, @machineWrite)
                """;
            cmd.Parameters.AddWithValue("@ts", timestamp);
            cmd.Parameters.AddWithValue("@sampler", phases.SamplerMs);
            cmd.Parameters.AddWithValue("@machineSample", phases.MachineSampleMs);
            cmd.Parameters.AddWithValue("@identity", phases.IdentityMs);
            cmd.Parameters.AddWithValue("@instance", phases.InstanceMs);
            cmd.Parameters.AddWithValue("@rowBuild", phases.RowBuildMs);
            cmd.Parameters.AddWithValue("@sampleWrite", phases.SampleWriteMs);
            cmd.Parameters.AddWithValue("@machineWrite", phases.MachineWriteMs);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Promotes rows older than <paramref name="cutoffMs"/> from a raw or finer
    /// grained table into coarser buckets, then removes the rows it promoted.
    /// The cutoff is rounded down to a bucket boundary, and any bucket the target
    /// already holds is skipped, so running this repeatedly is safe: a bucket is
    /// never produced twice and never counted twice.
    /// </summary>
    public void RollupSamples(long cutoffMs, string sourceTable, string targetTable,
        int bucketMinutes, bool isMachine)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(bucketMinutes, 1);

        lock (_gate)
        {
            ThrowIfDisposed();

            using var tx = _conn.BeginTransaction();
            using var cmd = _conn.CreateCommand();
            cmd.Transaction = tx;

            long bucketMs = bucketMinutes * 60_000L;

            // Only ever promote whole buckets. The caller's cutoff is a wall clock
            // instant and rarely lands on a bucket boundary, so without this the bucket
            // containing the cutoff would be promoted while part of it is still newer
            // than the cutoff, and its leftover rows would be promoted a second time on
            // the next cycle under the same bucket timestamp.
            long alignedCutoff = FloorToBucket(cutoffMs, bucketMs);

            bool isReRollup = sourceTable.Contains("_1m") || sourceTable.Contains("_10m");

            // The machine tables carry memory_total_mb, a value that describes the
            // machine rather than the interval, so it is taken from the last row in
            // each bucket instead of being averaged. That lookup used to be a
            // correlated subquery, which SQLite answered with one full scan of the
            // source table per bucket: invisible when a cycle promotes one or two
            // buckets, and minutes of held write lock when it promotes a backlog.
            // The last_total CTE below computes the same value for every bucket in a
            // fixed number of scans instead.
            //
            // Restricting the CTE to rows older than the cutoff is safe even though
            // the original subquery had no such filter: the cutoff is already rounded
            // down to a bucket boundary, so no promoted bucket straddles it and the
            // row that wins is the same one either way.

            if (isMachine && !isReRollup)
            {
                cmd.CommandText = $"""
                    WITH last_total AS (
                        SELECT bucket_ts, memory_total_mb
                        FROM (
                            SELECT (ts / @bucket) * @bucket AS bucket_ts, memory_total_mb,
                                   ROW_NUMBER() OVER (
                                       PARTITION BY ts / @bucket ORDER BY ts DESC) AS rn
                            FROM {sourceTable}
                            WHERE ts < @cutoff)
                        WHERE rn = 1)
                    INSERT INTO {targetTable}
                        (ts, cpu_pct_avg, cpu_pct_max, memory_avail_mb_avg, memory_total_mb,
                         commit_mb_max, hard_faults_total, disk_read_ms_avg, disk_write_ms_avg,
                         disk_busy_pct_avg, disk_busy_pct_max, net_kbps_avg, gpu_busy_pct_avg, sample_count)
                    SELECT (s.ts / @bucket) * @bucket,
                           AVG(s.cpu_pct), MAX(s.cpu_pct), AVG(s.memory_avail_mb),
                           MAX(lt.memory_total_mb),
                           MAX(s.commit_mb), SUM(s.hard_faults),
                           AVG(s.disk_read_ms), AVG(s.disk_write_ms),
                           AVG(s.disk_busy_pct), MAX(s.disk_busy_pct),
                           AVG(s.net_kbps), AVG(s.gpu_busy_pct), COUNT(*)
                    FROM {sourceTable} s
                    LEFT JOIN last_total lt ON lt.bucket_ts = (s.ts / @bucket) * @bucket
                    WHERE s.ts < @cutoff
                      AND NOT EXISTS (
                          SELECT 1 FROM {targetTable} t
                          WHERE t.ts = (s.ts / @bucket) * @bucket)
                    GROUP BY s.ts / @bucket
                    """;
            }
            else if (isMachine && isReRollup)
            {
                cmd.CommandText = $"""
                    WITH last_total AS (
                        SELECT bucket_ts, memory_total_mb
                        FROM (
                            SELECT (ts / @bucket) * @bucket AS bucket_ts, memory_total_mb,
                                   ROW_NUMBER() OVER (
                                       PARTITION BY ts / @bucket ORDER BY ts DESC) AS rn
                            FROM {sourceTable}
                            WHERE ts < @cutoff)
                        WHERE rn = 1)
                    INSERT INTO {targetTable}
                        (ts, cpu_pct_avg, cpu_pct_max, memory_avail_mb_avg, memory_total_mb,
                         commit_mb_max, hard_faults_total, disk_read_ms_avg, disk_write_ms_avg,
                         disk_busy_pct_avg, disk_busy_pct_max, net_kbps_avg, gpu_busy_pct_avg, sample_count)
                    SELECT (s.ts / @bucket) * @bucket,
                           {WeightedAvg("s.cpu_pct_avg", "s.sample_count")},
                           MAX(s.cpu_pct_max),
                           {WeightedAvg("s.memory_avail_mb_avg", "s.sample_count")},
                           MAX(lt.memory_total_mb),
                           MAX(s.commit_mb_max), SUM(s.hard_faults_total),
                           {WeightedAvg("s.disk_read_ms_avg", "s.sample_count")},
                           {WeightedAvg("s.disk_write_ms_avg", "s.sample_count")},
                           {WeightedAvg("s.disk_busy_pct_avg", "s.sample_count")},
                           MAX(s.disk_busy_pct_max),
                           {WeightedAvg("s.net_kbps_avg", "s.sample_count")},
                           {WeightedAvg("s.gpu_busy_pct_avg", "s.sample_count")},
                           SUM(s.sample_count)
                    FROM {sourceTable} s
                    LEFT JOIN last_total lt ON lt.bucket_ts = (s.ts / @bucket) * @bucket
                    WHERE s.ts < @cutoff
                      AND NOT EXISTS (
                          SELECT 1 FROM {targetTable} t
                          WHERE t.ts = (s.ts / @bucket) * @bucket)
                    GROUP BY s.ts / @bucket
                    """;
            }
            else if (!isMachine && !isReRollup)
            {
                cmd.CommandText = $"""
                    INSERT INTO {targetTable}
                        (ts, instance_id, cpu_pct_avg, cpu_pct_max, private_mb_max,
                         working_set_mb_max, io_kb_total, sample_count)
                    SELECT (ts / @bucket) * @bucket, instance_id,
                           AVG(cpu_pct), MAX(cpu_pct), MAX(private_mb),
                           MAX(working_set_mb), SUM(io_kb), COUNT(*)
                    FROM {sourceTable}
                    WHERE ts < @cutoff
                      AND NOT EXISTS (
                          SELECT 1 FROM {targetTable} t
                          WHERE t.ts = ({sourceTable}.ts / @bucket) * @bucket
                            AND t.instance_id = {sourceTable}.instance_id)
                    GROUP BY ts / @bucket, instance_id
                    """;
            }
            else
            {
                cmd.CommandText = $"""
                    INSERT INTO {targetTable}
                        (ts, instance_id, cpu_pct_avg, cpu_pct_max, private_mb_max,
                         working_set_mb_max, io_kb_total, sample_count)
                    SELECT (ts / @bucket) * @bucket, instance_id,
                           {WeightedAvg("cpu_pct_avg", "sample_count")},
                           MAX(cpu_pct_max), MAX(private_mb_max),
                           MAX(working_set_mb_max), SUM(io_kb_total), SUM(sample_count)
                    FROM {sourceTable}
                    WHERE ts < @cutoff
                      AND NOT EXISTS (
                          SELECT 1 FROM {targetTable} t
                          WHERE t.ts = ({sourceTable}.ts / @bucket) * @bucket
                            AND t.instance_id = {sourceTable}.instance_id)
                    GROUP BY ts / @bucket, instance_id
                    """;
            }

            cmd.Parameters.AddWithValue("@bucket", bucketMs);
            cmd.Parameters.AddWithValue("@cutoff", alignedCutoff);
            cmd.ExecuteNonQuery();

            // Everything older than the aligned cutoff goes, including rows the insert
            // skipped because the target already held their bucket. Keeping them would
            // grow the raw table without bound and retry the same conflict on every
            // later cycle. Those rows are discarded rather than merged, so the cost is
            // whatever landed in an already promoted bucket: normally nothing, one
            // bucket on the first cycle after a database was left stuck by the old
            // behaviour, and as much as a backwards clock jump spans if one occurs.
            cmd.CommandText = $"DELETE FROM {sourceTable} WHERE ts < @cutoff";
            cmd.Parameters.Clear();
            cmd.Parameters.AddWithValue("@cutoff", alignedCutoff);
            cmd.ExecuteNonQuery();

            tx.Commit();
        }
    }

    /// <summary>
    /// Builds the weighted average of an already-rolled-up column, using each
    /// source row's sample_count as its weight.
    ///
    /// A row whose average is NULL carries samples that were never measured, so
    /// its weight is excluded from the divisor as well as the dividend. Summing
    /// the weight on only one side of the division charges an unmeasured sample
    /// against a value that was never taken, which biases the result low, and
    /// the result is stored rather than recomputed per query, so the bias is
    /// permanent. A bucket in which nothing was measured divides by NULL and
    /// stays NULL rather than collapsing to zero.
    /// </summary>
    private static string WeightedAvg(string column, string weight) =>
        $"SUM({column} * {weight}) / " +
        $"NULLIF(SUM(CASE WHEN {column} IS NULL THEN 0 ELSE {weight} END), 0)";

    /// <summary>
    /// Rounds a timestamp down to the start of the bucket containing it. C# integer
    /// division truncates toward zero rather than flooring, which would round a
    /// negative timestamp up and let an incomplete bucket through, so negatives are
    /// floored explicitly.
    /// </summary>
    public static long FloorToBucket(long timestampMs, long bucketMs)
    {
        long buckets = timestampMs / bucketMs;
        if (timestampMs < 0 && buckets * bucketMs != timestampMs)
            buckets--;

        return buckets * bucketMs;
    }

    public void DeleteOldData(string table, long cutoffMs)
    {
        lock (_gate)
        {
            ThrowIfDisposed();

            using var cmd = _conn.CreateCommand();
            cmd.CommandText = $"DELETE FROM {table} WHERE ts < @cutoff";
            cmd.Parameters.AddWithValue("@cutoff", cutoffMs);
            cmd.ExecuteNonQuery();
        }
    }

    public void DeleteOrphanedProcessInstances()
    {
        lock (_gate)
        {
            ThrowIfDisposed();

            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                DELETE FROM process_instance
                WHERE id NOT IN (SELECT DISTINCT instance_id FROM sample)
                  AND id NOT IN (SELECT DISTINCT instance_id FROM sample_1m)
                  AND id NOT IN (SELECT DISTINCT instance_id FROM sample_10m)
                """;
            cmd.ExecuteNonQuery();
        }
    }

    public void IncrementalVacuum()
    {
        lock (_gate)
        {
            ThrowIfDisposed();

            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "PRAGMA incremental_vacuum;";
            cmd.ExecuteNonQuery();
        }
    }

    public void WalCheckpoint()
    {
        lock (_gate)
        {
            ThrowIfDisposed();

            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "PRAGMA wal_checkpoint(PASSIVE);";
            cmd.ExecuteNonQuery();
        }
    }

    public long GetDatabaseSizeBytes()
    {
        lock (_gate)
        {
            ThrowIfDisposed();

            return GetDatabaseSizeBytesLocked();
        }
    }

    /// <summary>
    /// Drops whole days off the coarsest tiers until the file is back under the cap.
    /// The size checks and the deletes run under one acquisition, so a concurrent
    /// write cannot change the answer between a check and the delete it justified.
    /// </summary>
    public void EnforceSizeLimit(long maxBytes)
    {
        lock (_gate)
        {
            ThrowIfDisposed();

            if (GetDatabaseSizeBytesLocked() <= maxBytes) return;

            DeleteOldestRollupDataLocked("sample_10m");
            DeleteOldestRollupDataLocked("machine_10m");

            if (GetDatabaseSizeBytesLocked() <= maxBytes) return;

            DeleteOldestRollupDataLocked("sample_1m");
            DeleteOldestRollupDataLocked("machine_1m");
        }
    }

    private long GetDatabaseSizeBytesLocked()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT page_count * page_size FROM pragma_page_count(), pragma_page_size();";
        return (long)(cmd.ExecuteScalar() ?? 0L);
    }

    private void DeleteOldestRollupDataLocked(string table)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = $"""
            DELETE FROM {table}
            WHERE ts <= (SELECT MIN(ts) + 86400000 FROM {table})
            """;
        cmd.ExecuteNonQuery();
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(_disposed, this);

    private long GetLastInsertRowIdLocked()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT last_insert_rowid()";
        return (long)cmd.ExecuteScalar()!;
    }

    public void Dispose()
    {
        // Under the gate so the connection cannot be torn down mid statement, and
        // idempotent: unlike every other method this one must not object to being
        // called after disposal.
        lock (_gate)
        {
            if (_disposed) return;

            _disposed = true;
            _conn.Dispose();
        }
    }
}

/// <summary>
/// One process instance as a tick saw it, handed to
/// <see cref="Database.UpsertProcessInstances"/> so the whole tick resolves in a
/// single transaction.
/// </summary>
public sealed record ProcessInstanceUpsert(
    int Pid, long CreateTime, string Name, string? Path, string? CommandLine);

/// <summary>
/// Where one sampling tick spent its time. Each figure covers one phase of
/// <c>CollectorWorker.SampleTick</c>, and together they account for the tick cost
/// recorded alongside them in <c>collector_health</c>.
/// </summary>
public sealed record TickPhaseTimings(
    double SamplerMs,
    double MachineSampleMs,
    double IdentityMs,
    double InstanceMs,
    double RowBuildMs,
    double SampleWriteMs,
    double MachineWriteMs);

public record SampleRow(
    long InstanceId,
    double? CpuPct,
    double PrivateMb,
    double WorkingSetMb,
    double? IoKb,
    int Threads,
    int Handles);

public record MachineSample(
    double? CpuPct,
    double? MemoryAvailMb,
    double? CommitMb,
    int? HardFaults,
    double? DiskReadMs,
    double? DiskWriteMs,
    double? MemoryTotalMb,
    double? DiskBusyPct,
    double? NetKbps,
    double? GpuBusyPct);
