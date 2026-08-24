using Microsoft.Data.Sqlite;

namespace Telltale.Collector;

public sealed class Database : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly ILogger _logger;

    public Database(string dbPath, ILogger logger)
    {
        _logger = logger;
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        _conn = new SqliteConnection($"Data Source={dbPath}");
        _conn.Open();
        InitSchema();
    }

    private void InitSchema()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode = WAL;";
        cmd.ExecuteNonQuery();

        cmd.CommandText = "PRAGMA synchronous = NORMAL;";
        cmd.ExecuteNonQuery();

        cmd.CommandText = "PRAGMA auto_vacuum = INCREMENTAL;";
        cmd.ExecuteNonQuery();

        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='schema_version'";
        var exists = cmd.ExecuteScalar();
        if (exists != null) return;

        cmd.CommandText = """
            CREATE TABLE schema_version (version INTEGER PRIMARY KEY);
            INSERT INTO schema_version VALUES (1);

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
            CREATE INDEX ix_s1m_ts ON sample_1m(ts);
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
            CREATE INDEX ix_s10m_ts ON sample_10m(ts);
            CREATE INDEX ix_s10m_inst ON sample_10m(instance_id, ts);

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

            CREATE TABLE collector_health (
                ts              INTEGER PRIMARY KEY,
                cpu_pct         REAL,
                private_mb      REAL,
                sample_cost_ms  REAL,
                process_count   INTEGER,
                stored_count    INTEGER
            );
            """;
        cmd.ExecuteNonQuery();

        _logger.LogInformation("Database schema created (version 1).");
    }

    public long GetOrCreateProcessInstance(int pid, long createTime, string name, string? path,
        string? commandLine, long timestamp)
    {
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

        return GetLastInsertRowId();
    }

    public void WriteSampleBatch(long timestamp, List<SampleRow> rows)
    {
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

    public void WriteMachineSample(long timestamp, MachineSample sample)
    {
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

    public void WriteCollectorHealth(long timestamp, double cpuPct, double privateMb,
        double sampleCostMs, int processCount, int storedCount)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO collector_health (ts, cpu_pct, private_mb, sample_cost_ms, process_count, stored_count)
            VALUES (@ts, @cpu, @pm, @cost, @pc, @sc)
            """;
        cmd.Parameters.AddWithValue("@ts", timestamp);
        cmd.Parameters.AddWithValue("@cpu", cpuPct);
        cmd.Parameters.AddWithValue("@pm", privateMb);
        cmd.Parameters.AddWithValue("@cost", sampleCostMs);
        cmd.Parameters.AddWithValue("@pc", processCount);
        cmd.Parameters.AddWithValue("@sc", storedCount);
        cmd.ExecuteNonQuery();
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
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = $"DELETE FROM {table} WHERE ts < @cutoff";
        cmd.Parameters.AddWithValue("@cutoff", cutoffMs);
        cmd.ExecuteNonQuery();
    }

    public void DeleteOrphanedProcessInstances()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            DELETE FROM process_instance
            WHERE id NOT IN (SELECT DISTINCT instance_id FROM sample)
              AND id NOT IN (SELECT DISTINCT instance_id FROM sample_1m)
              AND id NOT IN (SELECT DISTINCT instance_id FROM sample_10m)
            """;
        cmd.ExecuteNonQuery();
    }

    public void IncrementalVacuum()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "PRAGMA incremental_vacuum;";
        cmd.ExecuteNonQuery();
    }

    public void WalCheckpoint()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "PRAGMA wal_checkpoint(PASSIVE);";
        cmd.ExecuteNonQuery();
    }

    public long GetDatabaseSizeBytes()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT page_count * page_size FROM pragma_page_count(), pragma_page_size();";
        return (long)(cmd.ExecuteScalar() ?? 0L);
    }

    public void EnforceSizeLimit(long maxBytes)
    {
        if (GetDatabaseSizeBytes() <= maxBytes) return;

        DeleteOldestRollupData("sample_10m");
        DeleteOldestRollupData("machine_10m");

        if (GetDatabaseSizeBytes() <= maxBytes) return;

        DeleteOldestRollupData("sample_1m");
        DeleteOldestRollupData("machine_1m");
    }

    private void DeleteOldestRollupData(string table)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = $"""
            DELETE FROM {table}
            WHERE ts <= (SELECT MIN(ts) + 86400000 FROM {table})
            """;
        cmd.ExecuteNonQuery();
    }

    private long GetLastInsertRowId()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT last_insert_rowid()";
        return (long)cmd.ExecuteScalar()!;
    }

    public void Dispose()
    {
        _conn.Dispose();
    }
}

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
