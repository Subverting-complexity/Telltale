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

    private void RunRollup()
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        PromoteThroughTiers(now);

        long healthCutoff = now - (long)TimeSpan.FromDays(_config.HealthRetentionDays).TotalMilliseconds;
        _db.DeleteOldData("collector_health", healthCutoff);

        // The phase breakdown is part of the same health record, one row per tick
        // against the same timestamp, so it keeps and loses its rows together with
        // the summary rather than outliving it.
        _db.DeleteOldData("collector_tick_phase", healthCutoff);

        _db.DeleteOrphanedProcessInstances();

        _db.EnforceSizeLimit(_config.MaxDatabaseSizeMb * 1024L * 1024L);

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
    private void PromoteThroughTiers(long now)
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

            // Null means the tier keeps what it holds, which is true only of the
            // coarsest one. That has no tier below it to be promoted into, so it is
            // never a source here, but the check keeps this honest if a rung is
            // ever appended below it.
            long? retentionMs = _config.RetentionMsFor(source);
            if (retentionMs is null) continue;

            long cutoff = Math.Min(now - retentionMs.Value, previousCutoff);

            _db.RollupSamples(cutoff, source, target, isMachine: false);
            _db.RollupSamples(cutoff, source, target, isMachine: true);

            _logger.LogDebug("Promoted {Source} into {Target} (cutoff: {Cutoff}).",
                source.SampleTable, target.SampleTable, cutoff);

            previousCutoff = cutoff;
        }
    }
}
