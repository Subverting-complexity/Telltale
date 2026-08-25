-- Frozen snapshot of schema.sql as it shipped at version 1.
--
-- Every database created before the migration path existed still has this
-- shape, so this file is the fixture the migration tests start from. It must
-- not be updated when schema.sql changes: doing so would make the tests
-- migrate from a database shape that never existed.

PRAGMA journal_mode = WAL;
PRAGMA synchronous = NORMAL;
PRAGMA auto_vacuum = INCREMENTAL;

CREATE TABLE schema_version (
    version INTEGER PRIMARY KEY
);
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
