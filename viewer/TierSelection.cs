namespace Telltale.Viewer;

/// <summary>The span of time a storage tier actually holds rows for.</summary>
public readonly record struct TierCoverage(long MinTs, long MaxTs);

/// <summary>One tier and the portion of the requested window it serves.</summary>
public sealed record TierSlice(string Table, long From, long To);

public sealed record TierPlan
{
    public TierPlan(IReadOnlyList<TierSlice> slices, long bucket, long? requestedBucket = null)
    {
        if (slices.Count == 0)
            throw new ArgumentException("A tier plan must name at least one slice.", nameof(slices));

        Slices = slices;
        Bucket = bucket;

        // A request of zero or less is not a narrower ask, it is no ask, and the
        // endpoint promises to treat one as absent rather than as an error. It is
        // normalised here so nothing downstream has to repeat the check.
        RequestedBucket = requestedBucket > 0 ? requestedBucket : null;
    }

    public IReadOnlyList<TierSlice> Slices { get; }

    /// <summary>
    /// The bucket this range would be given if nobody asked for one. Still what
    /// the process endpoints read; the timeline reads <see cref="EffectiveBucket"/>.
    /// </summary>
    public long Bucket { get; }

    /// <summary>The bucket the caller asked for, or null if it did not ask for one.</summary>
    public long? RequestedBucket { get; }

    public long CoarsestIntervalMs => Slices.Max(s => TierSelection.NativeIntervalMs(s.Table));

    public bool IsSingleRawTier => Slices.Count == 1 && TierSelection.IsRawTable(Slices[0].Table);

    /// <summary>
    /// True when one raw tier serves the whole window and that window is narrow
    /// enough to hand back every row it holds. Callers that want the raw table's
    /// native detail use this to decide whether to skip bucketing.
    ///
    /// Measured from the slice rather than the requested range, because the slice
    /// is the span actually read: a request wider than the raw table's coverage
    /// is clamped to that coverage, and the rows returned are what has to be
    /// bounded.
    /// </summary>
    public bool ServesFullResolution
    {
        get
        {
            if (!IsSingleRawTier) return false;

            // When no tier covers the window at all, Plan falls back to a slice
            // carrying the caller's own from and to, which are unvalidated. An
            // inverted range gives a negative span, and a range spanning most of
            // long overflows a 64 bit subtraction into one, which would read as
            // narrow enough and wave the widest possible window through. Widening
            // the subtraction keeps both cases honest.
            TierSlice slice = Slices[0];
            if (slice.To < slice.From) return false;

            return (Int128)slice.To - slice.From
                <= (Int128)TierSelection.MaxRawOnlyPoints * TierSelection.NativeIntervalMs(slice.Table);
        }
    }

    /// <summary>
    /// The span actually read, widened for the same overflow reason as
    /// <see cref="ServesFullResolution"/>. Slices are sorted and disjoint, so the
    /// first slice's start and the last slice's end bound the whole read.
    /// </summary>
    Int128 ReadSpanMs => (Int128)Slices[^1].To - Slices[0].From;

    /// <summary>
    /// The width the timeline query groups by, where <c>0</c> means hand back
    /// rows exactly as they are stored.
    ///
    /// This is the one number the query branches on. Without a request it
    /// reproduces what the endpoint has always done: full resolution where the
    /// window earns it, the automatic bucket otherwise. With a request it is that
    /// request, clamped to something the tiers can actually serve.
    /// </summary>
    public long EffectiveBucket => RequestedBucket is long wanted
        ? Clamp(wanted)
        : ServesFullResolution ? 0 : Bucket;

    /// <summary>
    /// The finest bucket this window could be served at, which is what a request
    /// for an impossibly small one clamps to. <c>0</c> means the window can be
    /// served at full stored resolution.
    ///
    /// The frontend needs this to know which granularity options to offer. It
    /// cannot work the answer out for itself, because only the database knows
    /// which tiers still hold the window.
    /// </summary>
    public long SmallestServableBucket => Clamp(1);

    /// <summary>
    /// The finest interval the tiers serving this window store, ignoring how many
    /// points that would come to.
    ///
    /// Paired with <see cref="SmallestServableBucket"/> it separates the two
    /// reasons a bucket can be refused: below this one the recording no longer
    /// holds that detail, between the two the window is simply too wide to return
    /// that many points. A caller cannot work the difference out for itself, and
    /// the two want different words in front of a person.
    /// </summary>
    public long TierFloorMs => CoarsestIntervalMs;

    /// <summary>
    /// Brings a requested bucket to something the selected tiers can serve. It
    /// widens, never narrows, so a request is answered with at least the detail
    /// asked for or the closest the recording still holds.
    /// </summary>
    long Clamp(long wanted)
    {
        long coarsest = CoarsestIntervalMs;

        // Down to a whole tier interval, then floored at one. A bucket that
        // straddled part of a rollup row would average a fraction of a row against
        // whole ones, and no tier can be divided finer than the rows it stores.
        long bucket = Math.Max(coarsest, wanted - wanted % coarsest);

        // Then up, if that many points would be more than one response carries.
        long capFloor = TierSelection.SmallestBucketWithinCap(ReadSpanMs, coarsest);
        if (bucket < capFloor) bucket = capFloor;

        // A single raw tier asked for no more detail than it stores is the
        // unbucketed path. The cap has already been applied, so a bucket still
        // this small also means the window is inside the raw-only exemption.
        if (IsSingleRawTier && bucket <= coarsest) return 0;

        return bucket;
    }

    /// <summary>
    /// Tables read, oldest first, for the API's `resolution` field. A tier can
    /// serve more than one slice, so names are de-duplicated.
    /// </summary>
    public string Resolution => string.Join(",", Slices.Select(s => s.Table).Distinct());
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
    /// <summary>
    /// How many points a bucketed range aims for. The bucket is rounded down to a
    /// whole tier interval, so a range can come back with up to twice this.
    /// </summary>
    public const long MaxPoints = 2000;

    /// <summary>
    /// The most points a raw-only window may be worth before it is bucketed like
    /// any other range, and, since granularity became selectable, the ceiling on
    /// what an explicitly requested bucket may produce as well. A caller naming
    /// its own bucket must not be a way past the bound the automatic choice
    /// respects, and the number that bounds the widest automatic response is the
    /// natural one to bound a requested one by too.
    ///
    /// Raw-only windows are exempt from <c>MaxPoints</c> so the machine timeline
    /// keeps the 5 second detail the day view exists to show. Left unbounded,
    /// that exemption scales with the raw retention setting: at the maximum
    /// <c>RawRetentionHours</c> of 168 a week-long request is served entirely
    /// from the raw table and would return roughly 121,000 points in one
    /// response. This is the bound on the exemption, not a second cap.
    ///
    /// 20,000 covers a day of 5 second samples (17,280) with room for the 25
    /// hour day the clocks going back produces (18,000), so the day view holds
    /// its resolution all year.
    ///
    /// It is counted at <see cref="NativeIntervalMs"/>, which is a fixed 5,000ms
    /// for the raw tables, so in practice this is a bound on how wide the window
    /// may be: about 27.8 hours. The collector's <c>IntervalSeconds</c> is
    /// configurable from 2 to 60, and the viewer has no way to read it without
    /// crossing the boundary that keeps the two executables independent, so at a
    /// finer setting the same window carries proportionally more rows. At the
    /// finest setting of 2 seconds the widest exempt window holds about 50,000.
    /// That is still bounded and still independent of retention, which is what
    /// this constant is for, but it is not literally a count of rows returned.
    /// </summary>
    public const long MaxRawOnlyPoints = 20_000;

    // Finest first, which is the order Plan claims coverage in. The collector keeps
    // its own copy of this ladder in StorageTiers, deliberately: collector and
    // viewer must not reference each other, and schema.sql stays the only contract
    // between them. A tier added there has to be added here too, and a database
    // written by a build that has one this one has not heard of is read without
    // error, because a tier absent from this list is simply never selected from.
    static readonly string[] MachineTiers =
        { "machine", "machine_1m", "machine_10m", "machine_1h", "machine_1d", "machine_1w" };

    static readonly string[] SampleTiers =
        { "sample", "sample_1m", "sample_10m", "sample_1h", "sample_1d", "sample_1w" };

    /// <summary>Tiers finest-resolution first.</summary>
    public static IReadOnlyList<string> TiersFor(bool isMachine) => isMachine ? MachineTiers : SampleTiers;

    public static bool IsRawTable(string table) => table is "machine" or "sample";

    /// <summary>
    /// How wide one stored row of <paramref name="table"/> is.
    /// </summary>
    /// <remarks>
    /// Matched on the whole suffix rather than as a chain of EndsWith tests, so
    /// that adding a tier cannot silently fall through to the raw interval the way
    /// a missing case in an ordered chain would. Anything unrecognised is the raw
    /// table, which has no suffix.
    /// </remarks>
    public static long NativeIntervalMs(string table)
    {
        int underscore = table.LastIndexOf('_');
        string suffix = underscore < 0 ? string.Empty : table[(underscore + 1)..];

        return suffix switch
        {
            "1m" => 60_000L,
            "10m" => 600_000L,
            "1h" => 3_600_000L,
            "1d" => 86_400_000L,
            "1w" => 604_800_000L,
            _ => 5_000L,
        };
    }

    /// <summary>
    /// Chooses the tiers for a window. <paramref name="requestedBucketMs"/> is the
    /// bucket the caller asked for, if any; the plan clamps it to something the
    /// selected tiers can serve rather than refusing it.
    /// </summary>
    public static TierPlan Plan(long from, long to, bool isMachine, IReadOnlyDictionary<string, TierCoverage> coverage,
        long? requestedBucketMs = null)
    {
        IReadOnlyList<string> tiers = TiersFor(isMachine);
        var slices = new List<TierSlice>();

        // Finest tier claims what it covers; coarser tiers only fill what is left,
        // so an instant present in two tiers is never counted twice.
        var unclaimed = new List<(long From, long To)>();
        if (to >= from) unclaimed.Add((from, to));

        // Coverage is a tier's outer extent, not a record of which instants it
        // holds, so a finer tier also claims any hole inside its own extent.
        // That is safe because the rollup worker promotes oldest-first and
        // deletes as it goes, which only ever moves a tier's lower bound
        // forward and never punches a hole in the middle of one.
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
        return new TierPlan(slices, ComputeBucket(from, to, slices), requestedBucketMs);
    }

    /// <summary>
    /// The smallest bucket that divides <paramref name="spanMs"/> into no more
    /// than <see cref="MaxRawOnlyPoints"/> points, rounded up to a whole
    /// <paramref name="tierInterval"/>. Zero for an empty or inverted span, which
    /// leaves the caller's own floor standing.
    ///
    /// The span arrives as <see cref="Int128"/> because a window spanning most of
    /// long overflows a 64 bit subtraction. The result cannot: the widest span two
    /// longs can produce is about 1.8e19, and divided by 20,000 that is about
    /// 9.2e14, four orders of magnitude inside long.
    /// </summary>
    public static long SmallestBucketWithinCap(Int128 spanMs, long tierInterval)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(tierInterval);

        if (spanMs <= 0) return 0;

        Int128 perPoint = (spanMs + MaxRawOnlyPoints - 1) / MaxRawOnlyPoints;
        return (long)(((perPoint + tierInterval - 1) / tierInterval) * tierInterval);
    }

    static long ComputeBucket(long from, long to, List<TierSlice> slices)
    {
        // Widened for the same reason as TierPlan.ServesFullResolution: from and
        // to reach here unvalidated, and a range spanning most of long overflows
        // a 64 bit subtraction into a negative number. That reads as a range too
        // narrow to bucket, which would leave the widest request expressible as
        // the one that comes back unaggregated. The widest bucket this can
        // produce is about 9.2e15, so the result still fits a long.
        Int128 rangeMs = (Int128)to - from;
        long coarsest = slices.Max(s => NativeIntervalMs(s.Table));
        long bucket = 0;

        if (rangeMs > 0 && rangeMs / coarsest > MaxPoints)
            bucket = (long)(rangeMs / MaxPoints / coarsest) * coarsest;

        // Mixed tiers must share one bucket size, otherwise the series would
        // change resolution partway along the chart.
        if (slices.Count > 1 && bucket == 0) bucket = coarsest;

        return bucket;
    }
}
