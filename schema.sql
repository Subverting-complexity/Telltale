-- auto_vacuum has to be chosen before anything writes the database header,
-- and switching the journal mode to WAL writes it. Reversed, the auto_vacuum
-- statement succeeds but changes nothing and the file never releases freed
-- pages. collector/Database.cs applies the same order.
PRAGMA auto_vacuum = INCREMENTAL;
PRAGMA journal_mode = WAL;
PRAGMA synchronous = NORMAL;

CREATE TABLE schema_version (
    version INTEGER PRIMARY KEY
);
INSERT INTO schema_version VALUES (3);

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

-- collector_health.sample_cost_ms is the whole tick as one number, which says
-- that a tick ran long but not where the time went. This table breaks the same
-- tick into the phases it is spent in, one row per tick, sharing the health
-- row's timestamp. It is a separate table rather than more columns on
-- collector_health because a migration can add a table whose definition is
-- written out in full, and cannot add a column without SQLite rewriting the
-- stored definition into a shape this file cannot reproduce.
CREATE TABLE collector_tick_phase (
    ts                INTEGER PRIMARY KEY,
    sampler_ms        REAL,
    machine_sample_ms REAL,
    identity_ms       REAL,
    instance_ms       REAL,
    sample_write_ms   REAL,
    machine_write_ms  REAL
);
