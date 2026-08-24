namespace Telltale.Viewer;

/// <summary>The span of time a storage tier actually holds rows for.</summary>
public readonly record struct TierCoverage(long MinTs, long MaxTs);

/// <summary>One tier and the portion of the requested window it serves.</summary>
public sealed record TierSlice(string Table, long From, long To);

public sealed record TierPlan(IReadOnlyList<TierSlice> Slices, long Bucket)
{
    public long CoarsestIntervalMs => Slices.Max(s => TierSelection.NativeIntervalMs(s.Table));

    public bool IsSingleRawTier => Slices.Count == 1 && TierSelection.IsRawTable(Slices[0].Table);

    /// <summary>Tables read, oldest first, for the API's `resolution` field.</summary>
    public string Resolution => string.Join(",", Slices.Select(s => s.Table));
}

/// <summary>
/// Chooses storage tiers by which ones overlap the requested window, rather than
/// by how old the window is. The rollup worker only promotes rows once they fall
/// outside the raw retention window, so the tiers are disjoint in time and an
/// age-based rule selects an empty tier at exactly the boundary where the tier
/// it switches away from is the only one holding data.
/// </summary>
public static class TierSelection
{
    const long MaxPoints = 2000;

    static readonly string[] MachineTiers = { "machine", "machine_1m", "machine_10m" };
    static readonly string[] SampleTiers = { "sample", "sample_1m", "sample_10m" };

    /// <summary>Tiers finest-resolution first.</summary>
    public static IReadOnlyList<string> TiersFor(bool isMachine) => isMachine ? MachineTiers : SampleTiers;

    public static bool IsRawTable(string table) => table is "machine" or "sample";

    public static long NativeIntervalMs(string table) =>
        table.EndsWith("_10m", StringComparison.Ordinal) ? 600_000L
        : table.EndsWith("_1m", StringComparison.Ordinal) ? 60_000L
        : 5_000L;

    public static TierPlan Plan(long from, long to, bool isMachine, IReadOnlyDictionary<string, TierCoverage> coverage)
    {
        IReadOnlyList<string> tiers = TiersFor(isMachine);
        var slices = new List<TierSlice>();

        // Finest tier claims what it covers; coarser tiers only fill what is left,
        // so an instant present in two tiers is never counted twice.
        var unclaimed = new List<(long From, long To)>();
        if (to >= from) unclaimed.Add((from, to));

        foreach (string tier in tiers)
        {
            if (unclaimed.Count == 0) break;
            if (!coverage.TryGetValue(tier, out TierCoverage cov)) continue;

            var stillUnclaimed = new List<(long From, long To)>();
            foreach ((long gapFrom, long gapTo) in unclaimed)
            {
                long lo = Math.Max(gapFrom, cov.MinTs);
                long hi = Math.Min(gapTo, cov.MaxTs);
                if (lo > hi)
                {
                    stillUnclaimed.Add((gapFrom, gapTo));
                    continue;
                }
                slices.Add(new TierSlice(tier, lo, hi));
                if (gapFrom < lo) stillUnclaimed.Add((gapFrom, lo - 1));
                if (hi < gapTo) stillUnclaimed.Add((hi + 1, gapTo));
            }
            unclaimed = stillUnclaimed;
        }

        // No tier holds anything in this window: query the raw tier so the caller
        // still gets a well-formed empty result.
        if (slices.Count == 0) slices.Add(new TierSlice(tiers[0], from, to));

        slices.Sort((a, b) => a.From.CompareTo(b.From));
        return new TierPlan(slices, ComputeBucket(from, to, slices));
    }

    static long ComputeBucket(long from, long to, List<TierSlice> slices)
    {
        long rangeMs = to - from;
        long coarsest = slices.Max(s => NativeIntervalMs(s.Table));
        long bucket = 0;

        if (rangeMs > 0 && rangeMs / coarsest > MaxPoints)
            bucket = (rangeMs / MaxPoints / coarsest) * coarsest;

        // Mixed tiers must share one bucket size, otherwise the series would
        // change resolution partway along the chart.
        if (slices.Count > 1 && bucket == 0) bucket = coarsest;

        return bucket;
    }
}
