using System.Diagnostics;

namespace Telltale.Collector;

public sealed class CollectorWorker : BackgroundService
{
    /// <summary>
    /// Once this many ticks in a row have run longer than the sampling interval, the
    /// message is logged as an error rather than a warning, and stays there until a
    /// tick finishes in time. One slow tick is usually a busy moment. A run of them
    /// means the collector cannot keep up and the recorded history will have gaps.
    /// </summary>
    public const int ConsecutiveOverrunsBeforeError = 3;

    private readonly ILogger<CollectorWorker> _logger;
    private readonly TelltaleConfig _config;
    private readonly Database _db;
    private readonly IProcessSampler _sampler;
    private readonly MachineSampler _machineSampler;
    private readonly ProcessIdentityResolver _identities;

    private readonly Dictionary<(int Pid, long CreateTime), PreviousSample> _previous = new();
    private readonly Stopwatch _elapsedTimer = new();

    private int _consecutiveOverruns;

    public CollectorWorker(
        ILogger<CollectorWorker> logger,
        TelltaleConfig config,
        Database db,
        IProcessSampler sampler,
        MachineSampler machineSampler,
        ProcessIdentityResolver identities)
    {
        _logger = logger;
        _config = config;
        _db = db;
        _sampler = sampler;
        _machineSampler = machineSampler;
        _identities = identities;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Collector started. Interval: {Interval}s, Sampler: {Type}",
            _config.IntervalSeconds, _sampler.IsNative ? "Native" : "Managed");

        _machineSampler.Initialize();
        _elapsedTimer.Start();

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_config.IntervalSeconds));

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                SampleTick();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during sample tick.");
            }
        }
    }

    private void SampleTick()
    {
        var sw = Stopwatch.StartNew();
        long elapsedTicks = _elapsedTimer.ElapsedTicks;
        long timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var snapshots = _sampler.Sample();
        var machineSample = _machineSampler.Sample();

        // The tick's distinct process instances, gathered before the loop so every
        // one whose identity is not yet known can be looked up in a single batch
        // rather than one query at a time.
        var seenKeys = new HashSet<(int Pid, long CreateTime)>(snapshots.Count);
        foreach (var snap in snapshots)
            seenKeys.Add((snap.Pid, snap.CreateTimeTicks));

        _identities.Resolve(seenKeys);

        var rows = new List<SampleRow>(snapshots.Count);
        var handled = new HashSet<(int, long)>(seenKeys.Count);

        foreach (var snap in snapshots)
        {
            var key = (snap.Pid, snap.CreateTimeTicks);
            if (!handled.Add(key)) continue;

            var identity = _identities.For(key);

            long instanceId = _db.GetOrCreateProcessInstance(
                snap.Pid, snap.CreateTimeTicks, snap.Name, identity.Path, identity.CommandLine,
                timestamp);

            double? cpuPct = null;
            double? ioKb = null;

            if (_previous.TryGetValue(key, out var prev))
            {
                long cpuDelta = (snap.KernelTime + snap.UserTime) - (prev.KernelTime + prev.UserTime);
                long ticksDelta = elapsedTicks - prev.ElapsedTicks;

                if (ticksDelta > 0 && cpuDelta >= 0)
                {
                    double elapsedSec = (double)ticksDelta / Stopwatch.Frequency;
                    cpuPct = (cpuDelta / 10_000_000.0) / elapsedSec * 100.0;
                }

                long totalIo = snap.IoReadBytes + snap.IoWriteBytes + snap.IoOtherBytes;
                long prevTotalIo = prev.IoReadBytes + prev.IoWriteBytes + prev.IoOtherBytes;
                long ioDelta = totalIo - prevTotalIo;
                if (ioDelta >= 0)
                    ioKb = ioDelta / 1024.0;
            }

            _previous[key] = new PreviousSample(
                snap.KernelTime, snap.UserTime, elapsedTicks,
                snap.IoReadBytes, snap.IoWriteBytes, snap.IoOtherBytes);

            double privateMb = snap.PrivateBytes / (1024.0 * 1024.0);
            double workingSetMb = snap.WorkingSetBytes / (1024.0 * 1024.0);

            bool meetsThreshold = (cpuPct.HasValue && cpuPct.Value >= _config.Thresholds.CpuPct) ||
                                  privateMb >= _config.Thresholds.PrivateMemoryMb;

            if (meetsThreshold || !cpuPct.HasValue)
            {
                rows.Add(new SampleRow(instanceId, cpuPct, privateMb, workingSetMb, ioKb,
                    snap.ThreadCount, snap.HandleCount));
            }
        }

        CleanStalePrevious(seenKeys);
        _identities.Prune(seenKeys);

        if (rows.Count > 0)
            _db.WriteSampleBatch(timestamp, rows);

        _db.WriteMachineSample(timestamp, machineSample);

        sw.Stop();
        RecordHealth(timestamp, sw.Elapsed.TotalMilliseconds, snapshots.Count, rows.Count);
        RecordOverrun(sw.Elapsed);
    }

    private void CleanStalePrevious(HashSet<(int Pid, long CreateTime)> seenKeys)
    {
        var staleKeys = _previous.Keys.Where(k => !seenKeys.Contains(k)).ToList();
        foreach (var k in staleKeys)
            _previous.Remove(k);
    }

    private void RecordHealth(long timestamp, double sampleCostMs, int processCount, int storedCount)
    {
        var self = Process.GetCurrentProcess();
        double cpuPct = 0;
        double privateMb = self.PrivateMemorySize64 / (1024.0 * 1024.0);

        _db.WriteCollectorHealth(timestamp, cpuPct, privateMb, sampleCostMs, processCount, storedCount);
    }

    /// <summary>
    /// Says so when a tick takes longer than the interval it is meant to fit inside.
    /// Without this the failure is silent: the process stays up, no error is raised,
    /// and the only symptom is a viewer with nothing in it.
    /// </summary>
    private void RecordOverrun(TimeSpan tickDuration)
    {
        if (tickDuration.TotalSeconds <= _config.IntervalSeconds)
        {
            if (_consecutiveOverruns > 0)
            {
                _logger.LogInformation(
                    "Sampling is keeping up again after {Overruns} tick(s) that ran long.",
                    _consecutiveOverruns);
                _consecutiveOverruns = 0;
            }
            return;
        }

        _consecutiveOverruns++;

        if (LevelForConsecutiveOverruns(_consecutiveOverruns) == LogLevel.Error)
        {
            _logger.LogError(
                "Sampling has run longer than its {Interval}s interval {Overruns} times in a "
                + "row, the last taking {Duration:F1}s. The collector cannot keep up, so the "
                + "recorded history will have gaps.",
                _config.IntervalSeconds, _consecutiveOverruns, tickDuration.TotalSeconds);
        }
        else
        {
            _logger.LogWarning(
                "Sampling tick took {Duration:F1}s, longer than the {Interval}s interval.",
                tickDuration.TotalSeconds, _config.IntervalSeconds);
        }
    }

    /// <summary>
    /// The level an overrunning tick should be logged at, given how many ticks have
    /// now overrun in a row.
    /// </summary>
    public static LogLevel LevelForConsecutiveOverruns(int consecutiveOverruns) =>
        consecutiveOverruns >= ConsecutiveOverrunsBeforeError
            ? LogLevel.Error
            : LogLevel.Warning;

    private record PreviousSample(
        long KernelTime, long UserTime, long ElapsedTicks,
        long IoReadBytes, long IoWriteBytes, long IoOtherBytes);
}
