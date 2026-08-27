namespace Telltale.Collector;

/// <summary>
/// One tightening of one tier's retention: the promotion it implies, and the
/// retention that tier is left holding afterwards.
/// </summary>
public sealed record PressureStep(StorageTier Source, StorageTier Target, long RetentionMs);

/// <summary>
/// Decides what to give up when the database is larger than the user asked it to
/// be.
///
/// The answer is never a deletion. A tier's retention is pulled inward, which
/// means more of what it holds is folded into the tier below it on the next
/// promotion. Detail is given up; readings are not. That is the whole difference
/// between this and what it replaces, which dropped the oldest day of the ten
/// minute tables and then the oldest day of the one minute tables, and returned
/// whatever the size happened to be by then.
/// </summary>
/// <remarks>
/// Pure: no database, no clock, no configuration reading. It is handed the
/// configured retentions and whatever pressure has already been applied, and it
/// returns the next step or nothing. That makes the policy testable without a
/// file on disk, which matters because the interesting cases are the ones where
/// it has to stop.
/// </remarks>
public static class SizePressure
{
    /// <summary>
    /// The tiers whose retention pressure may pull in, oldest boundary first.
    ///
    /// Detail is given up from the far end of the recording before the near end.
    /// If something went wrong this morning, this morning's fine detail is the
    /// most valuable thing in the file, so the daily tier's hold on two years is
    /// surrendered long before the one minute tier's hold on a week.
    /// </summary>
    /// <remarks>
    /// The raw tier is deliberately absent. Twenty four hours of five second
    /// readings is plausibly the largest single thing in the file, which makes it
    /// the largest available lever, but that is arithmetic from the schema rather
    /// than a measurement, and shortening it costs the live view the detail it
    /// exists for. Whether it ever belongs here is its own decision, taken against
    /// a real recording rather than an estimate.
    /// </remarks>
    public static readonly IReadOnlyList<StorageTier> Pullable =
        [StorageTiers.OneDay, StorageTiers.OneHour, StorageTiers.TenMinute, StorageTiers.OneMinute];

    /// <summary>
    /// How close to its floor a tier has to be before the last step goes straight
    /// there rather than halving the remaining gap again.
    ///
    /// Without it the halving approaches the floor without reaching it and the loop
    /// keeps finding a step that frees almost nothing.
    /// </summary>
    public const long MinimumStepMs = 3_600_000L;

    /// <summary>
    /// How long <paramref name="tier"/> actually keeps what it holds: the
    /// configured retention, or whatever pressure has pulled it back to, whichever
    /// is shorter.
    /// </summary>
    /// <remarks>
    /// Pressure is a high-water mark, so this only ever moves inward. Raising
    /// <c>maxDatabaseSizeMb</c> afterwards does not restore the detail that was
    /// given up: it was folded into the tier below and the finer rows are gone.
    /// What a raised limit buys is that no further tightening happens.
    /// </remarks>
    public static long? EffectiveRetentionMs(
        TelltaleConfig config, StorageTier tier, IReadOnlyDictionary<string, long> applied)
    {
        long? configured = config.RetentionMsFor(tier);
        if (configured is null) return null;

        return applied.TryGetValue(tier.SampleTable, out long pressured)
            ? Math.Min(configured.Value, pressured)
            : configured.Value;
    }

    /// <summary>
    /// The next tightening to apply, or null when every tier is already as tight as
    /// it is allowed to get.
    /// </summary>
    /// <remarks>
    /// Null is the end of the ladder, and the caller's answer to it is to say so
    /// and let the file exceed its limit rather than start deleting. With a weekly
    /// tier at the floor that should take a recording well outside anything normal
    /// use produces.
    /// </remarks>
    public static PressureStep? NextStep(TelltaleConfig config, IReadOnlyDictionary<string, long> applied)
    {
        foreach (StorageTier tier in Pullable)
        {
            long? current = EffectiveRetentionMs(config, tier, applied);
            if (current is null) continue;

            long floor = FloorMsFor(config, tier);
            if (current.Value <= floor) continue;

            return new PressureStep(tier, TierBelow(tier), TightenedFrom(current.Value, floor));
        }

        return null;
    }

    /// <summary>
    /// The shortest retention <paramref name="tier"/> may be pulled back to: where
    /// the tier feeding it is <em>configured</em> to hand over.
    /// </summary>
    /// <remarks>
    /// The configured retention of the finer tier, not its effective one, and that
    /// distinction is load bearing rather than incidental.
    ///
    /// Measured against the effective value the floors move as the ladder is
    /// tightened, so every step taken on a fine tier re-opens room on every coarse
    /// tier above it. The steps halve the remaining gap, so each re-opening costs
    /// another dozen or so steps on each tier above, and the total compounds: with
    /// four pullable tiers it runs to thousands of steps before it comes to rest.
    /// It does terminate, but far too slowly to be called convergence, and a test
    /// that walks it to the end is how that showed up.
    ///
    /// Against the configured value each tier has a fixed floor and converges on it
    /// in about fifteen steps, independently of the others. The ladder as a whole
    /// settles with each tier holding roughly what the tier above it was configured
    /// to, which is one rung of compaction across the board and is both predictable
    /// and easy to describe to whoever has to read the log line.
    ///
    /// The invariant still holds. A tier that kept its rows for less time than the
    /// tier feeding it would be asked to promote buckets that tier has not finished
    /// filling, and the rows still to come would land in a bucket the target already
    /// holds and be discarded. Every effective retention is at most its configured
    /// one, and the configured ones are validated in order, so no tier can end up
    /// below the one feeding it.
    /// </remarks>
    private static long FloorMsFor(TelltaleConfig config, StorageTier tier)
    {
        int index = StorageTiers.IndexOf(tier);
        StorageTier finer = StorageTiers.Ordered[index - 1];

        return config.RetentionMsFor(finer)
               ?? (long)TimeSpan.FromHours(config.RawRetentionHours).TotalMilliseconds;
    }

    private static StorageTier TierBelow(StorageTier tier) =>
        StorageTiers.Ordered[StorageTiers.IndexOf(tier) + 1];

    /// <summary>
    /// Halves the distance to the floor, or goes straight to it once the remaining
    /// distance is not worth another step.
    /// </summary>
    /// <remarks>
    /// Halving rather than stepping by a fixed amount because the gaps differ by
    /// orders of magnitude: the daily tier starts about 550 days above its floor
    /// and the ten minute tier about 23. A fixed step would take hundreds of cycles
    /// on one and overshoot the other in a single move.
    /// </remarks>
    private static long TightenedFrom(long current, long floor)
    {
        long gap = current - floor;

        return gap <= MinimumStepMs ? floor : floor + (gap / 2);
    }
}
