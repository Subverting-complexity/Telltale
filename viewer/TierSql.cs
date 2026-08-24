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
///
/// A row's <c>weight</c> carries how much time it stands for, because a raw row
/// covers about five seconds where a 1 minute rollup row covers sixty. An
/// aggregate that spans tiers has to weight by it, or the finer tier dominates
/// the answer purely by having contributed more rows.
///
/// Peak-oriented queries read the <c>_peak</c> columns rather than taking
/// <c>MAX()</c> of the averaged ones, so a maximum over a mixed range compares
/// like with like instead of raw peaks against rollup averages.
/// </summary>
public static class TierSql
{
    const string MachineRawColumns =
        "ts, cpu_pct, memory_avail_mb, commit_mb, hard_faults, disk_read_ms, disk_write_ms, " +
        "memory_total_mb, disk_busy_pct, net_kbps, gpu_busy_pct, " +
        "cpu_pct AS cpu_pct_peak, disk_busy_pct AS disk_busy_pct_peak, 1 AS weight";

    const string MachineRollupColumns =
        "ts, cpu_pct_avg AS cpu_pct, memory_avail_mb_avg AS memory_avail_mb, " +
        "commit_mb_max AS commit_mb, hard_faults_total AS hard_faults, " +
        "disk_read_ms_avg AS disk_read_ms, disk_write_ms_avg AS disk_write_ms, " +
        "memory_total_mb, disk_busy_pct_avg AS disk_busy_pct, " +
        "net_kbps_avg AS net_kbps, gpu_busy_pct_avg AS gpu_busy_pct, " +
        "cpu_pct_max AS cpu_pct_peak, disk_busy_pct_max AS disk_busy_pct_peak, " +
        "COALESCE(sample_count, 1) AS weight";

    const string SampleRawColumns =
        "ts, instance_id, cpu_pct, private_mb, working_set_mb, io_kb, " +
        "cpu_pct AS cpu_pct_peak, 1 AS weight";

    const string SampleRollupColumns =
        "ts, instance_id, cpu_pct_avg AS cpu_pct, private_mb_max AS private_mb, " +
        "working_set_mb_max AS working_set_mb, io_kb_total AS io_kb, " +
        "cpu_pct_max AS cpu_pct_peak, COALESCE(sample_count, 1) AS weight";

    /// <summary>
    /// A time-weighted mean of <paramref name="column"/>, for queries whose range
    /// can span tiers. Plain AVG() over a mixed range answers "the mean of the rows
    /// we happen to hold", which is not the mean of the period.
    ///
    /// <paramref name="weight"/> names the weighting column, which an inner query
    /// that has already grouped rows may have carried through under its own name.
    ///
    /// A row whose value is NULL is left out of both halves, matching AVG(). Only
    /// dropping it from the numerator would count its weight against a value that
    /// was never measured and pull the mean toward zero, and the collector stores
    /// a sample precisely when CPU could not be computed, so those rows are common.
    ///
    /// Both arguments are interpolated, so callers must pass literals. No
    /// request-derived value may reach either, the same constraint
    /// <see cref="Source"/> keeps for slice bounds.
    /// </summary>
    public static string WeightedAvgExpr(string column, string weight = "weight") =>
        $"SUM({column} * 1.0 * {weight}) / " +
        $"NULLIF(SUM(CASE WHEN {column} IS NULL THEN 0 ELSE {weight} END), 0)";

    /// <summary>
    /// <see cref="WeightedAvgExpr"/> with an alias, for a select list. HAVING and
    /// ORDER BY have to repeat the expression instead, since neither can refer to
    /// a select alias in every SQLite version the viewer runs against.
    /// </summary>
    public static string WeightedAvg(string column, string alias, string weight = "weight") =>
        $"{WeightedAvgExpr(column, weight)} AS {alias}";

    /// <summary>
    /// Inner half of a two-stage average, for the endpoints that total a group
    /// across its instances at one instant before averaging over time. The total
    /// is scaled by the row's weight here so the outer half only has to divide.
    /// </summary>
    public static string WeightedTotal(string column, string alias, string weight = "weight") =>
        $"SUM({column} * 1.0 * {weight}) AS {alias}";

    /// <summary>
    /// Weight of one instant, for the same two-stage shape. Instances at one
    /// timestamp in one tier cover the same span, but an instance that started or
    /// exited mid-bucket carries a smaller count than its siblings, and the
    /// instant is still worth the widest coverage any of them saw.
    /// </summary>
    public static string InstantWeight(string weight = "weight") => $"MAX({weight})";

    /// <summary>
    /// Outer half of the two-stage average: the totals arrive already weighted, so
    /// this only divides.
    ///
    /// An instant where nothing was measured carries a NULL total, and it has to
    /// leave the divisor as well, for the same reason a NULL row does in
    /// <see cref="WeightedAvgExpr"/>. Dropping it from the numerator alone would
    /// charge its weight against a total that was never taken, and a process's
    /// first observation is exactly such an instant.
    /// </summary>
    public static string AvgOfWeightedTotalsExpr(string total, string weight) =>
        $"SUM({total}) / NULLIF(SUM(CASE WHEN {total} IS NULL THEN 0 ELSE {weight} END), 0)";

    /// <summary><see cref="AvgOfWeightedTotalsExpr"/> with an alias, for a select list.</summary>
    public static string AvgOfWeightedTotals(string total, string weight, string alias) =>
        $"{AvgOfWeightedTotalsExpr(total, weight)} AS {alias}";

    /// <summary>
    /// A parenthesised UNION ALL over the selected tiers, valid directly after
    /// FROM and able to take an alias. Slice bounds are parameterised, so no
    /// request-derived value is ever concatenated into the statement.
    ///
    /// Even a single raw tier is projected rather than named bare, so that
    /// <c>weight</c> and the <c>_peak</c> columns exist whichever tiers were
    /// chosen and callers never branch on the tier layout.
    /// </summary>
    public static TierSource Source(TierPlan plan, bool isMachine)
    {
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
