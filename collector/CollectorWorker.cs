using System.Diagnostics;

namespace Telltale.Collector;

public sealed class CollectorWorker : BackgroundService
{
    private readonly ILogger<CollectorWorker> _logger;
    private readonly TelltaleConfig _config;
    private readonly Database _db;
    private readonly IProcessSampler _sampler;
    private readonly MachineSampler _machineSampler;
    private readonly ProcessIdentityResolver _identities;

    private readonly Dictionary<(int Pid, long CreateTime), PreviousSample> _previous = new();

    /// <summary>
    /// The collector's own processor time and the stopwatch reading it was taken
    /// at, from the previous tick. Null until the first health row has been
    /// written, because a rate needs two readings and the first tick has one.
    /// </summary>
    private (long CpuTicks, long ElapsedTicks)? _previousSelf;
    private readonly Stopwatch _elapsedTimer = new();
    private readonly TickOverrunMonitor _overruns = new();

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

        try
        {
            _machineSampler.Initialize();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Machine sampler failed to initialise. System-level metrics (CPU, memory, "
                + "disk, network) will be missing from this recording.");
        }

        // Recorded once, because it describes the machine rather than a moment in
        // the recording. The viewer needs it to turn a per process CPU figure,
        // which is a share of one core, into a share of the whole machine, and
        // reading the live count instead is wrong as soon as a capture is opened
        // anywhere but the machine it was made on.
        // Guarded like the machine sampler above, and for the same reason. This is
        // the first write of the run, so it is the one most likely to meet a full
        // disk or a locked file, and an unhandled exception in a BackgroundService
        // stops the host by default. That would take the tray icon and the viewer
        // window down with it, over one informational row the viewer already has a
        // fallback for.
        try
        {
            _db.WriteMachineInfo(Environment.ProcessorCount);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Could not record this machine's processor count. The viewer will "
                + "fall back to the processor count of whichever machine reads the "
                + "capture, which is only correct while that is this one.");
        }

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

        // Every phase of the tick is timed separately. sample_cost_ms says only
        // that a tick ran long; these say which part of it did, which is the
        // difference between knowing there is a problem and knowing where it is.
        var phase = Stopwatch.StartNew();

        var snapshots = _sampler.Sample();
        double samplerMs = LapMs(phase);

        var machineSample = _machineSampler.Sample();
        double machineSampleMs = LapMs(phase);

        // The tick's distinct process instances, gathered before anything else so
        // every one whose identity is not yet known can be looked up in a single
        // batch rather than one query at a time. A pid can appear twice in one
        // snapshot, so the distinct snapshots are kept here and every step below
        // works from them rather than deduplicating the raw list again each time.
        var seenKeys = new HashSet<(int Pid, long CreateTime)>(snapshots.Count);
        var distinct = new List<ProcessSnapshot>(snapshots.Count);

        foreach (var snap in snapshots)
        {
            if (seenKeys.Add((snap.Pid, snap.CreateTimeTicks)))
                distinct.Add(snap);
        }

        _identities.Resolve(seenKeys);
        double identityMs = LapMs(phase);

        // The row ids are resolved for the whole tick in one call, for the same
        // reason the identities above are: asking per process meant a separate
        // commit per process, and a tick covering some 670 of them spent tens of
        // seconds waiting on them one after another.
        var rows = new List<SampleRow>(distinct.Count);
        double instanceMs;
        double rowBuildMs;

        try
        {
            var upserts = new List<ProcessInstanceUpsert>(distinct.Count);

            foreach (var snap in distinct)
            {
                var upsertIdentity = _identities.For((snap.Pid, snap.CreateTimeTicks));
                upserts.Add(new ProcessInstanceUpsert(
                    snap.Pid, snap.CreateTimeTicks, snap.Name,
                    upsertIdentity.Path, upsertIdentity.CommandLine));
            }

            var instanceIds = _db.UpsertProcessInstances(upserts, timestamp);
            instanceMs = LapMs(phase);

            foreach (var snap in distinct)
            {
                var key = (snap.Pid, snap.CreateTimeTicks);

                // Defensive, and unreachable today: the upsert returns an id for
                // every key it is handed, and throws rather than returning a
                // partial map. It is here so that if it ever does report a row it
                // could not place, this tick drops that process rather than
                // pairing its sample with an id belonging to something else.
                if (!instanceIds.TryGetValue(key, out long instanceId)) continue;

                double? cpuPct = null;
                double? ioKb = null;

                if (_previous.TryGetValue(key, out var prev))
                {
                    long cpuDelta = (snap.KernelTime + snap.UserTime) - (prev.KernelTime + prev.UserTime);
                    cpuPct = CpuRate.PercentOfOneCore(cpuDelta, elapsedTicks - prev.ElapsedTicks);

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

            rowBuildMs = LapMs(phase);
        }
        finally
        {
            // Pruning runs even when the tick fails part way through, including
            // inside the instance upsert above. Without this a tick that throws
            // leaves both maps holding processes that have already gone, and they
            // only clear on the next tick that gets all the way down.
            CleanStalePrevious(seenKeys);
            _identities.Prune(seenKeys);
        }

        if (rows.Count > 0)
            _db.WriteSampleBatch(timestamp, rows);
        double sampleWriteMs = LapMs(phase);

        _db.WriteMachineSample(timestamp, machineSample);
        double machineWriteMs = LapMs(phase);

        // Read before the health write so sample_cost_ms keeps meaning what it always
        // meant, but stop the clock after it, so a tick pushed over the interval by
        // its own health write is still counted as an overrun. The phase row is
        // written after the reading too, for the same reason: the phases have to add
        // up to the cost recorded beside them.
        double sampleCostMs = sw.Elapsed.TotalMilliseconds;

        // Named rather than positional. Seven doubles in a row is a shape where two
        // of them swapped still compiles, still passes, and answers the question
        // this table exists to ask with the wrong phase named.
        _db.WriteTickPhases(timestamp, new TickPhaseTimings(
            SamplerMs: samplerMs,
            MachineSampleMs: machineSampleMs,
            IdentityMs: identityMs,
            InstanceMs: instanceMs,
            RowBuildMs: rowBuildMs,
            SampleWriteMs: sampleWriteMs,
            MachineWriteMs: machineWriteMs));

        RecordHealth(timestamp, sampleCostMs, snapshots.Count, rows.Count);
        sw.Stop();
        RecordOverrun(sw.Elapsed);
    }

    /// <summary>
    /// Reads how long the current phase took and starts the clock for the next
    /// one, so the phases divide the tick between them rather than overlapping.
    /// </summary>
    private static double LapMs(Stopwatch timer)
    {
        double elapsedMs = timer.Elapsed.TotalMilliseconds;
        timer.Restart();

        return elapsedMs;
    }

    private void CleanStalePrevious(HashSet<(int Pid, long CreateTime)> seenKeys)
    {
        var staleKeys = _previous.Keys.Where(k => !seenKeys.Contains(k)).ToList();
        foreach (var k in staleKeys)
            _previous.Remove(k);
    }

    /// <summary>
    /// Writes the row that answers "is the recorder itself the thing slowing this
    /// machine down". The CPU figure is measured exactly as every other process's
    /// is: processor time used since the previous tick, over the wall clock time
    /// between the two readings, against the same stopwatch the sampling loop
    /// uses. It is therefore on the same denominator as <c>sample.cpu_pct</c>, a
    /// share of one core, and not of the whole machine.
    /// </summary>
    private void RecordHealth(long timestamp, double sampleCostMs, int processCount, int storedCount)
    {
        // Disposed rather than left to the finaliser. This runs once a tick, and
        // each call opens a handle to the process it describes.
        using var self = Process.GetCurrentProcess();

        long cpuTicks = self.PrivilegedProcessorTime.Ticks + self.UserProcessorTime.Ticks;
        long elapsedTicks = _elapsedTimer.ElapsedTicks;

        // Nothing rather than zero on the first tick, and on the reading after a
        // stopwatch or counter that went backwards. Zero is a measurement, and
        // writing one where none was taken is what this field did before.
        double? cpuPct = _previousSelf is { } previous
            ? CpuRate.PercentOfOneCore(cpuTicks - previous.CpuTicks, elapsedTicks - previous.ElapsedTicks)
            : null;

        _previousSelf = (cpuTicks, elapsedTicks);

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
        var (outcome, overruns) = _overruns.Record(tickDuration, _config.IntervalSeconds);

        switch (outcome)
        {
            case TickOutcome.Recovered:
                _logger.LogInformation(
                    "Sampling is keeping up again after {Overruns} tick(s) that ran long.",
                    overruns);
                break;

            case TickOutcome.Overrun
                when TickOverrunMonitor.LevelForConsecutiveOverruns(overruns) == LogLevel.Error:
                _logger.LogError(
                    "Sampling has run longer than its {Interval}s interval {Overruns} times in a "
                    + "row, the last taking {Duration:F1}s. The collector cannot keep up, so the "
                    + "recorded history will have gaps.",
                    _config.IntervalSeconds, overruns, tickDuration.TotalSeconds);
                break;

            case TickOutcome.Overrun:
                _logger.LogWarning(
                    "Sampling tick took {Duration:F1}s, longer than the {Interval}s interval.",
                    tickDuration.TotalSeconds, _config.IntervalSeconds);
                break;
        }
    }

    private record PreviousSample(
        long KernelTime, long UserTime, long ElapsedTicks,
        long IoReadBytes, long IoWriteBytes, long IoOtherBytes);
}
