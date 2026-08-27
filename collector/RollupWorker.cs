namespace Telltale.Collector;

public sealed class RollupWorker : BackgroundService
{
    private readonly ILogger<RollupWorker> _logger;
    private readonly TelltaleConfig _config;
    private readonly Database _db;

    /// <summary>
    /// Consecutive failures at or above this count are logged as critical rather
    /// than as an error, and stay critical on every later failing cycle. A rollup
    /// that fails once is usually transient. One that keeps failing means nothing is
    /// being aggregated and the raw tables are no longer being trimmed, so the
    /// severity is raised to match.
    /// </summary>
    public const int ConsecutiveFailuresBeforeCritical = 3;

    /// <summary>
    /// How many summarising steps one cycle may take to bring an oversized file
    /// back under its limit before it stops and leaves the rest to the next cycle.
    ///
    /// Each step holds the write lock, and the recorder cannot write while it is
    /// held, so this bounds how long sampling can be held up by housekeeping. A
    /// database far over its limit converges across several cycles instead, which
    /// is slower but never stalls the thing the application exists to do.
    /// </summary>
    public const int MaxPressureStepsPerCycle = 8;

    private int _consecutiveFailures;

    public RollupWorker(ILogger<RollupWorker> logger, TelltaleConfig config, Database db)
    {
        _logger = logger;
        _config = config;
        _db = db;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Rollup worker started. Interval: {Interval} minutes.",
            _config.RollupIntervalMinutes);

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(_config.RollupIntervalMinutes));

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                RunRollup();

                if (_consecutiveFailures > 0)
                {
                    _logger.LogWarning(
                        "Rollup recovered after {Failures} consecutive failed cycle(s).",
                        _consecutiveFailures);
                    _consecutiveFailures = 0;
                }
            }
            catch (Exception ex)
            {
                _consecutiveFailures++;

                if (LevelForConsecutiveFailures(_consecutiveFailures) == LogLevel.Critical)
                {
                    _logger.LogCritical(ex,
                        "Rollup has failed {Failures} cycles in a row. No data is being "
                        + "aggregated and the raw tables are not being trimmed, so the "
                        + "database will keep growing until this is resolved.",
                        _consecutiveFailures);
                }
                else
                {
                    _logger.LogError(ex,
                        "Error during rollup cycle ({Failures} consecutive failure(s)).",
                        _consecutiveFailures);
                }
            }
        }
    }

    /// <summary>
    /// The level a failing cycle should be logged at, given how many cycles have now
    /// failed in a row.
    /// </summary>
    public static LogLevel LevelForConsecutiveFailures(int consecutiveFailures) =>
        consecutiveFailures >= ConsecutiveFailuresBeforeCritical
            ? LogLevel.Critical
            : LogLevel.Error;

    /// <summary>
    /// One whole cycle: age everything that is due, trim the health tables, and
    /// bring the file back under its size limit if it has outgrown it.
    /// </summary>
    /// <remarks>
    /// Public so a test can drive a single cycle against a real database. Going
    /// through <see cref="ExecuteAsync"/> instead would mean waiting out a
    /// <see cref="PeriodicTimer"/> measured in minutes, and the behaviour worth
    /// pinning here is what one cycle does rather than that the timer fires.
    /// </remarks>
    public void RunRollup()
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        IReadOnlyDictionary<string, long> pressure = _db.ReadTierPressure();

        PromoteThroughTiers(now, pressure);

        long healthCutoff = now - (long)TimeSpan.FromDays(_config.HealthRetentionDays).TotalMilliseconds;
        _db.DeleteOldData("collector_health", healthCutoff);

        // The phase breakdown is part of the same health record, one row per tick
        // against the same timestamp, so it keeps and loses its rows together with
        // the summary rather than outliving it.
        _db.DeleteOldData("collector_tick_phase", healthCutoff);

        _db.DeleteOrphanedProcessInstances();

        ApplySizePressure(now, pressure);

        _db.IncrementalVacuum();
        _db.WalCheckpoint();

        _logger.LogInformation("Rollup cycle complete. DB size: {Size:F1} MB.",
            _db.GetDatabaseSizeBytes() / (1024.0 * 1024.0));
    }

    /// <summary>
    /// Walks every rung of <see cref="StorageTiers.Ordered"/> in turn, folding what
    /// has aged out of each one into the tier below it.
    /// </summary>
    /// <remarks>
    /// Nothing here deletes a recorded reading. Ageing gives up detail and only
    /// detail: a row leaves a tier by being folded into the tier below, and the
    /// coarsest tier is never promoted out of and never trimmed, so a recording is
    /// kept indefinitely at a width that costs a few hundred rows a year.
    ///
    /// This used to end at the ten minute tier, with anything older than
    /// <c>rollup10mRetentionDays</c> deleted outright. That deletion is gone. The
    /// hourly, daily and weekly tiers below it are where that data goes now.
    /// </remarks>
    private void PromoteThroughTiers(long now, IReadOnlyDictionary<string, long> pressure)
    {
        // Never let a tier run ahead of the one feeding it. If it did, it would
        // promote a bucket the tier above has not finished filling, and the rows
        // still to come would land in a bucket the target already holds and be
        // discarded. A configuration whose retentions are out of order would cause
        // exactly that; Config.Validate rejects those, and this running clamp keeps
        // the invariant even if one slips through.
        long previousCutoff = long.MaxValue;

        IReadOnlyList<StorageTier> tiers = StorageTiers.Ordered;

        for (int i = 0; i < tiers.Count - 1; i++)
        {
            StorageTier source = tiers[i];
            StorageTier target = tiers[i + 1];

            // The effective retention, not the configured one: a tier the size
            // limit has already pulled in keeps its tightened hold rather than
            // springing back to what telltale.json says every cycle.
            //
            // Null means the tier keeps what it holds, which is true only of the
            // coarsest one. That has no tier below it to be promoted into, so it is
            // never a source here, but the check keeps this honest if a rung is
            // ever appended below it.
            long? retentionMs = SizePressure.EffectiveRetentionMs(_config, source, pressure);
            if (retentionMs is null) continue;

            long cutoff = Math.Min(now - retentionMs.Value, previousCutoff);

            _db.RollupSamples(cutoff, source, target, isMachine: false);
            _db.RollupSamples(cutoff, source, target, isMachine: true);

            _logger.LogDebug("Promoted {Source} into {Target} (cutoff: {Cutoff}).",
                source.SampleTable, target.SampleTable, cutoff);

            previousCutoff = cutoff;
        }
    }

    /// <summary>
    /// Brings the file back under <c>maxDatabaseSizeMb</c> by summarising further,
    /// one step at a time, until it fits or there is nothing left to summarise.
    /// </summary>
    /// <remarks>
    /// Nothing here deletes a recorded reading either. Each step pulls one tier's
    /// retention inward and promotes what that releases into the tier below, so the
    /// file shrinks by giving up detail. What it replaces dropped the oldest day of
    /// the ten minute tables and then the oldest day of the one minute tables,
    /// which lost those readings outright, and in the one minute case lost a day
    /// that had not been promoted yet, leaving a hole in the middle of a tier's
    /// span that <c>TierSelection.Plan</c> assumes cannot exist.
    ///
    /// Bounded per cycle rather than run to completion. Every step takes the write
    /// lock, and while it is held the recorder cannot write, so a database far over
    /// its limit converges across several cycles instead of stalling sampling for
    /// one long one. The state is persisted, so the next cycle carries on from
    /// where this one stopped rather than starting again.
    ///
    /// The vacuum inside the loop is not optional. A promotion frees its pages onto
    /// the file's own free list without reducing <c>page_count</c>, so without it
    /// every measurement after the first would report the size the file had before
    /// any of this ran, and the loop would spend its whole budget every time.
    /// </remarks>
    private void ApplySizePressure(long now, IReadOnlyDictionary<string, long> pressure)
    {
        long maxBytes = _config.MaxDatabaseSizeMb * 1024L * 1024L;
        if (_db.GetDatabaseSizeBytes() <= maxBytes) return;

        var applied = new Dictionary<string, long>(pressure, StringComparer.Ordinal);

        for (int step = 0; step < MaxPressureStepsPerCycle; step++)
        {
            PressureStep? next = SizePressure.NextStep(_config, applied);

            if (next is null)
            {
                _logger.LogCritical(
                    "The capture is {Size:F1} MB against a limit of {Limit} MB, and every tier is already "
                    + "as coarse as it is allowed to get, so there is nothing left to summarise. Recording "
                    + "continues and the file will keep growing. Raise maxDatabaseSizeMb, or delete some "
                    + "history from the Telltale window.",
                    _db.GetDatabaseSizeBytes() / (1024.0 * 1024.0), _config.MaxDatabaseSizeMb);
                return;
            }

            applied[next.Source.SampleTable] = next.RetentionMs;
            _db.WriteTierPressure(next.Source.SampleTable, next.RetentionMs);

            long cutoff = now - next.RetentionMs;
            _db.RollupSamples(cutoff, next.Source, next.Target, isMachine: false);
            _db.RollupSamples(cutoff, next.Source, next.Target, isMachine: true);

            _db.IncrementalVacuum();

            _logger.LogInformation(
                "Capture over its {Limit} MB limit: {Source} now keeps {Days:F1} days rather than what was "
                + "configured, and the rest has been summarised into {Target}.",
                _config.MaxDatabaseSizeMb, next.Source.SampleTable,
                TimeSpan.FromMilliseconds(next.RetentionMs).TotalDays, next.Target.SampleTable);

            if (_db.GetDatabaseSizeBytes() <= maxBytes) return;
        }

        _logger.LogWarning(
            "The capture is still {Size:F1} MB against a limit of {Limit} MB after {Steps} summarising "
            + "steps. Stopping for this cycle so sampling is not held up, and carrying on at the next one.",
            _db.GetDatabaseSizeBytes() / (1024.0 * 1024.0), _config.MaxDatabaseSizeMb,
            MaxPressureStepsPerCycle);
    }
}
