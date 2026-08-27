namespace Telltale.Collector;

/// <summary>
/// How the rows in a tier's tables are shaped, which decides how a promotion out
/// of that tier has to aggregate them.
/// </summary>
public enum TierShape
{
    /// <summary>
    /// One row per reading, holding the reading itself: <c>cpu_pct</c>,
    /// <c>private_mb</c>, <c>io_kb</c>. Averaging these is a plain average,
    /// because every row stands for exactly one measurement.
    /// </summary>
    Raw,

    /// <summary>
    /// One row per bucket, holding figures already aggregated over the readings
    /// that fell in it: <c>cpu_pct_avg</c>, <c>private_mb_max</c>,
    /// <c>io_kb_total</c>, and the <c>sample_count</c> saying how many readings
    /// they came from. Averaging these has to weight by that count, or a bucket
    /// holding one reading would count as much as one holding six hundred.
    /// </summary>
    Summarised,
}

/// <summary>
/// One rung of the storage ladder: a pair of tables holding the same span of
/// recorded history at the same width, one for per process readings and one for
/// the machine as a whole.
/// </summary>
/// <param name="SampleTable">The per process table, keyed on (ts, instance_id).</param>
/// <param name="MachineTable">The machine wide table, keyed on ts.</param>
/// <param name="BucketMinutes">
/// How wide one row is. Zero for <see cref="StorageTiers.Raw"/>, whose width is
/// the collector's sampling interval and so is not fixed here. Nothing promotes
/// into the raw tier, so its width is never needed as a target.
/// </param>
/// <param name="Shape">What a promotion out of this tier has to read.</param>
/// <param name="HasSustainedMax">
/// Whether this tier's tables carry <c>cpu_pct_sustained_max</c>, the highest ten
/// minute average inside the bucket.
///
/// The plain <c>cpu_pct_max</c> is the highest single reading, which is useful at
/// a minute and useless at a week: something spikes at some point in seven days,
/// so a weekly maximum is pinned near the top whatever kind of week it was. The
/// sustained figure answers the question people actually have at that range,
/// which is whether anything was busy for a while rather than for an instant.
///
/// It is carried only on the tiers wide enough to need it. Ten minutes is where
/// they are fed from, so at the hourly tier it is the worst ten minutes of the
/// hour, and above that it composes as a plain maximum of maxima.
/// </param>
public sealed record StorageTier(
    string SampleTable,
    string MachineTable,
    int BucketMinutes,
    TierShape Shape,
    bool HasSustainedMax = false);

/// <summary>
/// The ladder recorded history descends as it ages, finest first.
///
/// This is the one place the rungs are named. Three separate lists used to repeat
/// them: the promotion steps in <c>RollupWorker</c>, the orphan cleanup in
/// <c>Database</c>, and the table list a wipe empties. Adding a rung and missing
/// one of those failed silently rather than loudly, and in the orphan cleanup's
/// case it would have deleted <c>process_instance</c> rows a tier still referred
/// to.
/// </summary>
/// <remarks>
/// The viewer keeps its own list, in <c>TierSelection</c>, and deliberately so.
/// <c>collector</c> and <c>viewer</c> must not reference each other, and
/// <c>schema.sql</c> stays the only contract between them.
/// </remarks>
public static class StorageTiers
{
    /// <summary>Every reading as it was taken, at the collector's sampling interval.</summary>
    public static readonly StorageTier Raw = new("sample", "machine", 0, TierShape.Raw);

    public static readonly StorageTier OneMinute = new("sample_1m", "machine_1m", 1, TierShape.Summarised);

    public static readonly StorageTier TenMinute = new("sample_10m", "machine_10m", 10, TierShape.Summarised);

    public static readonly StorageTier OneHour = new("sample_1h", "machine_1h", 60, TierShape.Summarised, HasSustainedMax: true);

    public static readonly StorageTier OneDay = new("sample_1d", "machine_1d", 1_440, TierShape.Summarised, HasSustainedMax: true);

    /// <summary>
    /// The floor. Nothing is promoted out of it and nothing is deleted from it on
    /// a schedule, which is what makes keeping a recording indefinitely
    /// affordable: a year of weekly rows is a few hundred of them.
    /// </summary>
    public static readonly StorageTier OneWeek = new("sample_1w", "machine_1w", 10_080, TierShape.Summarised, HasSustainedMax: true);

    /// <summary>
    /// Every tier, finest first. Consecutive pairs are exactly the promotions the
    /// rollup performs, so a rung added here is promoted into without any other
    /// change.
    /// </summary>
    public static readonly IReadOnlyList<StorageTier> Ordered =
        [Raw, OneMinute, TenMinute, OneHour, OneDay, OneWeek];

    /// <summary>
    /// Where <paramref name="tier"/> sits on the ladder, or -1 if it is not on it.
    /// Finer tiers have lower indexes.
    /// </summary>
    public static int IndexOf(StorageTier tier)
    {
        for (int i = 0; i < Ordered.Count; i++)
        {
            if (Ordered[i] == tier) return i;
        }

        return -1;
    }

    /// <summary>
    /// Every per process table, each of which refers to <c>process_instance</c>.
    /// The orphan cleanup has to check all of them: a row still referred to by any
    /// one tier is not an orphan.
    /// </summary>
    public static IEnumerable<string> SampleTables => Ordered.Select(t => t.SampleTable);

    /// <summary>Every machine wide table.</summary>
    public static IEnumerable<string> MachineTables => Ordered.Select(t => t.MachineTable);

    /// <summary>
    /// Every table holding recorded readings, per process tables first. A wipe
    /// empties these, and the order matters only in that the rows referring to
    /// <c>process_instance</c> should go before the orphan cleanup runs.
    /// </summary>
    public static IEnumerable<string> AllTables => SampleTables.Concat(MachineTables);
}
