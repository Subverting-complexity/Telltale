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

/// <summary>
/// A timeline answer and the granularity behind it.
///
/// <c>0</c> appears in two of these and does not mean the same thing twice. In
/// <see cref="BucketMs"/> it says the points were not aggregated at all; in
/// <see cref="MinBucketMs"/> and <see cref="TierFloorMs"/> it says there is no
/// floor to speak of, which is the ordinary reading of the number rather than a
/// sentinel.
/// </summary>
/// <param name="Resolution">The tables read, oldest first.</param>
/// <param name="BucketMs">
/// The width each point covers. <c>0</c> means the points are the stored samples
/// themselves rather than an aggregate of them.
/// </param>
/// <param name="BucketRequestMs">
/// What the caller asked for, or null if it asked for nothing. A caller compares
/// it against <paramref name="BucketMs"/> to see whether it got what it wanted.
/// </param>
/// <param name="MinBucketMs">
/// The finest this window could have been served at, whichever limit bound it.
/// </param>
/// <param name="TierFloorMs">
/// The finest interval the tiers themselves store. Below this the recording no
/// longer holds the detail; between this and <paramref name="MinBucketMs"/> the
/// window is merely too wide to return that many points.
/// </param>
/// <param name="Points">The series itself.</param>
public sealed record TimelineResult(
    string Resolution,
    long BucketMs,
    long? BucketRequestMs,
    long MinBucketMs,
    long TierFloorMs,
    IReadOnlyList<TimelinePoint> Points);

public static class TimelineQuery
{
    /// <summary>The raw machine tier, which is also the resolution an empty answer names.</summary>
    const string RawTier = "machine";

    /// <summary>
    /// Reads the machine timeline for a window. <paramref name="requestedBucketMs"/>
    /// is the granularity the caller asked for, if any; the plan clamps it to
    /// something the tiers holding this window can serve.
    /// </summary>
    public static TimelineResult Execute(SqliteConnection conn, long from, long to, long? requestedBucketMs = null)
    {
        TierCoverageSet tiers = TierCoverageReader.ReadSet(conn, isMachine: true);

        // A database with no machine table has no timeline to read, and querying
        // one that is not there would throw rather than answer. The check lives
        // here rather than at the endpoint because the read above has already
        // asked sqlite_master which tables exist, and asking twice was the whole
        // cost of a check standing on its own.
        //
        // The requested width is normalised the way TierPlan normalises it, so a
        // caller cannot tell the two paths apart by what the response echoes back.
        if (!tiers.Has(RawTier))
            return new TimelineResult(RawTier, 0, requestedBucketMs > 0 ? requestedBucketMs : null,
                0, 0, Array.Empty<TimelinePoint>());

        var plan = TierSelection.Plan(from, to, isMachine: true, tiers.Coverage, requestedBucketMs);
        TierSource source = TierSql.Source(plan, isMachine: true);

        // One number decides the shape of the query. Whether it came from the
        // caller or from the range is already settled by the plan.
        long bucket = plan.EffectiveBucket;

        using var cmd = conn.CreateCommand();

        if (bucket > 0)
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
            cmd.Parameters.AddWithValue("@bucket", bucket);
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

        return new TimelineResult(
            plan.Resolution, bucket, plan.RequestedBucket,
            plan.SmallestServableBucket, plan.TierFloorMs, points);
    }
}
