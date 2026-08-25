using Microsoft.Data.Sqlite;

namespace Telltale.Viewer;

public sealed record TimelinePoint(
    long Ts,
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

public sealed record TimelineResult(string Resolution, IReadOnlyList<TimelinePoint> Points);

public static class TimelineQuery
{
    public static TimelineResult Execute(SqliteConnection conn, long from, long to)
    {
        var plan = TierSelection.Plan(from, to, isMachine: true,
            TierCoverageReader.Read(conn, isMachine: true));
        TierSource source = TierSql.Source(plan, isMachine: true);

        using var cmd = conn.CreateCommand();

        if (!plan.ServesFullResolution && plan.Bucket > 0)
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

        foreach (TierBound bound in source.Parameters)
            cmd.Parameters.AddWithValue($"@{bound.Name}", bound.Value);
        cmd.Parameters.AddWithValue("@from", from);
        cmd.Parameters.AddWithValue("@to", to);

        var points = new List<TimelinePoint>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            points.Add(new TimelinePoint(
                Ts: reader.GetInt64(0),
                CpuPct: reader.IsDBNull(1) ? null : reader.GetDouble(1),
                MemoryAvailMb: reader.IsDBNull(2) ? null : reader.GetDouble(2),
                CommitMb: reader.IsDBNull(3) ? null : reader.GetDouble(3),
                HardFaults: reader.IsDBNull(4) ? null : reader.GetInt32(4),
                DiskReadMs: reader.IsDBNull(5) ? null : reader.GetDouble(5),
                DiskWriteMs: reader.IsDBNull(6) ? null : reader.GetDouble(6),
                MemoryTotalMb: reader.IsDBNull(7) ? null : reader.GetDouble(7),
                DiskBusyPct: reader.IsDBNull(8) ? null : reader.GetDouble(8),
                NetKbps: reader.IsDBNull(9) ? null : reader.GetDouble(9),
                GpuBusyPct: reader.IsDBNull(10) ? null : reader.GetDouble(10)));
        }

        return new TimelineResult(plan.Resolution, points);
    }
}
