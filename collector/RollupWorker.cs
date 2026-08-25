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

        long rawCutoff = now - (long)TimeSpan.FromHours(_config.RawRetentionHours).TotalMilliseconds;
        _db.RollupSamples(rawCutoff, "sample", "sample_1m", 1, isMachine: false);
        _db.RollupSamples(rawCutoff, "machine", "machine_1m", 1, isMachine: true);
        _logger.LogDebug("Tier 1 rollup complete (cutoff: {Cutoff}).", rawCutoff);

        // Never let tier two run ahead of tier one. If it did, it would promote a ten
        // minute bucket that tier one has not finished filling, and the minutes still
        // to come would then land in a bucket the target already holds and be
        // discarded. A configuration whose one minute retention is shorter than the
        // raw retention would otherwise cause exactly that; Config.Validate now
        // rejects those, and this clamp keeps the invariant even if one slips through.
        long tier1Cutoff = Math.Min(
            now - (long)TimeSpan.FromDays(_config.Rollup1mRetentionDays).TotalMilliseconds,
            rawCutoff);
        _db.RollupSamples(tier1Cutoff, "sample_1m", "sample_10m", 10, isMachine: false);
        _db.RollupSamples(tier1Cutoff, "machine_1m", "machine_10m", 10, isMachine: true);
        _logger.LogDebug("Tier 2 rollup complete (cutoff: {Cutoff}).", tier1Cutoff);

        long tier2Cutoff = now - (long)TimeSpan.FromDays(_config.Rollup10mRetentionDays).TotalMilliseconds;
        _db.DeleteOldData("sample_10m", tier2Cutoff);
        _db.DeleteOldData("machine_10m", tier2Cutoff);

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
}
