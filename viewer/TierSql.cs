namespace Telltale.Viewer;

/// <summary>A slice bound bound to a SQL parameter rather than interpolated.</summary>
public sealed record TierBound(string Name, long Value);

/// <summary>
/// The SQL fragment a query reads FROM, together with the parameters its slice
/// bounds are bound to. Callers add <see cref="Parameters"/> to the command.
/// </summary>
public sealed record TierSource(string Sql, IReadOnlyList<TierBound> Parameters);

/// <summary>
/// Turns a <see cref="TierPlan"/> into the SQL fragment a query reads FROM.
/// Rollup tiers store aggregated columns under different names, so each tier's
/// projection is aliased to the raw table's column names. Callers then write one
/// query against a uniform shape regardless of which tiers were selected.
/// </summary>
public static class TierSql
{
    const string MachineRawColumns =
        "ts, cpu_pct, memory_avail_mb, commit_mb, hard_faults, disk_read_ms, disk_write_ms, " +
        "memory_total_mb, disk_busy_pct, net_kbps, gpu_busy_pct";

    const string MachineRollupColumns =
        "ts, cpu_pct_avg AS cpu_pct, memory_avail_mb_avg AS memory_avail_mb, " +
        "commit_mb_max AS commit_mb, hard_faults_total AS hard_faults, " +
        "disk_read_ms_avg AS disk_read_ms, disk_write_ms_avg AS disk_write_ms, " +
        "memory_total_mb, disk_busy_pct_avg AS disk_busy_pct, " +
        "net_kbps_avg AS net_kbps, gpu_busy_pct_avg AS gpu_busy_pct";

    const string SampleRawColumns =
        "ts, instance_id, cpu_pct, private_mb, working_set_mb, io_kb";

    const string SampleRollupColumns =
        "ts, instance_id, cpu_pct_avg AS cpu_pct, private_mb_max AS private_mb, " +
        "working_set_mb_max AS working_set_mb, io_kb_total AS io_kb";

    /// <summary>
    /// A bare table name when one raw tier serves the whole window, otherwise a
    /// parenthesised UNION ALL over the selected tiers. Both are valid directly
    /// after FROM and can be given an alias. Slice bounds are parameterised, so
    /// no request-derived value is ever concatenated into the statement.
    /// </summary>
    public static TierSource Source(TierPlan plan, bool isMachine)
    {
        if (plan.IsSingleRawTier)
            return new TierSource(plan.Slices[0].Table, Array.Empty<TierBound>());

        var parameters = new List<TierBound>(plan.Slices.Count * 2);
        var parts = new List<string>(plan.Slices.Count);

        for (int i = 0; i < plan.Slices.Count; i++)
        {
            TierSlice slice = plan.Slices[i];
            string fromName = $"tier{i}From";
            string toName = $"tier{i}To";

            parts.Add($"SELECT {Projection(slice.Table, isMachine)} FROM {slice.Table} " +
                      $"WHERE ts >= @{fromName} AND ts <= @{toName}");

            parameters.Add(new TierBound(fromName, slice.From));
            parameters.Add(new TierBound(toName, slice.To));
        }

        return new TierSource("(" + string.Join(" UNION ALL ", parts) + ")", parameters);
    }

    static string Projection(string table, bool isMachine) => (isMachine, TierSelection.IsRawTable(table)) switch
    {
        (true, true) => MachineRawColumns,
        (true, false) => MachineRollupColumns,
        (false, true) => SampleRawColumns,
        (false, false) => SampleRollupColumns,
    };
}
