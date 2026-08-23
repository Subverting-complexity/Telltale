# Telltale v1 Implementation Plan

Prepared 23 August 2026, before any code exists. Complements the design brief of the same date. Everything here is a plan, not a commitment.

## Settled decisions

These came out of the initial planning review and are reflected throughout the document.

- **Command lines**: recorded by default, with a config toggle to disable. Basic redaction of common secret patterns (tokens, passwords, API keys) applied before storage.
- **Console app, not a service**: the collector is a plain executable. Run it from a terminal, add it to Task Scheduler or shell:startup for auto-start. No Windows Service complexity in v1. The code uses .NET's BackgroundService pattern internally, so adding service hosting later is a one-line change.
- **Database path**: `%LocalAppData%\Telltale\telltale.db` by default.
- **Stack**: .NET 10 for both collector and viewer. The collector's CPU cost is dominated by the native API call (7.2 ms), not by .NET overhead. A leaner runtime (Rust, Go) would save ~25 MB of memory, which is not noticeable on 64 GB. The development speed advantage of staying in one ecosystem is worth more than that saving for v1. If the collector ever needs to run on constrained hardware, it can be rewritten independently because it shares no code with the viewer.
- **Default interval**: 5 seconds. Two-second sampling is supported via config but increases raw storage by 2.5x. At 5 seconds the collector is working for 0.14% of each interval (7.2 ms out of 5,000 ms). The per-cycle cost is independent of the interval; only the frequency changes.
- **Raw retention**: 24 hours. This is the window of full-resolution data available for fine-grained drill-down. After 24 hours, data exists only in rolled-up form. Configurable via telltale.json.
- **Two-tier rollup**: 1-minute buckets retained for 7 days, 10-minute buckets retained for 12 months. Without the second tier, 1-minute process data for 12 months would exceed 1 GB on its own. The second tier adds one extra table per data type and one more pass in the rollup worker, but keeps the database under 500 MB while preserving a full year of history at a resolution that is still useful for trend analysis.
- **auto_vacuum**: incremental mode, set at database creation time (`PRAGMA auto_vacuum = INCREMENTAL`). The collector calls `PRAGMA incremental_vacuum` after each rollup cycle to reclaim freed pages. This avoids the full-database copy that VACUUM requires, at the cost of slightly less compaction. Switching from incremental to full VACUUM later is a one-line change; going the other direction requires recreating the database.
- **Collector single-instance**: global mutex, same pattern as the viewer. Prevents a second collector instance (e.g., a Task Scheduler job overlapping a manual start) from writing to the same database concurrently. Two concurrent writers would produce locking errors or corrupt data even with WAL.
- **Viewer single-instance**: global mutex, same pattern as FolderFileSizeScanner. Starting a second viewer instance activates the existing window instead of binding to the same port and failing.
- **Viewer binding**: 127.0.0.1 only. The viewer is a local tool; binding to all interfaces would expose process data to the network without authentication.

## Stack

| Component | Technology | Why |
|-----------|-----------|-----|
| Collector | .NET 10 console app (BackgroundService pattern) | Lightweight host, same ecosystem as viewer, easy to debug |
| Native sampling | P/Invoke into ntdll.dll | No separate native binary, no IPC overhead, single deployment |
| Fallback sampling | System.Diagnostics.Process | Documented API, automatic fallback if native layout changes |
| Database | SQLite via Microsoft.Data.Sqlite | Official binding, small footprint, WAL mode for concurrent read/write |
| Viewer backend | .NET 10 minimal API | Same pattern as FolderFileSizeScanner, serves API + static SPA |
| Viewer frontend | React 19 + TypeScript + Vite 6 | Known stack, fast dev cycle, builds into backend's wwwroot |
| Charts | uPlot (~35 KB) | Purpose-built for time series, handles millions of points, no dependencies |

Total external NuGet packages: one (Microsoft.Data.Sqlite). Total frontend runtime dependencies: three (react, react-dom, uplot).

### Why uPlot for charts

The data volume rules out SVG-based charting. A full day at 5-second intervals is 17,280 machine-wide data points, plus potentially hundreds of per-process series. uPlot is canvas-based and handles this natively. The accessibility gap (canvas is opaque to screen readers) is closed by TT-A-01, which requires a companion data table for every chart. The tables carry the accessibility; the charts are visual aids.

uPlot has no official React wrapper. The integration requires manual lifecycle management via useRef and useEffect: creating and destroying uPlot instances, handling resize, and updating data without full re-renders. This is straightforward but adds friction compared to a React-native charting library, and accounts for roughly half a day of the Phase 5 estimate.

If uPlot proves inadequate during prototyping (poor zoom interaction, bad resize behaviour), the fallback is Recharts. This trades performance for SVG and a more React-native API, and would require downsampling for longer time ranges.

## Architecture

```
 [Collector.exe]  ---writes--->  [telltale.db]  <---reads---  [Viewer.exe]
  (runs in background)           (SQLite, WAL)              (open when needed)
        |                               |                        |
   NativeSampler                  process_instance           Minimal API
   MachineSampler                 sample (raw, 24h)          React SPA
   RollupWorker                   sample_1m (7 days)         uPlot charts
                                  sample_10m (12 months)     Data tables
                                  machine (raw, 24h)
                                  machine_1m (7 days)
                                  machine_10m (12 months)
                                  collector_health (7 days)
```

Two separate executables. They share nothing except the database file.

- **Collector.exe**: no web framework, no HTTP listener, no UI. Run it and it samples in the background. Close the window or Ctrl+C to stop. Add to Task Scheduler for auto-start at login.
- **Viewer.exe**: run it when you want to look at data. Opens a browser. Close it when you're done.

This means updating the viewer never risks the collector, the collector's memory stays minimal (~30 MB, no ASP.NET Core loaded), and the viewer can crash or never start without affecting data collection.

### SQLite configuration

- `journal_mode = WAL` allows the viewer to read while the collector writes, with no locking.
- `synchronous = NORMAL` is safe with WAL and avoids an fsync on every commit.
- `auto_vacuum = INCREMENTAL` set at database creation. Freed pages are reclaimable without a full VACUUM.
- One transaction per sample tick (all process rows for one 5-second window committed together).
- At ~89 rows per tick, write throughput is ~18 rows/second, which is trivial for SQLite.
- **WAL checkpointing**: the collector calls `PRAGMA wal_checkpoint(PASSIVE)` after each rollup cycle (every 5 minutes). PASSIVE checkpoints whatever it can without waiting for active readers and never blocks the collector. This keeps the WAL file small under normal operation. The viewer uses short-lived transactions (one per API request), so it does not hold snapshots that would prevent checkpointing.

### Database location

Default: `%LocalAppData%\Telltale\telltale.db`. This is per-user, not cloud-synced, and writable without admin. The collector validates at startup that the path is not inside a known sync folder (OneDrive, Google Drive, Dropbox) per TT-S-06.

## Project Layout

```
Telltale/
  Telltale.sln
  collector/                        .NET console app
    Collector.csproj
    Program.cs                      Host builder, BackgroundService registration
    CollectorWorker.cs              Main sampling loop (BackgroundService)
    NativeSampler.cs                NtQuerySystemInformation P/Invoke + validation
    ProcessSampler.cs               System.Diagnostics.Process fallback
    MachineSampler.cs               Performance counters for machine-wide metrics
    Database.cs                     All SQLite write operations + schema creation
    RollupWorker.cs                 Rollup and retention (BackgroundService)
    Config.cs                       Configuration model and validation
    Interop/
      NtDefs.cs                     Native struct definitions
  viewer/                           .NET minimal API
    Viewer.csproj
    Program.cs                      API endpoints, static file serving
  frontend/                         React + Vite
    src/
      App.tsx                       App shell, routing state, landing page logic
      StatusBar.tsx                 Collector status indicator (running/stopped)
      TimeNav.tsx                   Scale selector and breadcrumb trail
      Timeline.tsx                  uPlot charts, works at all time scales
      ProcessTable.tsx              Ranked process table with group-by-name default
      ProcessDetail.tsx             Single-process or process-group drill-down
      DataTable.tsx                 Reusable accessible data table (TT-A-01)
      api.ts                        Fetch-based API client
      types.ts                      TypeScript interfaces mirroring API responses
      utils.ts                      formatSize, formatDate, formatElapsed, etc.
      Alerts.tsx                    Problematic process alerts with period selector
      App.css                       Theming with CSS custom properties
    vite.config.ts                  Dev proxy to viewer backend, build into viewer/wwwroot
  collector.Tests/                  xUnit: sampling cost regression, rollup correctness
  viewer.Tests/                     xUnit: API integration tests
  frontend/src/utils.test.ts        Vitest: formatting and time utilities
  schema.sql                        Canonical schema (all tiers), version-tracked in source
  telltale.json                     Configuration file template
  dev.bat                           Start collector + Vite dev server for development
  publish.bat                       Self-contained single-file builds for both executables
```

The frontend splits into ~8 files rather than one. FolderFileSizeScanner's single-file App.tsx works at 910 lines, but Telltale's viewer has more distinct concerns (time navigation, chart rendering, data tables, process drill-down) and would be unwieldy as a single file.

## Schema (outline)

```sql
PRAGMA journal_mode = WAL;
PRAGMA synchronous = NORMAL;
PRAGMA auto_vacuum = INCREMENTAL;

CREATE TABLE schema_version (
    version INTEGER PRIMARY KEY
);
INSERT INTO schema_version VALUES (1);

-- One row per process lifetime (handles PID reuse via create_time)
CREATE TABLE process_instance (
    id           INTEGER PRIMARY KEY,
    pid          INTEGER NOT NULL,
    create_time  INTEGER NOT NULL,     -- process creation timestamp, epoch ms
    name         TEXT    NOT NULL,
    path         TEXT,
    command_line TEXT,                  -- nullable; controlled by config
    first_seen   INTEGER NOT NULL,     -- first sample timestamp
    last_seen    INTEGER NOT NULL,     -- updated on each sample
    UNIQUE(pid, create_time)
);

-- Raw per-process samples (retained for 24 hours by default)
CREATE TABLE sample (
    ts           INTEGER NOT NULL,     -- epoch ms
    instance_id  INTEGER NOT NULL REFERENCES process_instance(id),
    cpu_pct      REAL,                 -- percent of one logical core; null on first sample after restart
    private_mb   REAL,
    working_set_mb REAL,
    io_kb        REAL,                 -- I/O since previous sample (read+write+other), KB; null on first sample
    threads      INTEGER,
    handles      INTEGER
);
CREATE INDEX ix_sample_ts ON sample(ts);
CREATE INDEX ix_sample_inst ON sample(instance_id, ts);

-- Rolled-up per-process samples (1-minute buckets, retained for 7 days)
CREATE TABLE sample_1m (
    ts           INTEGER NOT NULL,     -- bucket start, epoch ms
    instance_id  INTEGER NOT NULL REFERENCES process_instance(id),
    cpu_pct_avg  REAL,
    cpu_pct_max  REAL,
    private_mb_max REAL,
    working_set_mb_max REAL,
    io_kb_total  REAL,                 -- SUM(io_kb) in the 1-minute window
    sample_count INTEGER
);
CREATE INDEX ix_s1m_ts ON sample_1m(ts);
CREATE INDEX ix_s1m_inst ON sample_1m(instance_id, ts);

-- Rolled-up per-process samples (10-minute buckets, retained for 12 months)
CREATE TABLE sample_10m (
    ts           INTEGER NOT NULL,     -- bucket start, epoch ms
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

-- Machine-wide metrics (same cadence as process samples)
CREATE TABLE machine (
    ts              INTEGER PRIMARY KEY,
    cpu_pct         REAL,
    memory_avail_mb REAL,
    commit_mb       REAL,
    hard_faults     INTEGER,
    disk_read_ms    REAL,              -- Avg. Disk sec/Read × 1000 (PhysicalDisk _Total)
    disk_write_ms   REAL,              -- Avg. Disk sec/Write × 1000 (PhysicalDisk _Total)
    memory_total_mb REAL,              -- total physical memory; recorded per sample so hardware changes are captured
    disk_busy_pct   REAL,              -- % Idle Time inverted (PhysicalDisk _Total); capped at 100%
    net_kbps        REAL,              -- sum of all non-loopback Network Interface adapters
    gpu_busy_pct    REAL               -- nullable; see Phase 2 notes on GPU availability
);

-- Rolled-up machine-wide metrics (1-minute buckets, retained for 7 days)
CREATE TABLE machine_1m (
    ts                  INTEGER PRIMARY KEY,  -- bucket start, epoch ms
    cpu_pct_avg         REAL,
    cpu_pct_max         REAL,
    memory_avail_mb_avg REAL,
    memory_total_mb     REAL,             -- latest value in window (changes only on hardware swap)
    commit_mb_max       REAL,
    hard_faults_total   INTEGER,              -- sum in window
    disk_read_ms_avg    REAL,
    disk_write_ms_avg   REAL,
    disk_busy_pct_avg   REAL,
    disk_busy_pct_max   REAL,
    net_kbps_avg        REAL,
    gpu_busy_pct_avg    REAL,
    sample_count        INTEGER
);

-- Rolled-up machine-wide metrics (10-minute buckets, retained for 12 months)
CREATE TABLE machine_10m (
    ts                  INTEGER PRIMARY KEY,
    cpu_pct_avg         REAL,
    cpu_pct_max         REAL,
    memory_avail_mb_avg REAL,
    memory_total_mb     REAL,             -- latest value in window
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

-- Collector self-monitoring (TT-C-08, retained for 7 days)
CREATE TABLE collector_health (
    ts              INTEGER PRIMARY KEY,
    cpu_pct         REAL,
    private_mb      REAL,
    sample_cost_ms  REAL,              -- wall time to complete one sample cycle
    process_count   INTEGER,           -- total processes observed
    stored_count    INTEGER            -- processes that met thresholds
);
```

### Design notes

**Computed values, not raw counters.** CPU percentage and I/O delta are computed by the collector from consecutive samples. The viewer and rollup logic consume them directly without needing the previous sample. CPU is expressed as percent of one logical core (not percent of machine), so per-process values are directly comparable and sum to machine-wide CPU from processes. On a 16-core machine, a single multi-threaded process can report up to 1600%. The viewer normalises to percent-of-machine where that is clearer (e.g., stacked area charts), and shows per-core percentages in the process detail view where comparability between processes matters.

**CPU delta uses actual elapsed time from a monotonic clock.** CPU percentage is computed as (kernel_time_delta + user_time_delta) / actual_elapsed_time, not divided by the configured interval. The elapsed time is measured with `Stopwatch` (QueryPerformanceCounter), not `DateTime.UtcNow` differences. This handles irregular intervals correctly (a GC pause or slow tick causing a 7-second gap) and is immune to wall-clock adjustments from NTP sync, DST changes, or manual clock changes that would produce garbage percentages. Timestamps stored in the database are still wall-clock time (`DateTimeOffset.UtcNow`) for human-readable display.

**First-sample nulls.** CPU and I/O require a previous sample to compute deltas. The first sample after a collector start (or restart) writes null for cpu_pct and io_kb. The viewer treats null as "process was running but no performance data available" and renders it as a gap, not a zero. This avoids false dips in charts when the collector restarts.

**I/O stored as delta, not rate.** The sample table stores io_kb (KB transferred since the previous sample) rather than a rate. The viewer computes rate by dividing by the actual interval between timestamps. The rollup computes total by summing: `io_kb_total = SUM(io_kb)`. This avoids both tables needing to know the configured sample interval and handles irregular intervals correctly.

**Real units.** MB, KB, and percentages are stored instead of raw bytes. This avoids integer overflow for large values, matches what the viewer displays, and makes the schema self-documenting. If storage becomes a concern, converting to INTEGER with smaller units (centipercent, KB) would save ~20 bytes per row through varint encoding, but that optimisation is not needed at the current data rates.

**Machine rollup tables.** Without machine_1m and machine_10m, the raw machine table at 5-second intervals grows to ~200 MB in a year. Machine data follows the same two-tier rollup lifecycle as process data.

**Rollup atomicity.** Each rollup step (INSERT into the target table + DELETE from the source table) runs in a single transaction. This prevents a window where data exists in both tables (double-counting in the viewer) or neither (data gap).

**Orphan cleanup.** After deleting the oldest 10-minute rollup data, the rollup worker also deletes process_instance rows that have no remaining references in any sample table. Without this, process_instance accumulates roughly 2,000-5,000 orphan rows per year. The overhead is small (~1 MB/year), but a process that has been gone for over a year and has no remaining sample data should not stay in the database.

**Process grouping in the viewer.** Chrome, Edge, VS Code, and similar applications spawn dozens of processes. The viewer's process table defaults to a grouped-by-name view: all chrome.exe instances are summed into a single "chrome.exe" row showing total CPU, total memory, instance count. Clicking a group row expands it to show individual instances. The stacked area chart (TT-V-04) also groups by name: the top 8 series are process names, not individual instances. The grouping is a viewer-side aggregation; the schema and collector are unchanged. The process detail view works at both levels: viewing "chrome.exe" shows the aggregate, and drilling into an individual instance shows that instance's timeline.

**Fallback sampler limitations.** The System.Diagnostics.Process fallback is degraded, not equivalent. Process.StartTime throws AccessDeniedException for system processes (System, smss, csrss), so (pid, create_time) deduplication does not work for them. Enumerating processes individually is slower than a single NtQuerySystemInformation call (estimated 50-200 ms vs 7.2 ms). At a 5-second interval, even the 200 ms fallback consumes only 4% of the interval, which is acceptable as a temporary degraded mode. I/O counters and handle counts may also be inaccessible for privileged processes. The fallback writes null for metrics it cannot read and logs a one-time warning per process.

## Implementation Phases

Ordered to get a working end-to-end loop as early as possible. Each phase produces something testable.

### Phase 1: Collector core (1 to 1.5 days)

The goal is a console app that samples processes at a configurable interval (default 5 seconds) and writes to SQLite.

1. .NET console app scaffold with BackgroundService pattern
2. Global mutex for single-instance enforcement, preventing a second collector from writing to the same database
3. Configuration loading from telltale.json (interval, path, thresholds)
4. Configuration validation at startup (TT-G-03)
5. SQLite database creation with schema migration, including `auto_vacuum = INCREMENTAL`
6. NtQuerySystemInformation P/Invoke with struct definitions
7. Layout validation against documented API (TT-C-05)
8. Get-Process fallback path, activated when validation fails (TT-C-05). Log a warning describing what is degraded (see "Fallback sampler limitations" above).
9. Main sampling loop: sample all processes, compute deltas from previous sample using actual elapsed time, apply thresholds, write one transaction per tick
10. First-sample handling: when no previous sample exists for a process (first tick after start, or newly seen process), write null for cpu_pct and io_kb
11. Process instance deduplication: lookup by (pid, create_time), insert on first sight, update last_seen on every sample
12. Cloud-sync folder detection (TT-S-06)
13. Performance regression test: sample 100 times, assert median under 1% of one core (TT-C-04)

This phase is the foundation. The native sampler and its validation are the hardest pieces. If the layout check works, the rest of the collector is straightforward.

### Phase 2: Machine-wide metrics and self-tracking (0.5 to 1 day)

1. Performance counter sampling: CPU, available memory, total physical memory, commit charge, hard faults, disk latency, disk busy, network throughput, GPU busy. All counters are constructed during startup, before the first sample tick. The first PerformanceCounter construction on Windows can take 2-5 seconds while the registry is parsed; this is expected startup cost and is not counted toward sample cost.
2. Graceful handling of missing or failing counters. Record null, log a warning, keep sampling everything else. (TT-C-07)
3. Locale-independent counter resolution. Performance counter names are localised on non-English Windows. Use `PdhLookupPerfNameByIndex` via P/Invoke with the well-known English indices (e.g., index 238 for `Processor`, index 6 for `% Processor Time`) to get the correct localised names at startup. If PDH lookup fails, fall back to English names and log a warning.
4. Disk counters: use `PhysicalDisk` category, `_Total` instance. `Avg. Disk sec/Read` and `Avg. Disk sec/Write` for latency (seconds from the counter, stored as milliseconds). `% Idle Time` inverted (100 - idle) for disk busy. Note: `% Disk Time` can exceed 100% on multi-disk systems; using inverted idle avoids this.
5. Network counters: enumerate all `Network Interface` instances at startup, exclude loopback and known virtual adapters (Hyper-V, vEthernet, Docker, WSL). Sum `Bytes Total/sec` across remaining adapters, convert to KB/s.
6. GPU metrics: attempt to read from the `GPU Engine` performance counter category (available on WDDM 2.0+ drivers, Windows 10+). This requires summing utilisation across engines (3D, Copy, Video Encode). If the category does not exist or cannot be read, record null and log a one-time warning. GPU metrics are best-effort; they vary by vendor and driver version, and there is no single reliable cross-vendor approach.
7. Collector health recording: own CPU, own memory, sample cost, process counts (TT-C-08)
8. Separate cadence for machine metrics if needed (e.g., 10 seconds instead of 5)

### Phase 3: Rollup, retention, and maintenance (1 to 1.5 days)

1. RollupWorker as a second BackgroundService, running on a timer (every 5 minutes)
2. **Tier 1 rollup**: aggregate raw process samples older than 24 hours into 1-minute buckets (avg, max, sum as appropriate). Aggregate raw machine samples older than 24 hours into 1-minute buckets (machine_1m). Delete raw rows after rollup. Both INSERT and DELETE in a single transaction per table to prevent double-counting or data gaps in viewer queries.
3. **Tier 2 rollup**: aggregate 1-minute process data older than 7 days into 10-minute buckets (sample_10m). Aggregate 1-minute machine data older than 7 days into 10-minute buckets (machine_10m). Delete 1-minute rows after rollup. Same single-transaction atomicity.
4. Delete 10-minute rolled-up data older than 12 months
5. Delete orphaned process_instance rows (no remaining references in sample, sample_1m, or sample_10m)
6. Delete collector_health rows older than 7 days
7. Enforce maximum database size, deleting oldest rolled-up data first (TT-S-08)
8. Call `PRAGMA incremental_vacuum` after each rollup cycle to reclaim freed pages
9. Call `PRAGMA wal_checkpoint(PASSIVE)` after each rollup cycle to keep the WAL file small
10. Verify rollup does not block or delay collection (TT-S-05)

### Phase 4: Viewer backend (0.5 to 1 day)

1. .NET minimal API with read-only SQLite connection (`Mode=ReadOnly` in connection string, short-lived per request, no long-held transactions), bound to 127.0.0.1. Read-only mode requires the WAL shm file to exist, which the collector creates. If the collector has never run, the viewer opens in normal mode against an empty database and shows a "no data yet" state.
2. `GET /api/range` returns earliest and latest timestamps in the database, so the UI knows what is navigable
3. `GET /api/timeline?from=&to=` returns machine-wide metrics for a time range. The server selects the source table based on data age and range width: raw `machine` rows within 24 hours, `machine_1m` rows for 24 hours to 7 days, `machine_10m` rows for older data. For wide time ranges, the server aggregates further (GROUP BY with appropriate bucket size) so the response stays under ~2,000 data points. The response includes the actual resolution used. For ranges spanning tier boundaries, the server unions data from the relevant tiers.
4. `GET /api/processes?from=&to=&limit=&sort=&q=&group=true` returns processes for a time range. With `group=true` (the default), rows are aggregated by process name: total CPU, total memory, instance count. With `group=false`, rows are individual process instances. The `q` parameter filters by process name (case-insensitive substring match). The server unions across the relevant tiers for the requested range.
5. `GET /api/process/:id?from=&to=` returns one process instance's metrics over time, using the same tier-selection logic as the timeline endpoint
6. `GET /api/process-group/:name?from=&to=` returns aggregated metrics over time for all instances of a named process (e.g., all chrome.exe instances summed). Uses the same tier-selection logic.
7. `GET /api/health` returns collector status (last sample time, database size, whether the collector appears to be running based on recency of last sample)
8. `GET /api/alerts?days=` returns processes with high resource usage over the specified period (1-365 days, default 1). Detection criteria: average CPU above 5% or peak memory above 500 MB. Returns alert rows with avg/peak CPU, peak memory, total I/O, instance count, time range, and human-readable reason tags. Supports period switching: 1, 3, 5, 15, 30, 60, 90, 180 days.
9. Static file serving (SPA fallback)
9. Browser auto-open on startup (same pattern as FolderFileSizeScanner)
10. Global mutex for single-instance enforcement (same pattern as FolderFileSizeScanner)
11. Different default port from FolderFileSizeScanner (5111 instead of 5000)

The server-side tier selection means the viewer frontend does not need to know about rollup boundaries or manage downsampling. It requests a time range and gets back an appropriate number of points.

### Phase 5: Viewer frontend (4 to 5 days)

This is the largest phase. Chart interaction, multi-scale navigation, and accessibility are hard to judge on paper. The uPlot React integration (manual lifecycle via useRef/useEffect, resize handling, data updates without re-renders) accounts for roughly half a day of the estimate. Process grouping adds roughly half a day to the process table and stacked chart work.

**Landing page and status (items 1-2)**

1. Default landing page: the current day's view, scrolled to the most recent data. If the collector has never run (empty database), show a "no data yet" state with instructions. If no data exists for today but older data exists, show the most recent day that has data.
2. Collector status indicator: a persistent element (header bar or status line) showing whether the collector appears to be running. Based on the recency of the last sample from `/api/health`. If the last sample is more than 3x the configured interval old, show a warning ("collector stopped at HH:MM" or "no data for the last N minutes"). This prevents the user from staring at stale data without realising collection has stopped.

**Time navigation (items 3-8)**

3. Scale selector: year, month, week, day. Each renders the appropriate overview.
4. Year view: 12 month cells, each showing summary stats (peak CPU, peak memory, total I/O). Click a month to drill in.
5. Month view: day cells with summary stats and miniature sparklines. Click a day to drill in.
6. Week view: same layout as month, scoped to 7 days.
7. Day view: full timeline with uPlot charts (CPU, memory, disk, network as separate panels or overlaid lanes). This is the primary detailed view.
8. Breadcrumb trail (2026 > August > 23) showing the current position. Each segment is clickable to navigate back up. Arrow buttons for prev/next at each level. URL parameters for deep linking (`?year=2026&month=8&day=23`).

**Day-level detail (items 9-15)**

9. Time range selection within a day: click-drag on chart, or manual timestamp input fields
10. Gap detection: identify periods with no samples and render them as visible breaks (TT-V-05)
11. Process table for selected range: defaults to grouped-by-name view (all chrome.exe instances summed into one row showing total CPU, total memory, instance count). Ranked by CPU, with proportional size bars, sortable by other columns. A group row is expandable to show individual instances beneath it.
12. Filter-as-you-type on the process table: filter by process name or path (case-insensitive substring). Essential for finding a specific process in a list of 89+.
13. Process detail view: clicking a group name shows the aggregate timeline for that process name (via `/api/process-group/:name`). Clicking an individual instance shows that instance's timeline (via `/api/process/:id`). Both views show start/stop markers and peak annotations.
14. Machine CPU broken down by contributing processes (TT-V-04): stacked area chart showing the top 8 process names (grouped, not individual instances) by CPU in the selected range, plus an "other" bucket computed as machine CPU minus the sum of the top 8. The "other" value is clamped to zero (sampling jitter can cause the process sum to slightly exceed the machine total). A legend identifies each series. Clicking a series in the legend opens that process group's detail view. A note below the chart explains that "other" includes processes below the sampling threshold and processes shorter than the sampling interval.
15. Memory display: show "X of Y GB used" using `memory_total_mb` from the machine data, not just available memory.

**Alerts (items 16-17)**

16. Alerts section: a dedicated panel at the top of the main view showing processes with notably high resource usage over a configurable period. Period selector buttons: 1 day, 3 days, 5 days, 15 days, 30 days, 60 days, 90 days, and 180 days. Clicking a period loads the alert data for that window.
17. Alert detection criteria: processes with average CPU above 5% or peak memory above 500 MB within the selected period. Each alert row shows the process name, average CPU, peak CPU, peak memory, total I/O, instance count, active time range, and reason tags (e.g., "Sustained high CPU", "Memory above 2 GB", "Heavy I/O"). Clicking an alert row navigates to that process group's detail view.

**Cross-scale features (items 18-19)**

18. The process table and process detail work at all time scales. "Top processes this month" is a valid view. Drilling into a process from a month view shows that process for the full month; from there the user can zoom into specific days.
19. When navigating from a coarser view (month) to a finer view (day) by clicking a data point, the selected process or metric carries through so the user does not lose context.

**Accessibility and polish (items 20-27)**

20. Data table behind every chart (TT-A-01): toggle between chart view and table view
21. Jump-to-timestamp: input field or URL parameter (TT-V-06)
22. Keyboard navigation for all controls including time range selection and scale switching (TT-A-02)
23. Threshold markers on the timeline for pressure periods (TT-V-07)
24. Correct heading order, landmarks, table scopes, visible focus outlines (TT-A-04)
25. Colour-independent status indicators (TT-A-03)
26. Dark/light/system theme toggle (CSS custom properties, same approach as FolderFileSizeScanner)
27. High-contrast and reduced-motion support (TT-A-05)

I would prototype items 7-10 early with real data (even manually generated) to validate that uPlot handles the day-level interaction model before building the navigation layers on top.

### Phase 6: Packaging and auto-start (0.5 day)

1. publish.bat: self-contained single-file builds for both collector and viewer
2. Task Scheduler setup script: register collector to run at login
3. Verify the viewer can read the database while the collector is writing
4. Verify the collector restarts cleanly after a crash (re-run picks up the same database, first-sample nulls are written correctly)
5. End-to-end test: start collector, let it run, open viewer, browse data across time scales
6. Verify single-instance mutex works for both collector and viewer

## Space budget

Telltale should be invisible on disk. The estimates below are based on the actual per-row sizes in SQLite (data, header, rowid, cell overhead) plus index entries and B-tree page overhead. Each sample row is approximately 60 bytes on disk in the table, plus roughly 35 bytes across the two indexes. Machine rows are approximately 95 bytes (more columns including memory_total_mb, but the primary key serves as the B-tree key so there is no separate rowid cost). Rolled-up rows are similar in size to their raw equivalents.

### Arithmetic

At the default 5-second interval with 89 processes meeting thresholds:

- Ticks per day: 86,400 / 5 = **17,280**
- Process sample rows per day: 89 × 17,280 = **1,537,920**
- Bytes per process sample row (table + indexes + page overhead): **~95 bytes**
- Raw process data per day: 1,537,920 × 95 ≈ **146 MB**

- Machine rows per day: **17,280**
- Bytes per machine row (table + page overhead, no separate indexes): **~95 bytes**
- Raw machine data per day: 17,280 × 95 ≈ **1.6 MB**

For rollups, not all 89 processes run continuously. Some are transient (browser tabs, dev tool windows, build processes). The estimates below use ~40 average active processes for the 10-minute tier and ~50 for the 1-minute tier, reflecting that shorter windows catch more active processes.

### Steady-state sizes (default settings)

| Item | Size | How calculated |
|------|------|----------------|
| Collector executable | ~70 MB | Self-contained .NET single-file (includes runtime) |
| Viewer executable | ~70 MB | Same, only present on disk, not running in background |
| Raw process data (24h) | ~146 MB | 1.54M rows × ~95 bytes |
| Raw machine data (24h) | ~1.6 MB | 17,280 rows × ~95 bytes |
| 1-minute process rollup (7d) | ~48 MB | ~50 avg processes × 1,440 min × 7 days × ~95 bytes |
| 1-minute machine rollup (7d) | ~1 MB | 10,080 rows × ~100 bytes |
| 10-minute process rollup (12mo) | ~200 MB | ~40 avg processes × 144 intervals/day × 365 days × ~95 bytes |
| 10-minute machine rollup (12mo) | ~5 MB | 52,560 rows × ~100 bytes |
| Collector health (7d) | ~4 MB | 120,960 rows × ~35 bytes |
| Process instances | ~1 MB | ~2,000-5,000 instances over 12 months |
| **Steady-state total DB** | **~407 MB** | |
| Maximum DB (configurable cap) | 500 MB default | TT-S-08: oldest data deleted first when reached |

### What the levers do

The interval, thresholds, and retention are all configurable in telltale.json. No rebuild required.

| Change | Effect on raw process data per day |
|--------|-------------------------------------|
| Interval 5s → 2s | 2.5x larger (~365 MB/day) |
| Interval 5s → 10s | 2x smaller (~73 MB/day) |
| Thresholds raised (89 → 40 processes) | 2.2x smaller (~66 MB/day) |
| Raw retention 24h → 12h | 2x smaller raw peak |

At 2-second intervals with 24-hour raw retention, raw process data alone would be ~365 MB, leaving only ~135 MB for all rollup tiers. This is why 5 seconds is the default: it gives a reasonable balance between resolution and database size.

At 10-second intervals, the steady-state total drops to roughly 230 MB, which provides substantial headroom under the 500 MB cap.

The configurable size cap (TT-S-08) is the safety net. If the database reaches 500 MB (or whatever the user sets), the oldest rolled-up data is deleted first. This means Telltale can never grow unboundedly, regardless of how many processes are active or how long it runs.

## Efficiency considerations

These are the choices that keep Telltale fast, small, and invisible.

**Sampling cost.** The benchmarked 7.2 ms per sample cycle means the collector is working for 0.14% of each 5-second interval. The rest of the time it's sleeping, consuming no CPU. The .NET host adds negligible overhead when idle.

**Disk writes.** At ~89 threshold-passing processes per tick, each row is roughly 60 bytes of payload. That's ~5.3 KB per tick, ~1.1 KB/s sustained. SQLite with WAL absorbs this without visible disk activity. One transaction per tick (not per row) keeps write amplification low. Incremental auto-vacuum reclaims space from deleted rows without the I/O spike of a full VACUUM.

**WAL management.** Passive checkpointing every 5 minutes keeps the WAL file small without blocking reads or writes. Short-lived viewer transactions (one per API request) ensure the WAL is always checkpointable.

**Memory.** The collector holds one sample buffer (reused), one previous-sample map for delta computation (keyed by PID + create_time, ~89 entries under idle, maybe 200-300 under load), and the SQLite connection. Estimated steady-state: under 30 MB. This is the .NET runtime overhead. The actual working set of the collector's own data is well under 1 MB.

**Viewer on demand.** The viewer process is not running unless you open it. No background memory cost when you're not looking at data.

**Server-side resolution.** The timeline API returns at most ~2,000 data points regardless of the requested time range. For a year view, this means daily aggregation computed from the 10-minute rollup table, not scanning millions of raw rows. The aggregation is a simple SQL GROUP BY, not a separate precomputed tier.

**Configurable everything.** The interval, thresholds, retention tiers, and size cap are all in telltale.json. If 5 seconds feels too aggressive, change it to 10. If the thresholds are catching too many idle processes, raise them. No rebuild required.

## Recommendations on the brief's open questions

**Storage estimate under real load.** At default settings (5-second interval, 89 processes at threshold), raw process data is approximately 146 MB per day. Run the snapshot script during a real work session to validate the process count assumption; a busy development session with browsers, IDEs, build tools, and Docker may push the active process count higher. Nothing in the architecture changes if the actual rate is higher than estimated. Threshold tuning is a config change.

**Short-lived processes.** Defer to v2. The machine-wide CPU graph shows the spike; the viewer should note "some CPU usage may be from processes shorter than the sampling interval" where relevant, rather than presenting an incomplete picture as complete (aligns with TT-V-05's principle of honesty about gaps). Event tracing adds 1-2 weeks and requires admin rights, which changes the installation story.

**Alerting.** Not in v1. The viewer is retrospective. The collector's architecture supports adding a notification path later (it's just another consumer of sample data), but designing useful alert thresholds is a separate project.

**Multi-machine.** Single machine for v1. No machine_id in the schema. Adding one later is a trivial migration (new column with a default value). Including it now introduces a concept that v1 never exercises.

**How far back in one screen.** The viewer supports year, month, week, and day views using server-side aggregation. For data older than 7 days, the 10-minute rollup table is the source. For data within 7 days, the 1-minute rollup table provides finer resolution. The server computes hourly or daily buckets on the fly via GROUP BY, which is fast enough for the row counts involved (~52,000 rows per year of 10-minute machine data, ~2.1 million rows per year of 10-minute process data). If query performance becomes an issue at scale, adding a precomputed hourly tier is a localised optimisation that does not change the API contract.

**Laptop / battery.** Not a concern for v1 (target is a 16-core desktop). Document in the config template that the interval can be increased to 10-30 seconds for lower-power machines.

**Configuration hot-reload.** Deferred to v2. The collector reads telltale.json at startup. Changing the interval or thresholds requires restarting the collector. For a background process that may run for months, this is a friction point, but the restart is fast (< 1 second) and the first-sample null handling means no data is lost beyond the single tick after restart.

## What is not covered

Everything listed as "explicitly out of scope for v1" in the design brief: per-process network/disk split, per-process GPU attribution, short-lived process capture, multi-machine, alerting, remote viewer access. Also not covered: an installer (MSI or similar), automatic updates, any kind of tray icon (the brief already notes this as a later addition), and configuration hot-reload.
