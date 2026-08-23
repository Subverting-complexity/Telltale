using System.Diagnostics;

namespace Telltale.Collector;

public sealed class CollectorWorker : BackgroundService
{
    private readonly ILogger<CollectorWorker> _logger;
    private readonly TelltaleConfig _config;
    private readonly Database _db;
    private readonly IProcessSampler _sampler;
    private readonly MachineSampler _machineSampler;

    private readonly Dictionary<(int Pid, long CreateTime), PreviousSample> _previous = new();
    private readonly Stopwatch _elapsedTimer = new();

    public CollectorWorker(
        ILogger<CollectorWorker> logger,
        TelltaleConfig config,
        Database db,
        IProcessSampler sampler,
        MachineSampler machineSampler)
    {
        _logger = logger;
        _config = config;
        _db = db;
        _sampler = sampler;
        _machineSampler = machineSampler;
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

        var rows = new List<SampleRow>(snapshots.Count);
        var seenKeys = new HashSet<(int, long)>();

        foreach (var snap in snapshots)
        {
            var key = (snap.Pid, snap.CreateTimeTicks);
            if (!seenKeys.Add(key)) continue;

            string? commandLine = null;
            string? path = null;

            if (_config.RecordCommandLines && snap.Pid > 4)
            {
                try
                {
                    using var proc = Process.GetProcessById(snap.Pid);
                    try
                    {
                        path = proc.MainModule?.FileName;
                    }
                    catch { }

                    try
                    {
                        commandLine = GetCommandLine(snap.Pid);
                        commandLine = TelltaleConfig.RedactCommandLine(commandLine);
                    }
                    catch { }
                }
                catch { }
            }
            else if (snap.Pid > 4)
            {
                try
                {
                    using var proc = Process.GetProcessById(snap.Pid);
                    path = proc.MainModule?.FileName;
                }
                catch { }
            }

            long instanceId = _db.GetOrCreateProcessInstance(
                snap.Pid, snap.CreateTimeTicks, snap.Name, path, commandLine, timestamp);

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

        if (rows.Count > 0)
            _db.WriteSampleBatch(timestamp, rows);

        _db.WriteMachineSample(timestamp, machineSample);

        sw.Stop();
        RecordHealth(timestamp, sw.Elapsed.TotalMilliseconds, snapshots.Count, rows.Count);
    }

    private void CleanStalePrevious(HashSet<(int, long)> seenKeys)
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

    private static string? GetCommandLine(int pid)
    {
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                $"SELECT CommandLine FROM Win32_Process WHERE ProcessId = {pid}");
            foreach (var obj in searcher.Get())
            {
                return obj["CommandLine"]?.ToString();
            }
        }
        catch { }
        return null;
    }

    private record PreviousSample(
        long KernelTime, long UserTime, long ElapsedTicks,
        long IoReadBytes, long IoWriteBytes, long IoOtherBytes);
}
