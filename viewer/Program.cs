using System.Text.Json;
using Microsoft.Data.Sqlite;
using Telltale.Viewer;

Mutex? mutex = null;
var isTestHost = AppDomain.CurrentDomain.GetAssemblies()
    .Any(a => a.GetName().Name == "Microsoft.AspNetCore.Mvc.Testing");
if (!isTestHost)
{
    mutex = new Mutex(true, @"Global\TelltaleViewerInstance", out bool createdNew);
    if (!createdNew)
    {
        mutex.Dispose();
        Console.Error.WriteLine("Another instance of the Telltale viewer is already running.");
        Environment.Exit(1);
        return;
    }
}

try
{
    var builder = WebApplication.CreateBuilder(args);

    var app = builder.Build();

    // No CORS policy: nothing legitimately reaches this API cross-origin. The
    // shipped build serves the frontend from this executable's own wwwroot, and
    // during development Vite proxies /api to the viewer from its own server, so
    // the browser only ever sees its own origin. A policy allowing any origin let
    // any page the user visited read their capture history through their browser.
    app.UseDefaultFiles();
    app.UseStaticFiles();

    var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    // Must be read after builder.Build(): that is when the test factory's
    // configuration override is applied. Hoisting this above the Build call
    // silently sends the tests back to the real user database.
    string dbPath = builder.Configuration["TELLTALE_DB"]
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Telltale", "telltale.db");

    SqliteConnection OpenDb()
    {
        string mode = File.Exists(dbPath) ? "ReadOnly" : "ReadWrite";
        var conn = new SqliteConnection($"Data Source={dbPath};Mode={mode}");
        conn.Open();
        return conn;
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
        catch
        {
            return Results.Json(new { min = (long?)null, max = (long?)null }, jsonOptions);
        }
    });

    app.MapGet("/api/timeline", (long from, long to) =>
    {
        using var conn = OpenDb();
        var plan = PlanTiers(conn, from, to, isMachine: true);
        TierSource source = TierSql.Source(plan, isMachine: true);

        using var cmd = conn.CreateCommand();

        // Raw-only ranges stay unaggregated, as they were before tier selection
        // learned to span tiers.
        //
        // A mixed bucket is at least as wide as the coarsest tier's interval, but
        // the bucket grid is anchored to the epoch while the tier changes over at
        // whatever instant the raw table happens to start. The one bucket holding
        // that instant therefore straddles both tiers, so it is weighted like any
        // other aggregate rather than trusted to be single-tier.
        if (!plan.IsSingleRawTier && plan.Bucket > 0)
        {
            cmd.CommandText = $"""
                SELECT (ts / @bucket) * @bucket as ts,
                       {TierSql.WeightedAvg("cpu_pct", "cpu_pct")},
                       {TierSql.WeightedAvg("memory_avail_mb", "memory_avail_mb")},
                       MAX(commit_mb) as commit_mb, SUM(hard_faults) as hard_faults,
                       {TierSql.WeightedAvg("disk_read_ms", "disk_read_ms")},
                       {TierSql.WeightedAvg("disk_write_ms", "disk_write_ms")},
                       memory_total_mb,
                       {TierSql.WeightedAvg("disk_busy_pct", "disk_busy_pct")},
                       {TierSql.WeightedAvg("net_kbps", "net_kbps")},
                       {TierSql.WeightedAvg("gpu_busy_pct", "gpu_busy_pct")}
                FROM {source.Sql} WHERE ts >= @from AND ts <= @to
                GROUP BY ts / @bucket ORDER BY ts
                """;
            cmd.Parameters.AddWithValue("@bucket", plan.Bucket);
        }
        else
        {
            cmd.CommandText = $"""
                SELECT ts, cpu_pct, memory_avail_mb, commit_mb, hard_faults,
                       disk_read_ms, disk_write_ms, memory_total_mb, disk_busy_pct, net_kbps, gpu_busy_pct
                FROM {source.Sql} WHERE ts >= @from AND ts <= @to ORDER BY ts
                """;
        }

        AddTierBounds(cmd, source);
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
                memoryAvailMb = reader.IsDBNull(2) ? null : (double?)reader.GetDouble(2),
                commitMb = reader.IsDBNull(3) ? null : (double?)reader.GetDouble(3),
                hardFaults = reader.IsDBNull(4) ? null : (int?)reader.GetInt32(4),
                diskReadMs = reader.IsDBNull(5) ? null : (double?)reader.GetDouble(5),
                diskWriteMs = reader.IsDBNull(6) ? null : (double?)reader.GetDouble(6),
                memoryTotalMb = reader.IsDBNull(7) ? null : (double?)reader.GetDouble(7),
                diskBusyPct = reader.IsDBNull(8) ? null : (double?)reader.GetDouble(8),
                netKbps = reader.IsDBNull(9) ? null : (double?)reader.GetDouble(9),
                gpuBusyPct = reader.IsDBNull(10) ? null : (double?)reader.GetDouble(10),
            });
        }

        return Results.Json(new { resolution = plan.Resolution, points }, jsonOptions);
    });

    app.MapGet("/api/processes", (long from, long to, int? limit, string? sort, string? q, bool? group) =>
    {
        using var conn = OpenDb();
        bool grouped = group ?? true;
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
                    {(q != null ? "AND pi.name LIKE @q" : "")}
                    GROUP BY pi.name, s.ts
                ) sub
                GROUP BY sub.name
                ORDER BY {sortExpr} DESC
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
            cmd.CommandText = $"""
                SELECT pi.id, pi.pid, pi.name, pi.path,
                       {TierSql.WeightedAvg("s.cpu_pct", "avg_cpu_pct", "s.weight")},
                       MAX(s.private_mb) as peak_private_mb,
                       SUM(s.io_kb) as total_io_kb
                FROM {source.Sql} s
                JOIN process_instance pi ON pi.id = s.instance_id
                WHERE s.ts >= @from AND s.ts <= @to
                {(q != null ? "AND pi.name LIKE @q" : "")}
                GROUP BY pi.id
                ORDER BY {sortExpr} DESC
                LIMIT @limit
                """;
        }

        AddTierBounds(cmd, source);
        cmd.Parameters.AddWithValue("@from", from);
        cmd.Parameters.AddWithValue("@to", to);
        cmd.Parameters.AddWithValue("@limit", take);
        if (q != null) cmd.Parameters.AddWithValue("@q", $"%{q}%");

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
    });

    app.MapGet("/api/process/{id:long}", (long id, long from, long to) =>
    {
        using var conn = OpenDb();
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
    });

    app.MapGet("/api/process-group/{name}", (string name, long from, long to) =>
    {
        using var conn = OpenDb();
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
        catch
        {
            return Results.Json(new { period, alerts = Array.Empty<object>() }, jsonOptions);
        }
    });

    app.MapGet("/api/health", () =>
    {
        long lastSampleTs = 0;
        double sampleCostMs = 0;
        int processCount = 0;
        int storedCount = 0;

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
        }
        catch { }

        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        bool collectorRunning = lastSampleTs > 0 && (now - lastSampleTs) < 15000;

        long dbSizeBytes = 0;
        try
        {
            var fi = new FileInfo(dbPath);
            if (fi.Exists) dbSizeBytes = fi.Length;
        }
        catch { }

        return Results.Json(new
        {
            collectorRunning,
            lastSampleTs,
            sampleCostMs,
            processCount,
            storedCount,
            dbSizeMb = Math.Round(dbSizeBytes / (1024.0 * 1024.0), 1),
            logicalProcessors = Environment.ProcessorCount,
        }, jsonOptions);
    });

    app.MapGet("/api/baselines", (string? names) =>
    {
        if (string.IsNullOrWhiteSpace(names))
            return Results.Json(new { baselines = Array.Empty<object>() }, jsonOptions);

        var nameList = names.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (nameList.Length == 0)
            return Results.Json(new { baselines = Array.Empty<object>() }, jsonOptions);

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
                           SQRT(AVG(s.cpu_pct_avg * s.cpu_pct_avg) - AVG(s.cpu_pct_avg) * AVG(s.cpu_pct_avg)) as stddev_cpu,
                           AVG(s.private_mb_max) as avg_memory_mb,
                           SQRT(AVG(s.private_mb_max * s.private_mb_max) - AVG(s.private_mb_max) * AVG(s.private_mb_max)) as stddev_memory_mb,
                           AVG(s.io_kb_total) as avg_io_kb,
                           SQRT(AVG(s.io_kb_total * s.io_kb_total) - AVG(s.io_kb_total) * AVG(s.io_kb_total)) as stddev_io_kb,
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
        catch
        {
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
        catch
        {
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

    app.Lifetime.ApplicationStarted.Register(() =>
    {
        var url = app.Urls.FirstOrDefault() ?? "http://localhost:5111";
        if (!app.Environment.IsDevelopment())
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                });
            }
            catch { }
        }
        Console.WriteLine($"Telltale viewer started at {url}");
    });

    app.Run();
}
finally
{
    mutex?.ReleaseMutex();
    mutex?.Dispose();
}

static TierPlan PlanTiers(SqliteConnection conn, long from, long to, bool isMachine)
    => TierSelection.Plan(from, to, isMachine, TierCoverageReader.Read(conn, isMachine));

/// <summary>Binds the slice bounds a tier source reads between.</summary>
static void AddTierBounds(SqliteCommand cmd, TierSource source)
{
    foreach (TierBound bound in source.Parameters)
        cmd.Parameters.AddWithValue($"@{bound.Name}", bound.Value);
}

public partial class Program { }
