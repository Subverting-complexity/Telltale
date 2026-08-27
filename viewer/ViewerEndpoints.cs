using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Telltale.Viewer;

/// <summary>
/// The viewer's HTTP surface: the static files that serve the frontend, every
/// /api endpoint, and the fallback that hands unmatched paths back to the SPA.
///
/// This lives apart from the viewer's own entry point so that the single-process
/// Telltale host can serve the same API without the viewer having to be its own
/// executable. The viewer executable and the host both call it and get identical
/// behaviour.
/// </summary>
public static class ViewerEndpoints
{
    /// <summary>
    /// Serves the frontend and the API from <paramref name="app"/>, reading the
    /// capture database at <paramref name="dbPath"/>.
    /// </summary>
    /// <remarks>
    /// The path is a parameter rather than something read from configuration in
    /// here because the two callers do not share a source for it: the viewer's
    /// test factory overrides it after the web host is built, and the Telltale
    /// host resolves it from telltale.json instead.
    /// </remarks>
    public static WebApplication MapTelltaleApi(this WebApplication app, string dbPath)
    {
        // No CORS policy: nothing legitimately reaches this API cross-origin. The
        // shipped build serves the frontend from this executable's own wwwroot, and
        // during development Vite proxies /api to the viewer from its own server, so
        // the browser only ever sees its own origin. A policy allowing any origin let
        // any page the user visited read their capture history through their browser.
        app.UseDefaultFiles();
        app.UseStaticFiles();

        var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        var logger = app.Logger;

        SqliteConnection OpenDb()
        {
            string mode = File.Exists(dbPath) ? "ReadOnly" : "ReadWrite";
            var conn = new SqliteConnection($"Data Source={dbPath};Mode={mode}");
            conn.Open();
            return conn;
        }

        // Every endpoint answers a failed query with its own empty shape so the page
        // still renders rather than breaking. That leaves the caller unable to tell a
        // capture with no data from one the viewer could not read, so the reason is
        // recorded here before the empty result goes out. Warning rather than error:
        // the request itself is still answered.
        //
        // Recorded once per source per distinct message. A capture that cannot be read
        // fails on every request, and the frontend polls /api/health every ten seconds,
        // so logging each one would fill the rotating log in a few hours and evict
        // everything else in it. The cost of collapsing them is that a failure which
        // clears and returns with the same message is only reported once.
        //
        // Bounded: the keys are the fixed set of route templates below plus the one
        // constant used for the configured path, and each value is replaced rather
        // than accumulated. The check and the set are not one atomic step, so two
        // simultaneous first requests to the same source can both report. That costs a
        // duplicate line and nothing else, which is not worth locking for.
        //
        // The state lives with the WebApplication, so it starts empty each time the
        // host builds one. Reopening the window reports a standing fault again.

        // Not a route, so it cannot collide with the endpoint keys.
        const string ConfiguredPathSource = "the configured database path";

        var lastReported = new ConcurrentDictionary<string, string>();

        bool NotYetReported(string source, string message)
        {
            if (lastReported.TryGetValue(source, out string? previous) && previous == message)
                return false;

            lastReported[source] = message;
            return true;
        }

        void ReportQueryFailure(SqliteException ex, string endpoint)
        {
            if (NotYetReported(endpoint, ex.Message))
                logger.LogWarning(ex, "The capture database could not be queried for {Endpoint}. Returning an empty result.", endpoint);
        }

        // --- Threshold constants (shared by /api/alerts and /api/thresholds) ---
        const double SystemCpuElevatedPct = 10;
        const double SystemCpuHighPct = 50;
        const double SystemMemoryHighPct = 80;

        const double ProcessCpuNotablePct = 5;
        const double ProcessCpuElevatedPct = 10;
        const double ProcessCpuHighPct = 50;
        const double ProcessMemoryNotableMb = 500;
        const double ProcessMemoryHighMb = 2048;
        const double ProcessIoHeavyKb = 10485760;
        const double ProcessCpuSpikePct = 200;

        app.MapGet("/api/range", () =>
        {
            try
            {
                using var conn = OpenDb();
                using var cmd = conn.CreateCommand();

                cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='sample'";
                if (cmd.ExecuteScalar() == null)
                    return Results.Json(new { min = (long?)null, max = (long?)null }, jsonOptions);

                cmd.CommandText = """
                    SELECT MIN(ts), MAX(ts) FROM (
                        SELECT MIN(ts) as ts FROM sample
                        UNION ALL SELECT MAX(ts) FROM sample
                        UNION ALL SELECT MIN(ts) FROM sample_1m
                        UNION ALL SELECT MAX(ts) FROM sample_1m
                        UNION ALL SELECT MIN(ts) FROM sample_10m
                        UNION ALL SELECT MAX(ts) FROM sample_10m
                        UNION ALL SELECT MIN(ts) FROM machine
                        UNION ALL SELECT MAX(ts) FROM machine
                        UNION ALL SELECT MIN(ts) FROM machine_1m
                        UNION ALL SELECT MAX(ts) FROM machine_1m
                        UNION ALL SELECT MIN(ts) FROM machine_10m
                        UNION ALL SELECT MAX(ts) FROM machine_10m
                    )
                    """;
                using var reader = cmd.ExecuteReader();
                if (reader.Read() && !reader.IsDBNull(0))
                {
                    return Results.Json(new { min = reader.GetInt64(0), max = reader.GetInt64(1) }, jsonOptions);
                }
                return Results.Json(new { min = (long?)null, max = (long?)null }, jsonOptions);
            }
            catch (SqliteException ex)
            {
                ReportQueryFailure(ex, "/api/range");
                return Results.Json(new { min = (long?)null, max = (long?)null }, jsonOptions);
            }
        });

        // `bucket` is taken as a string and parsed here rather than bound as a
        // long?, so that a value of zero, a negative one, or something that is not
        // a number at all reads as "no granularity asked for" instead of failing
        // the request. A person editing the query string by hand should get the
        // chart back, not a 400.
        app.MapGet("/api/timeline", (long from, long to, string? bucket) =>
        {
            // Parsed as invariant, matching how minimal APIs bind `from` and `to`.
            // A value off the wire should not read differently under a different
            // machine locale.
            long? requestedBucket =
                long.TryParse(bucket, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed)
                && parsed > 0 ? parsed : null;

            try
            {
                using var conn = OpenDb();

                // No table check here. TimelineQuery has to ask sqlite_master which
                // tier tables exist before it can read their coverage, and a check
                // beside it asked the same question a second time on every request.
                // It answers a database with no machine table with the same empty
                // result this endpoint used to build for itself.
                var result = TimelineQuery.Execute(conn, from, to, requestedBucket);

                var points = result.Points.Select(p => new
                {
                    ts = p.Ts,
                    cpuPct = p.CpuPct,
                    memoryAvailMb = p.MemoryAvailMb,
                    commitMb = p.CommitMb,
                    hardFaults = p.HardFaults,
                    diskReadMs = p.DiskReadMs,
                    diskWriteMs = p.DiskWriteMs,
                    memoryTotalMb = p.MemoryTotalMb,
                    diskBusyPct = p.DiskBusyPct,
                    netKbps = p.NetKbps,
                    gpuBusyPct = p.GpuBusyPct,
                });

                return Results.Json(new
                {
                    resolution = result.Resolution,
                    bucketMs = result.BucketMs,
                    bucketRequestMs = result.BucketRequestMs,
                    minBucketMs = result.MinBucketMs,
                    tierFloorMs = result.TierFloorMs,
                    points,
                }, jsonOptions);
            }
            catch (SqliteException ex)
            {
                ReportQueryFailure(ex, "/api/timeline");
                return Results.Json(EmptyTimeline(requestedBucket), jsonOptions);
            }
        });

        app.MapGet("/api/processes", (long from, long to, int? limit, string? sort, string? q, bool? group) =>
        {
            bool grouped = group ?? true;
            try
            {
                using var conn = OpenDb();
                if (!HasTable(conn, "sample"))
                    return Results.Json(new { grouped, processes = Array.Empty<object>() }, jsonOptions);
                int take = Math.Clamp(limit ?? 50, 1, 500);
        
                var plan = PlanTiers(conn, from, to, isMachine: false);
                TierSource source = TierSql.Source(plan, isMachine: false);
                using var cmd = conn.CreateCommand();
        
                if (grouped)
                {
                    // The group is totalled across its instances at each instant, then those
                    // totals are averaged over time. Scaling by weight happens on the inner
                    // total, so an instance present for only part of a rollup bucket
                    // contributes the share of the bucket it was actually there for.
                    string weightedCpu = TierSql.AvgOfWeightedTotalsExpr("sub.ts_cpu_weighted", "sub.ts_weight");
                    string sortExpr = sort switch
                    {
                        "memory" => "MAX(sub.ts_mem)",
                        "io" => "SUM(sub.ts_io)",
                        "name" => "sub.name",
                        _ => weightedCpu
                    };
                    string sortDir = sort == "name" ? "ASC" : "DESC";
                    cmd.CommandText = $"""
                        SELECT sub.name,
                               {TierSql.AvgOfWeightedTotals("sub.ts_cpu_weighted", "sub.ts_weight", "avg_cpu_pct")},
                               MAX(sub.ts_mem) as peak_private_mb,
                               SUM(sub.ts_io) as total_io_kb,
                               MAX(sub.inst_cnt) as instance_count,
                               (SELECT pi2.path FROM process_instance pi2 WHERE pi2.name = sub.name AND pi2.path IS NOT NULL LIMIT 1) as path
                        FROM (
                            SELECT pi.name,
                                   {TierSql.WeightedTotal("s.cpu_pct", "ts_cpu_weighted", "s.weight")},
                                   SUM(s.private_mb) as ts_mem,
                                   SUM(s.io_kb) as ts_io,
                                   COUNT(DISTINCT s.instance_id) as inst_cnt,
                                   {TierSql.InstantWeight("s.weight")} as ts_weight
                            FROM {source.Sql} s
                            JOIN process_instance pi ON pi.id = s.instance_id
                            WHERE s.ts >= @from AND s.ts <= @to
                            {(q != null ? "AND pi.name LIKE @q ESCAPE '\\'" : "")}
                            GROUP BY pi.name, s.ts
                        ) sub
                        GROUP BY sub.name
                        ORDER BY {sortExpr} {sortDir}
                        LIMIT @limit
                        """;
                }
                else
                {
                    string sortExpr = sort switch
                    {
                        "memory" => $"MAX(s.private_mb)",
                        "io" => $"SUM(s.io_kb)",
                        "name" => "pi.name",
                        _ => TierSql.WeightedAvgExpr("s.cpu_pct", "s.weight")
                    };
                    string sortDirUngrouped = sort == "name" ? "ASC" : "DESC";
                    cmd.CommandText = $"""
                        SELECT pi.id, pi.pid, pi.name, pi.path,
                               {TierSql.WeightedAvg("s.cpu_pct", "avg_cpu_pct", "s.weight")},
                               MAX(s.private_mb) as peak_private_mb,
                               SUM(s.io_kb) as total_io_kb
                        FROM {source.Sql} s
                        JOIN process_instance pi ON pi.id = s.instance_id
                        WHERE s.ts >= @from AND s.ts <= @to
                        {(q != null ? "AND pi.name LIKE @q ESCAPE '\\'" : "")}
                        GROUP BY pi.id
                        ORDER BY {sortExpr} {sortDirUngrouped}
                        LIMIT @limit
                        """;
                }
        
                AddTierBounds(cmd, source);
                cmd.Parameters.AddWithValue("@from", from);
                cmd.Parameters.AddWithValue("@to", to);
                cmd.Parameters.AddWithValue("@limit", take);
                if (q != null) cmd.Parameters.AddWithValue("@q", $"%{EscapeLike(q)}%");
        
                var results = new List<object>();
                using var reader = cmd.ExecuteReader();
        
                if (grouped)
                {
                    while (reader.Read())
                    {
                        results.Add(new
                        {
                            name = reader.GetString(0),
                            cpuPct = reader.IsDBNull(1) ? 0.0 : reader.GetDouble(1),
                            privateMb = reader.IsDBNull(2) ? 0.0 : reader.GetDouble(2),
                            ioKb = reader.IsDBNull(3) ? 0.0 : reader.GetDouble(3),
                            instanceCount = reader.GetInt32(4),
                            path = reader.IsDBNull(5) ? null : reader.GetString(5),
                        });
                    }
                }
                else
                {
                    while (reader.Read())
                    {
                        results.Add(new
                        {
                            id = reader.GetInt64(0),
                            pid = reader.GetInt32(1),
                            name = reader.GetString(2),
                            path = reader.IsDBNull(3) ? null : reader.GetString(3),
                            cpuPct = reader.IsDBNull(4) ? 0.0 : reader.GetDouble(4),
                            privateMb = reader.IsDBNull(5) ? 0.0 : reader.GetDouble(5),
                            ioKb = reader.IsDBNull(6) ? 0.0 : reader.GetDouble(6),
                        });
                    }
                }
        
                return Results.Json(new { grouped, processes = results }, jsonOptions);
            }
            catch (SqliteException ex)
            {
                ReportQueryFailure(ex, "/api/processes");
                return Results.Json(new { grouped, processes = Array.Empty<object>() }, jsonOptions);
            }
        });

        app.MapGet("/api/process/{id:long}", (long id, long from, long to) =>
        {
            try
            {
                using var conn = OpenDb();
                if (!HasTable(conn, "sample"))
                    return Results.Json(new { info = (object?)null, resolution = "sample", points = Array.Empty<object>() }, jsonOptions);
                var plan = PlanTiers(conn, from, to, isMachine: false);
                TierSource source = TierSql.Source(plan, isMachine: false);
        
                using var cmd = conn.CreateCommand();
                if (plan.Bucket > 0)
                {
                    cmd.CommandText = $"""
                        SELECT (s.ts / @bucket) * @bucket as ts,
                               {TierSql.WeightedAvg("s.cpu_pct", "cpu_pct", "s.weight")},
                               MAX(s.private_mb) as private_mb,
                               MAX(s.working_set_mb) as working_set_mb, SUM(s.io_kb) as io_kb
                        FROM {source.Sql} s
                        WHERE s.instance_id = @id AND s.ts >= @from AND s.ts <= @to
                        GROUP BY s.ts / @bucket ORDER BY ts
                        """;
                    cmd.Parameters.AddWithValue("@bucket", plan.Bucket);
                }
                else
                {
                    cmd.CommandText = $"""
                        SELECT s.ts, s.cpu_pct as cpu_pct, s.private_mb as private_mb,
                               s.working_set_mb as working_set_mb, s.io_kb as io_kb
                        FROM {source.Sql} s
                        WHERE s.instance_id = @id AND s.ts >= @from AND s.ts <= @to
                        ORDER BY s.ts
                        """;
                }
        
                AddTierBounds(cmd, source);
                cmd.Parameters.AddWithValue("@id", id);
                cmd.Parameters.AddWithValue("@from", from);
                cmd.Parameters.AddWithValue("@to", to);
        
                var points = new List<object>();
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    points.Add(new
                    {
                        ts = reader.GetInt64(0),
                        cpuPct = reader.IsDBNull(1) ? null : (double?)reader.GetDouble(1),
                        privateMb = reader.IsDBNull(2) ? null : (double?)reader.GetDouble(2),
                        workingSetMb = reader.IsDBNull(3) ? null : (double?)reader.GetDouble(3),
                        ioKb = reader.IsDBNull(4) ? null : (double?)reader.GetDouble(4),
                    });
                }
        
                using var infoCmd = conn.CreateCommand();
                infoCmd.CommandText = "SELECT pid, name, path, command_line, first_seen, last_seen FROM process_instance WHERE id = @id";
                infoCmd.Parameters.AddWithValue("@id", id);
                using var infoReader = infoCmd.ExecuteReader();
        
                object? info = null;
                if (infoReader.Read())
                {
                    info = new
                    {
                        pid = infoReader.GetInt32(0),
                        name = infoReader.GetString(1),
                        path = infoReader.IsDBNull(2) ? null : infoReader.GetString(2),
                        commandLine = infoReader.IsDBNull(3) ? null : infoReader.GetString(3),
                        firstSeen = infoReader.GetInt64(4),
                        lastSeen = infoReader.GetInt64(5),
                    };
                }
        
                return Results.Json(new { info, resolution = plan.Resolution, points }, jsonOptions);
            }
            catch (SqliteException ex)
            {
                ReportQueryFailure(ex, "/api/process/{id:long}");
                return Results.Json(new { info = (object?)null, resolution = "sample", points = Array.Empty<object>() }, jsonOptions);
            }
        });

        app.MapGet("/api/process-group/{name}", (string name, long from, long to) =>
        {
            try
            {
                using var conn = OpenDb();
                if (!HasTable(conn, "sample"))
                    return Results.Json(new { instances = Array.Empty<object>(), resolution = "sample", points = Array.Empty<object>() }, jsonOptions);
                var plan = PlanTiers(conn, from, to, isMachine: false);
                TierSource source = TierSql.Source(plan, isMachine: false);
        
                long effectiveBucket = plan.Bucket > 0 ? plan.Bucket : 5000;
        
                using var cmd = conn.CreateCommand();
        
                // Total the group at each instant first, then combine those totals across
                // the bucket, the same shape /api/processes uses. Summing rows directly
                // over a bucket would scale with how many rows the bucket happens to
                // hold, which differs between tiers and would step at the boundary.
                // The bucket holding the tier changeover draws from both tiers, so the
                // totals are weighted by the span each instant covers.
                cmd.CommandText = $"""
                    SELECT (sub.ts / @bucket) * @bucket as ts,
                           {TierSql.AvgOfWeightedTotals("sub.ts_cpu_weighted", "sub.ts_weight", "cpu_pct")},
                           {TierSql.AvgOfWeightedTotals("sub.ts_mem_weighted", "sub.ts_weight", "private_mb")},
                           {TierSql.AvgOfWeightedTotals("sub.ts_ws_weighted", "sub.ts_weight", "working_set_mb")},
                           SUM(sub.ts_io) as io_kb,
                           MAX(sub.inst_cnt) as instance_count
                    FROM (
                        SELECT s.ts,
                               {TierSql.WeightedTotal("s.cpu_pct", "ts_cpu_weighted", "s.weight")},
                               {TierSql.WeightedTotal("s.private_mb", "ts_mem_weighted", "s.weight")},
                               {TierSql.WeightedTotal("s.working_set_mb", "ts_ws_weighted", "s.weight")},
                               SUM(s.io_kb) as ts_io,
                               COUNT(DISTINCT s.instance_id) as inst_cnt,
                               {TierSql.InstantWeight("s.weight")} as ts_weight
                        FROM {source.Sql} s
                        JOIN process_instance pi ON pi.id = s.instance_id
                        WHERE pi.name = @name AND s.ts >= @from AND s.ts <= @to
                        GROUP BY s.ts
                    ) sub
                    GROUP BY sub.ts / @bucket ORDER BY ts
                    """;
        
                AddTierBounds(cmd, source);
                cmd.Parameters.AddWithValue("@bucket", effectiveBucket);
                cmd.Parameters.AddWithValue("@name", name);
                cmd.Parameters.AddWithValue("@from", from);
                cmd.Parameters.AddWithValue("@to", to);
        
                var points = new List<object>();
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    points.Add(new
                    {
                        ts = reader.GetInt64(0),
                        cpuPct = reader.IsDBNull(1) ? null : (double?)reader.GetDouble(1),
                        privateMb = reader.IsDBNull(2) ? null : (double?)reader.GetDouble(2),
                        workingSetMb = reader.IsDBNull(3) ? null : (double?)reader.GetDouble(3),
                        ioKb = reader.IsDBNull(4) ? null : (double?)reader.GetDouble(4),
                        instanceCount = reader.GetInt32(5),
                    });
                }
        
                return Results.Json(new { name, resolution = plan.Resolution, points }, jsonOptions);
            }
            catch (SqliteException ex)
            {
                ReportQueryFailure(ex, "/api/process-group/{name}");
                return Results.Json(new { name, resolution = "sample", points = Array.Empty<object>() }, jsonOptions);
            }
        });

        app.MapGet("/api/alerts", (int? days) =>
        {
            int period = Math.Clamp(days ?? 1, 1, 365);
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            long from = now - (long)TimeSpan.FromDays(period).TotalMilliseconds;

            try
            {
                using var conn = OpenDb();
                using var cmd = conn.CreateCommand();

                cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='sample'";
                if (cmd.ExecuteScalar() == null)
                    return Results.Json(new { period, alerts = Array.Empty<object>() }, jsonOptions);

                var plan = PlanTiers(conn, from, now, isMachine: false);
                TierSource source = TierSql.Source(plan, isMachine: false);
                // A threshold has to be compared against a time-weighted mean, or whether
                // an alert fires depends on how much of the window came from the raw
                // tier rather than on how the process actually behaved.
                string alertAvgCpu = TierSql.WeightedAvgExpr("s.cpu_pct", "s.weight");
                cmd.CommandText = $"""
                    SELECT pi.name,
                           {TierSql.WeightedAvg("s.cpu_pct", "avg_cpu", "s.weight")},
                           MAX(s.cpu_pct_peak) as peak_cpu,
                           MAX(s.private_mb) as peak_memory_mb,
                           SUM(s.io_kb) as total_io_kb,
                           SUM(s.weight) as sample_count,
                           COUNT(DISTINCT s.instance_id) as instance_count,
                           MIN(s.ts) as first_ts,
                           MAX(s.ts) as last_ts
                    FROM {source.Sql} s
                    JOIN process_instance pi ON pi.id = s.instance_id
                    WHERE s.ts >= @from AND s.ts <= @to
                      AND LOWER(pi.name) != 'idle'
                    GROUP BY pi.name
                    HAVING {alertAvgCpu} > {ProcessCpuNotablePct} OR MAX(s.private_mb) > {ProcessMemoryNotableMb}
                    ORDER BY {alertAvgCpu} DESC
                    LIMIT 50
                    """;

                AddTierBounds(cmd, source);
                cmd.Parameters.AddWithValue("@from", from);
                cmd.Parameters.AddWithValue("@to", now);

                var alerts = new List<object>();
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    double avgCpu = reader.IsDBNull(1) ? 0 : reader.GetDouble(1);
                    double peakCpu = reader.IsDBNull(2) ? 0 : reader.GetDouble(2);
                    double peakMemMb = reader.IsDBNull(3) ? 0 : reader.GetDouble(3);
                    double totalIoKb = reader.IsDBNull(4) ? 0 : reader.GetDouble(4);

                    var reasons = new List<string>();
                    if (avgCpu > ProcessCpuHighPct) reasons.Add("Sustained high CPU");
                    else if (avgCpu > ProcessCpuElevatedPct) reasons.Add("Elevated CPU");
                    else if (avgCpu > ProcessCpuNotablePct) reasons.Add("Notable CPU usage");
                    if (peakCpu > ProcessCpuSpikePct) reasons.Add($"CPU spike above {ProcessCpuSpikePct}%");
                    if (peakMemMb > ProcessMemoryHighMb) reasons.Add($"Memory above {ProcessMemoryHighMb / 1024} GB");
                    else if (peakMemMb > ProcessMemoryNotableMb) reasons.Add($"Memory above {ProcessMemoryNotableMb} MB");
                    if (totalIoKb > ProcessIoHeavyKb) reasons.Add("Heavy I/O (10+ GB total)");

                    alerts.Add(new
                    {
                        name = reader.GetString(0),
                        avgCpuPct = Math.Round(avgCpu, 2),
                        peakCpuPct = Math.Round(peakCpu, 2),
                        peakMemoryMb = Math.Round(peakMemMb, 1),
                        totalIoKb = Math.Round(totalIoKb, 0),
                        // Raw samples represented, not rows read: a rollup row stands for
                        // however many raw samples went into it.
                        sampleCount = reader.GetInt64(5),
                        instanceCount = reader.GetInt32(6),
                        firstTs = reader.GetInt64(7),
                        lastTs = reader.GetInt64(8),
                        reasons,
                    });
                }

                return Results.Json(new { period, alerts }, jsonOptions);
            }
            catch (SqliteException ex)
            {
                ReportQueryFailure(ex, "/api/alerts");
                return Results.Json(new { period, alerts = Array.Empty<object>() }, jsonOptions);
            }
        });

        app.MapGet("/api/health", () =>
        {
            long lastSampleTs = 0;
            double sampleCostMs = 0;
            int processCount = 0;
            int storedCount = 0;

            // The core count of the machine this recording was made on. Falls back
            // to the machine reading it, which is what the viewer used to do
            // unconditionally: right whenever a capture is read where it was made,
            // and wrong the moment it is copied somewhere else. A recording made
            // before machine_info existed has no row, and the fallback is the only
            // answer available for it.
            int logicalProcessors = Environment.ProcessorCount;

            try
            {
                using var conn = OpenDb();
                using var cmd = conn.CreateCommand();

                cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='collector_health'";
                if (cmd.ExecuteScalar() != null)
                {
                    cmd.CommandText = "SELECT ts, sample_cost_ms, process_count, stored_count FROM collector_health ORDER BY ts DESC LIMIT 1";
                    using var reader = cmd.ExecuteReader();
                    if (reader.Read())
                    {
                        lastSampleTs = reader.GetInt64(0);
                        sampleCostMs = reader.GetDouble(1);
                        processCount = reader.GetInt32(2);
                        storedCount = reader.GetInt32(3);
                    }
                }

                cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='machine_info'";
                if (cmd.ExecuteScalar() != null)
                {
                    cmd.CommandText = "SELECT logical_processors FROM machine_info WHERE id = 1";
                    if (cmd.ExecuteScalar() is { } recorded && recorded != DBNull.Value)
                    {
                        int recordedCount = Convert.ToInt32(recorded);

                        // A count of zero or less would make every conversion that
                        // uses it either divide by zero or change sign, so it is
                        // treated as no answer rather than as an answer.
                        if (recordedCount > 0) logicalProcessors = recordedCount;
                    }
                }
            }
            catch (SqliteException ex)
            {
                // An unreadable capture leaves the counters at their defaults,
                // which reports the collector as not running. That is the honest
                // answer for a database the viewer cannot open.
                ReportQueryFailure(ex, "/api/health");
            }

            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            bool collectorRunning = lastSampleTs > 0 && (now - lastSampleTs) < 15000;

            long dbSizeBytes = 0;
            try
            {
                var fi = new FileInfo(dbPath);
                if (fi.Exists) dbSizeBytes = fi.Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                or ArgumentException or NotSupportedException)
            {
                // This guards a file probe rather than a query, so SqliteException is
                // not what it can throw. Only the reported size is lost either way, so
                // the rest of the health response is still worth returning.
                //
                // The last two cover a configured database path that is empty or
                // malformed, which reaches here through the FileInfo constructor. That
                // is a configuration error rather than a transient one, so it is worth
                // a warning, where the transient cases below are left at debug and so
                // are dropped by the Information threshold the host's file logger
                // applies. That is deliberate: only the reported size is lost. Letting
                // it escape instead would turn /api/health into a 500, and the frontend
                // discards a failed health poll silently, so the operator would see the
                // status bar disappear with nothing anywhere explaining why.
                //
                // Collapsed like the query failures, and for the same reason. An
                // unusable path is a standing fault rather than a transient one, so it
                // throws on every poll of this endpoint; reporting each one would
                // refill the log this collapsing exists to protect.
                if (ex is ArgumentException or NotSupportedException)
                {
                    if (NotYetReported(ConfiguredPathSource, ex.Message))
                        logger.LogWarning(ex, "The configured capture database path is not usable.");
                }
                else
                {
                    logger.LogDebug(ex, "The size of the capture database could not be read.");
                }
            }

            return Results.Json(new
            {
                collectorRunning,
                lastSampleTs,
                sampleCostMs,
                processCount,
                storedCount,
                dbSizeMb = Math.Round(dbSizeBytes / (1024.0 * 1024.0), 1),
                logicalProcessors,
            }, jsonOptions);
        });

        app.MapGet("/api/baselines", (string? names) =>
        {
            if (string.IsNullOrWhiteSpace(names))
                return Results.Json(new { baselines = Array.Empty<object>() }, jsonOptions);

            var nameList = names.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (nameList.Length == 0)
                return Results.Json(new { baselines = Array.Empty<object>() }, jsonOptions);
            if (nameList.Length > 50)
                nameList = nameList[..50];

            try
            {
                using var conn = OpenDb();
                using var checkCmd = conn.CreateCommand();
                checkCmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='sample_1m'";
                if (checkCmd.ExecuteScalar() == null)
                    return Results.Json(new { baselines = Array.Empty<object>() }, jsonOptions);

                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                long sevenDaysAgo = now - 7L * 86_400_000L;

                // Use sample_1m for 7-day lookback (within the 1m tier range)
                string table = "sample_1m";
                int intervalMinutes = 1;
                int minDataPoints = 24 * 60 / intervalMinutes; // 1440 for 1m, requires 24h of data

                var baselines = new List<object>();
                foreach (var name in nameList)
                {
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = $"""
                        SELECT pi.name,
                               AVG(s.cpu_pct_avg) as avg_cpu,
                               SQRT(MAX(0, AVG(s.cpu_pct_avg * s.cpu_pct_avg) - AVG(s.cpu_pct_avg) * AVG(s.cpu_pct_avg))) as stddev_cpu,
                               AVG(s.private_mb_max) as avg_memory_mb,
                               SQRT(MAX(0, AVG(s.private_mb_max * s.private_mb_max) - AVG(s.private_mb_max) * AVG(s.private_mb_max))) as stddev_memory_mb,
                               AVG(s.io_kb_total) as avg_io_kb,
                               SQRT(MAX(0, AVG(s.io_kb_total * s.io_kb_total) - AVG(s.io_kb_total) * AVG(s.io_kb_total))) as stddev_io_kb,
                               COUNT(DISTINCT s.ts) as data_points
                        FROM {table} s
                        JOIN process_instance pi ON pi.id = s.instance_id
                        WHERE pi.name = @name AND s.ts >= @from AND s.ts <= @to
                        GROUP BY pi.name
                        HAVING COUNT(DISTINCT s.ts) >= @minPoints
                        """;
                    cmd.Parameters.AddWithValue("@name", name);
                    cmd.Parameters.AddWithValue("@from", sevenDaysAgo);
                    cmd.Parameters.AddWithValue("@to", now);
                    cmd.Parameters.AddWithValue("@minPoints", minDataPoints);

                    using var reader = cmd.ExecuteReader();
                    if (reader.Read())
                    {
                        long dataPoints = reader.GetInt64(7);
                        double dataHours = dataPoints * intervalMinutes / 60.0;
                        baselines.Add(new
                        {
                            name = reader.GetString(0),
                            avgCpu = Math.Round(reader.IsDBNull(1) ? 0 : reader.GetDouble(1), 2),
                            stddevCpu = Math.Round(reader.IsDBNull(2) ? 0 : reader.GetDouble(2), 2),
                            avgMemoryMb = Math.Round(reader.IsDBNull(3) ? 0 : reader.GetDouble(3), 2),
                            stddevMemoryMb = Math.Round(reader.IsDBNull(4) ? 0 : reader.GetDouble(4), 2),
                            avgIoKb = Math.Round(reader.IsDBNull(5) ? 0 : reader.GetDouble(5), 2),
                            stddevIoKb = Math.Round(reader.IsDBNull(6) ? 0 : reader.GetDouble(6), 2),
                            dataHours = Math.Round(dataHours, 1),
                        });
                    }
                }

                return Results.Json(new { baselines }, jsonOptions);
            }
            catch (SqliteException ex)
            {
                ReportQueryFailure(ex, "/api/baselines");
                return Results.Json(new { baselines = Array.Empty<object>() }, jsonOptions);
            }
        });

        app.MapGet("/api/heatmap", (long? from, long? to, string? metric) =>
        {
            if (from == null || to == null || string.IsNullOrWhiteSpace(metric))
                return Results.BadRequest(new { error = "from, to, and metric parameters are required" });

            string[] validMetrics = ["cpu", "memory", "disk", "network"];
            if (!validMetrics.Contains(metric))
                return Results.BadRequest(new { error = $"metric must be one of: {string.Join(", ", validMetrics)}" });

            try
            {
                using var conn = OpenDb();
                using var checkCmd = conn.CreateCommand();
                checkCmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='machine'";
                if (checkCmd.ExecuteScalar() == null)
                    return Results.Json(new { metric, buckets = Array.Empty<object>() }, jsonOptions);

                var plan = PlanTiers(conn, from.Value, to.Value, isMachine: true);
                TierSource source = TierSql.Source(plan, isMachine: true);

                string metricCol = metric switch
                {
                    "memory" => "memory_avail_mb",
                    "disk" => "disk_busy_pct",
                    "network" => "net_kbps",
                    _ => "cpu_pct"
                };

                // Only CPU and disk busy store a rollup maximum. For the other two the
                // best available peak is still the averaged column, which understates a
                // peak inside a rollup row. That understatement is pre-existing; what
                // matters here is not mixing a raw peak with a rollup average.
                string peakCol = metric switch
                {
                    "disk" => "disk_busy_pct_peak",
                    "cpu" => "cpu_pct_peak",
                    _ => metricCol
                };

                using var cmd = conn.CreateCommand();
                // An hour cell can straddle the retention boundary, so it can hold rows
                // from two tiers even though each is internally uniform.
                cmd.CommandText = $"""
                    SELECT (ts - @from) / 86400000 as day_offset,
                           ((ts % 86400000) / 3600000) as hour,
                           {TierSql.WeightedAvg(metricCol, "avg_val")},
                           MAX({peakCol}) as peak_val,
                           -- Raw samples represented, not rows read, the same change
                           -- made to sampleCount on /api/alerts.
                           SUM(weight) as cnt
                    FROM {source.Sql}
                    WHERE ts >= @from AND ts <= @to
                    GROUP BY day_offset, hour
                    ORDER BY day_offset, hour
                    """;
                AddTierBounds(cmd, source);
                cmd.Parameters.AddWithValue("@from", from.Value);
                cmd.Parameters.AddWithValue("@to", to.Value);

                var buckets = new List<object>();
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    buckets.Add(new
                    {
                        dayOffset = reader.GetInt64(0),
                        hour = reader.GetInt64(1),
                        avg = reader.IsDBNull(2) ? 0.0 : Math.Round(reader.GetDouble(2), 2),
                        peak = reader.IsDBNull(3) ? 0.0 : Math.Round(reader.GetDouble(3), 2),
                        count = reader.GetInt64(4),
                    });
                }

                return Results.Json(new { metric, buckets }, jsonOptions);
            }
            catch (SqliteException ex)
            {
                ReportQueryFailure(ex, "/api/heatmap");
                return Results.Json(new { metric, buckets = Array.Empty<object>() }, jsonOptions);
            }
        });

        app.MapGet("/api/thresholds", () =>
        {
            return Results.Json(new
            {
                system = new
                {
                    cpuElevatedPct = SystemCpuElevatedPct,
                    cpuHighPct = SystemCpuHighPct,
                    memoryHighPct = SystemMemoryHighPct,
                },
                process = new
                {
                    cpuNotablePct = ProcessCpuNotablePct,
                    cpuElevatedPct = ProcessCpuElevatedPct,
                    cpuHighPct = ProcessCpuHighPct,
                    memoryNotableMb = ProcessMemoryNotableMb,
                    memoryHighMb = ProcessMemoryHighMb,
                    ioHeavyKb = ProcessIoHeavyKb,
                    cpuSpikePct = ProcessCpuSpikePct,
                },
            }, jsonOptions);
        });

        app.MapFallback(context =>
        {
            if (!context.Request.Path.StartsWithSegments("/api"))
            {
                context.Request.Path = "/index.html";
                return context.Response.SendFileAsync(
                    app.Environment.WebRootFileProvider.GetFileInfo("index.html"));
            }
            context.Response.StatusCode = 404;
            return Task.CompletedTask;
        });

        return app;
    }

    /// <summary>
    /// The timeline answer for a window nothing can be read from, either because
    /// the capture holds no machine table or because the query failed.
    ///
    /// Both floors come back as zero, which says there is nothing constraining
    /// what a caller may ask for next. That is not a claim that full detail is
    /// available: there are no points at all, so there is nothing to be detailed
    /// about. Shared between the two paths so the shape cannot drift apart.
    /// </summary>
    static object EmptyTimeline(long? requestedBucket) => new
    {
        resolution = "machine",
        bucketMs = 0L,
        bucketRequestMs = requestedBucket,
        minBucketMs = 0L,
        tierFloorMs = 0L,
        points = Array.Empty<object>(),
    };

    static TierPlan PlanTiers(SqliteConnection conn, long from, long to, bool isMachine)
        => TierSelection.Plan(from, to, isMachine, TierCoverageReader.Read(conn, isMachine));

    /// <summary>Binds the slice bounds a tier source reads between.</summary>
    static void AddTierBounds(SqliteCommand cmd, TierSource source)
    {
        foreach (TierBound bound in source.Parameters)
            cmd.Parameters.AddWithValue($"@{bound.Name}", bound.Value);
    }

    static string EscapeLike(string value) =>
        value.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");

    static bool HasTable(SqliteConnection conn, string table)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name=@name";
        cmd.Parameters.AddWithValue("@name", table);
        return cmd.ExecuteScalar() != null;
    }
}
