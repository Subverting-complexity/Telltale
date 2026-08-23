namespace Telltale.Collector;

public sealed class RollupWorker : BackgroundService
{
    private readonly ILogger<RollupWorker> _logger;
    private readonly TelltaleConfig _config;
    private readonly Database _db;

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
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during rollup cycle.");
            }
        }
    }

    private void RunRollup()
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        long rawCutoff = now - (long)TimeSpan.FromHours(_config.RawRetentionHours).TotalMilliseconds;
        _db.RollupSamples(rawCutoff, "sample", "sample_1m", 1, isMachine: false);
        _db.RollupSamples(rawCutoff, "machine", "machine_1m", 1, isMachine: true);
        _logger.LogDebug("Tier 1 rollup complete (cutoff: {Cutoff}).", rawCutoff);

        long tier1Cutoff = now - (long)TimeSpan.FromDays(_config.Rollup1mRetentionDays).TotalMilliseconds;
        _db.RollupSamples(tier1Cutoff, "sample_1m", "sample_10m", 10, isMachine: false);
        _db.RollupSamples(tier1Cutoff, "machine_1m", "machine_10m", 10, isMachine: true);
        _logger.LogDebug("Tier 2 rollup complete (cutoff: {Cutoff}).", tier1Cutoff);

        long tier2Cutoff = now - (long)TimeSpan.FromDays(_config.Rollup10mRetentionDays).TotalMilliseconds;
        _db.DeleteOldData("sample_10m", tier2Cutoff);
        _db.DeleteOldData("machine_10m", tier2Cutoff);

        long healthCutoff = now - (long)TimeSpan.FromDays(_config.HealthRetentionDays).TotalMilliseconds;
        _db.DeleteOldData("collector_health", healthCutoff);

        _db.DeleteOrphanedProcessInstances();

        _db.EnforceSizeLimit(_config.MaxDatabaseSizeMb * 1024L * 1024L);

        _db.IncrementalVacuum();
        _db.WalCheckpoint();

        _logger.LogInformation("Rollup cycle complete. DB size: {Size:F1} MB.",
            _db.GetDatabaseSizeBytes() / (1024.0 * 1024.0));
    }
}
