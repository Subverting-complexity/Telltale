using System.Text.Json;
using Microsoft.Data.Sqlite;

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
    builder.Services.AddCors(options =>
        options.AddDefaultPolicy(policy =>
            policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

    var app = builder.Build();
    app.UseCors();
    app.UseDefaultFiles();
    app.UseStaticFiles();

    var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    string dbPath = Environment.GetEnvironmentVariable("TELLTALE_DB")
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
        var (table, bucket) = SelectTier(from, to, isMachine: true);

        using var cmd = conn.CreateCommand();
        if (table == "machine")
        {
            cmd.CommandText = $"""
                SELECT ts, cpu_pct, memory_avail_mb, commit_mb, hard_faults,
                       disk_read_ms, disk_write_ms, memory_total_mb, disk_busy_pct, net_kbps, gpu_busy_pct
                FROM machine WHERE ts >= @from AND ts <= @to ORDER BY ts
                """;
        }
        else
        {
            string cpuAvg = table == "machine" ? "cpu_pct" : "cpu_pct_avg";
            string memAvg = table == "machine" ? "memory_avail_mb" : "memory_avail_mb_avg";
            string memTotal = "memory_total_mb";
            string diskBusyAvg = table == "machine" ? "disk_busy_pct" : "disk_busy_pct_avg";
            string netAvg = table == "machine" ? "net_kbps" : "net_kbps_avg";

            if (bucket > 0)
            {
                cmd.CommandText = $"""
                    SELECT (ts / @bucket) * @bucket as ts,
                           AVG({cpuAvg}) as cpu_pct, AVG({memAvg}) as memory_avail_mb,
                           MAX(commit_mb_max) as commit_mb, SUM(hard_faults_total) as hard_faults,
                           AVG(disk_read_ms_avg) as disk_read_ms, AVG(disk_write_ms_avg) as disk_write_ms,
                           {memTotal}, AVG({diskBusyAvg}) as disk_busy_pct,
                           AVG({netAvg}) as net_kbps, AVG(gpu_busy_pct_avg) as gpu_busy_pct
                    FROM {table} WHERE ts >= @from AND ts <= @to
                    GROUP BY ts / @bucket ORDER BY ts
                    """;
                cmd.Parameters.AddWithValue("@bucket", bucket);
            }
            else
            {
                cmd.CommandText = $"""
                    SELECT ts, {cpuAvg} as cpu_pct, {memAvg} as memory_avail_mb,
                           commit_mb_max as commit_mb, hard_faults_total as hard_faults,
                           disk_read_ms_avg as disk_read_ms, disk_write_ms_avg as disk_write_ms,
                           {memTotal}, {diskBusyAvg} as disk_busy_pct,
                           {netAvg} as net_kbps, gpu_busy_pct_avg as gpu_busy_pct
                    FROM {table} WHERE ts >= @from AND ts <= @to ORDER BY ts
                    """;
            }
        }

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

        return Results.Json(new { resolution = table, points }, jsonOptions);
    });

    app.MapGet("/api/processes", (long from, long to, int? limit, string? sort, string? q, bool? group) =>
    {
        using var conn = OpenDb();
        bool grouped = group ?? true;
        int take = Math.Clamp(limit ?? 50, 1, 500);
        string sortCol = sort switch
        {
            "memory" => "total_private_mb",
            "io" => "total_io_kb",
            "name" => "name",
            _ => "total_cpu_pct"
        };

        var (table, _) = SelectTier(from, to, isMachine: false);
        string cpuCol = table == "sample" ? "cpu_pct" : "cpu_pct_avg";
        string memCol = table == "sample" ? "private_mb" : "private_mb_max";
        string ioCol = table == "sample" ? "io_kb" : "io_kb_total";

        using var cmd = conn.CreateCommand();

        if (grouped)
        {
            cmd.CommandText = $"""
                SELECT pi.name,
                       SUM(s.{cpuCol}) as total_cpu_pct,
                       SUM(s.{memCol}) as total_private_mb,
                       SUM(s.{ioCol}) as total_io_kb,
                       COUNT(DISTINCT s.instance_id) as instance_count,
                       (SELECT pi2.path FROM process_instance pi2 WHERE pi2.name = pi.name AND pi2.path IS NOT NULL LIMIT 1) as path
                FROM {table} s
                JOIN process_instance pi ON pi.id = s.instance_id
                WHERE s.ts >= @from AND s.ts <= @to
                {(q != null ? "AND pi.name LIKE @q" : "")}
                GROUP BY pi.name
                ORDER BY {sortCol} DESC
                LIMIT @limit
                """;
        }
        else
        {
            cmd.CommandText = $"""
                SELECT pi.id, pi.pid, pi.name, pi.path,
                       AVG(s.{cpuCol}) as total_cpu_pct,
                       MAX(s.{memCol}) as total_private_mb,
                       SUM(s.{ioCol}) as total_io_kb
                FROM {table} s
                JOIN process_instance pi ON pi.id = s.instance_id
                WHERE s.ts >= @from AND s.ts <= @to
                {(q != null ? "AND pi.name LIKE @q" : "")}
                GROUP BY pi.id
                ORDER BY {sortCol} DESC
                LIMIT @limit
                """;
        }

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
        var (table, bucket) = SelectTier(from, to, isMachine: false);
        string cpuCol = table == "sample" ? "cpu_pct" : "cpu_pct_avg";
        string memCol = table == "sample" ? "private_mb" : "private_mb_max";
        string wsCol = table == "sample" ? "working_set_mb" : "working_set_mb_max";
        string ioCol = table == "sample" ? "io_kb" : "io_kb_total";

        using var cmd = conn.CreateCommand();
        if (bucket > 0)
        {
            cmd.CommandText = $"""
                SELECT (s.ts / @bucket) * @bucket as ts,
                       AVG(s.{cpuCol}) as cpu_pct, MAX(s.{memCol}) as private_mb,
                       MAX(s.{wsCol}) as working_set_mb, SUM(s.{ioCol}) as io_kb
                FROM {table} s
                WHERE s.instance_id = @id AND s.ts >= @from AND s.ts <= @to
                GROUP BY s.ts / @bucket ORDER BY ts
                """;
            cmd.Parameters.AddWithValue("@bucket", bucket);
        }
        else
        {
            cmd.CommandText = $"""
                SELECT s.ts, s.{cpuCol} as cpu_pct, s.{memCol} as private_mb,
                       s.{wsCol} as working_set_mb, s.{ioCol} as io_kb
                FROM {table} s
                WHERE s.instance_id = @id AND s.ts >= @from AND s.ts <= @to
                ORDER BY s.ts
                """;
        }

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

        return Results.Json(new { info, resolution = table, points }, jsonOptions);
    });

    app.MapGet("/api/process-group/{name}", (string name, long from, long to) =>
    {
        using var conn = OpenDb();
        var (table, bucket) = SelectTier(from, to, isMachine: false);
        string cpuCol = table == "sample" ? "cpu_pct" : "cpu_pct_avg";
        string memCol = table == "sample" ? "private_mb" : "private_mb_max";
        string wsCol = table == "sample" ? "working_set_mb" : "working_set_mb_max";
        string ioCol = table == "sample" ? "io_kb" : "io_kb_total";

        long effectiveBucket = bucket > 0 ? bucket : 5000;

        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT (s.ts / @bucket) * @bucket as ts,
                   SUM(s.{cpuCol}) as cpu_pct, SUM(s.{memCol}) as private_mb,
                   SUM(s.{wsCol}) as working_set_mb, SUM(s.{ioCol}) as io_kb,
                   COUNT(DISTINCT s.instance_id) as instance_count
            FROM {table} s
            JOIN process_instance pi ON pi.id = s.instance_id
            WHERE pi.name = @name AND s.ts >= @from AND s.ts <= @to
            GROUP BY s.ts / @bucket ORDER BY ts
            """;

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

        return Results.Json(new { name, resolution = table, points }, jsonOptions);
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

            var (table, _) = SelectTier(from, now, isMachine: false);
            string cpuCol = table == "sample" ? "cpu_pct" : "cpu_pct_avg";
            string memCol = table == "sample" ? "private_mb" : "private_mb_max";
            string ioCol = table == "sample" ? "io_kb" : "io_kb_total";

            cmd.CommandText = $"""
                SELECT pi.name,
                       AVG(s.{cpuCol}) as avg_cpu,
                       MAX(s.{cpuCol}) as peak_cpu,
                       MAX(s.{memCol}) as peak_memory_mb,
                       SUM(s.{ioCol}) as total_io_kb,
                       COUNT(*) as sample_count,
                       COUNT(DISTINCT s.instance_id) as instance_count,
                       MIN(s.ts) as first_ts,
                       MAX(s.ts) as last_ts
                FROM {table} s
                JOIN process_instance pi ON pi.id = s.instance_id
                WHERE s.ts >= @from AND s.ts <= @to
                GROUP BY pi.name
                HAVING AVG(s.{cpuCol}) > {ProcessCpuNotablePct} OR MAX(s.{memCol}) > {ProcessMemoryNotableMb}
                ORDER BY AVG(s.{cpuCol}) DESC
                LIMIT 50
                """;

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
                    sampleCount = reader.GetInt32(5),
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

            var (table, _) = SelectTier(from.Value, to.Value, isMachine: true);

            string metricCol = metric switch
            {
                "cpu" => table == "machine" ? "cpu_pct" : "cpu_pct_avg",
                "memory" => table == "machine" ? "memory_avail_mb" : "memory_avail_mb_avg",
                "disk" => table == "machine" ? "disk_busy_pct" : "disk_busy_pct_avg",
                "network" => table == "machine" ? "net_kbps" : "net_kbps_avg",
                _ => "cpu_pct"
            };

            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                SELECT (ts - @from) / 86400000 as day_offset,
                       ((ts % 86400000) / 3600000) as hour,
                       AVG({metricCol}) as avg_val,
                       MAX({metricCol}) as peak_val,
                       COUNT(*) as cnt
                FROM {table}
                WHERE ts >= @from AND ts <= @to
                GROUP BY day_offset, hour
                ORDER BY day_offset, hour
                """;
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

static (string table, long bucket) SelectTier(long from, long to, bool isMachine)
{
    long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    long rangeMs = to - from;
    long ageMs = now - from;

    long oneDay = 86_400_000L;
    long sevenDays = 7 * oneDay;

    string table;
    if (ageMs <= oneDay)
        table = isMachine ? "machine" : "sample";
    else if (ageMs <= sevenDays)
        table = isMachine ? "machine_1m" : "sample_1m";
    else
        table = isMachine ? "machine_10m" : "sample_10m";

    long bucket = 0;
    long maxPoints = 2000;
    long rawInterval = table.Contains("10m") ? 600_000 : table.Contains("1m") ? 60_000 : 5_000;
    long estimatedPoints = rangeMs / rawInterval;

    if (estimatedPoints > maxPoints)
        bucket = (rangeMs / maxPoints / rawInterval) * rawInterval;

    return (table, bucket);
}

public partial class Program { }
