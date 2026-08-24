namespace Telltale.Viewer;

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
    /// after FROM and can be given an alias.
    /// </summary>
    public static string Source(TierPlan plan, bool isMachine)
    {
        if (plan.IsSingleRawTier) return plan.Slices[0].Table;

        var parts = plan.Slices.Select(slice =>
            $"SELECT {Projection(slice.Table, isMachine)} FROM {slice.Table} " +
            $"WHERE ts >= {slice.From} AND ts <= {slice.To}");

        return "(" + string.Join(" UNION ALL ", parts) + ")";
    }

    static string Projection(string table, bool isMachine) => (isMachine, TierSelection.IsRawTable(table)) switch
    {
        (true, true) => MachineRawColumns,
        (true, false) => MachineRollupColumns,
        (false, true) => SampleRawColumns,
        (false, false) => SampleRollupColumns,
    };
}
