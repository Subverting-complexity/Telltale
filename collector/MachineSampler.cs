using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Telltale.Collector;

[SupportedOSPlatform("windows")]
public sealed class MachineSampler : IDisposable
{
    private readonly ILogger _logger;
    private PerformanceCounter? _cpuCounter;
    private PerformanceCounter? _memAvailCounter;
    private PerformanceCounter? _commitCounter;
    private PerformanceCounter? _hardFaultCounter;
    private PerformanceCounter? _diskReadCounter;
    private PerformanceCounter? _diskWriteCounter;
    private PerformanceCounter? _diskIdleCounter;
    private PerformanceCounter[]? _netCounters;
    private bool _initialized;

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern int PdhLookupPerfNameByIndex(
        string? szMachineName, uint dwNameIndex, char[] szNameBuffer, ref uint pcchNameBufferSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    public MachineSampler(ILogger logger)
    {
        _logger = logger;
    }

    public void Initialize()
    {
        if (_initialized) return;
        _initialized = true;

        _cpuCounter = TryCreateCounter("Processor", "% Processor Time", "_Total",
            categoryIndex: 238, counterIndex: 6);
        _memAvailCounter = TryCreateCounter("Memory", "Available MBytes", null,
            categoryIndex: 4, counterIndex: 24);
        _commitCounter = TryCreateCounter("Memory", "Committed Bytes", null,
            categoryIndex: 4, counterIndex: 26);
        _hardFaultCounter = TryCreateCounter("Memory", "Page Reads/sec", null,
            categoryIndex: 4, counterIndex: 38);
        _diskReadCounter = TryCreateCounter("PhysicalDisk", "Avg. Disk sec/Read", "_Total",
            categoryIndex: 234, counterIndex: 208);
        _diskWriteCounter = TryCreateCounter("PhysicalDisk", "Avg. Disk sec/Write", "_Total",
            categoryIndex: 234, counterIndex: 210);
        _diskIdleCounter = TryCreateCounter("PhysicalDisk", "% Idle Time", "_Total",
            categoryIndex: 234, counterIndex: 1746);

        InitNetCounters();

        ReadCounter(_cpuCounter);
        ReadCounter(_memAvailCounter);
        ReadCounter(_commitCounter);
        ReadCounter(_hardFaultCounter);
        ReadCounter(_diskReadCounter);
        ReadCounter(_diskWriteCounter);
        ReadCounter(_diskIdleCounter);
        if (_netCounters != null)
            foreach (var c in _netCounters) ReadCounter(c);
    }

    private PerformanceCounter? TryCreateCounter(string categoryEnglish, string counterEnglish,
        string? instance, uint categoryIndex, uint counterIndex)
    {
        try
        {
            string category = ResolveCounterName(categoryIndex) ?? categoryEnglish;
            string counter = ResolveCounterName(counterIndex) ?? counterEnglish;
            var pc = instance != null
                ? new PerformanceCounter(category, counter, instance, true)
                : new PerformanceCounter(category, counter, true);
            return pc;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Could not create performance counter {Category}/{Counter}: {Message}",
                categoryEnglish, counterEnglish, ex.Message);
            return null;
        }
    }

    private static string? ResolveCounterName(uint index)
    {
        try
        {
            var buffer = new char[256];
            uint size = (uint)buffer.Length;
            int result = PdhLookupPerfNameByIndex(null, index, buffer, ref size);
            if (result == 0 && size > 0)
                return new string(buffer, 0, (int)size - 1);
        }
        catch { }
        return null;
    }

    private void InitNetCounters()
    {
        try
        {
            string category = ResolveCounterName(510) ?? "Network Interface";
            string counter = ResolveCounterName(388) ?? "Bytes Total/sec";

            if (!PerformanceCounterCategory.Exists(category))
            {
                _logger.LogWarning("Network performance counter category not found.");
                return;
            }

            var cat = new PerformanceCounterCategory(category);
            var instances = cat.GetInstanceNames();

            string[] excludePatterns = ["loopback", "hyper-v", "vethernet", "docker", "wsl", "isatap", "teredo"];
            var filtered = instances.Where(i =>
                !excludePatterns.Any(p => i.Contains(p, StringComparison.OrdinalIgnoreCase))).ToArray();

            _netCounters = filtered.Select(i =>
            {
                try { return new PerformanceCounter(category, counter, i, true); }
                catch { return null; }
            }).Where(c => c != null).Cast<PerformanceCounter>().ToArray();
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Could not initialise network counters: {Message}", ex.Message);
        }
    }

    public MachineSample Sample()
    {
        double? cpuPct = ReadCounter(_cpuCounter);
        double? commitBytes = ReadCounter(_commitCounter);
        double? commitMb = commitBytes.HasValue ? commitBytes.Value / (1024 * 1024) : null;
        double? hardFaults = ReadCounter(_hardFaultCounter);
        double? diskReadSec = ReadCounter(_diskReadCounter);
        double? diskWriteSec = ReadCounter(_diskWriteCounter);
        double? diskIdle = ReadCounter(_diskIdleCounter);

        double? diskReadMs = diskReadSec.HasValue ? diskReadSec.Value * 1000 : null;
        double? diskWriteMs = diskWriteSec.HasValue ? diskWriteSec.Value * 1000 : null;
        double? diskBusyPct = diskIdle.HasValue ? Math.Min(100.0, Math.Max(0.0, 100.0 - diskIdle.Value)) : null;

        double? netKbps = null;
        if (_netCounters is { Length: > 0 })
        {
            double sum = 0;
            bool anyRead = false;
            foreach (var c in _netCounters)
            {
                var val = ReadCounter(c);
                if (val.HasValue) { sum += val.Value; anyRead = true; }
            }
            if (anyRead) netKbps = sum / 1024.0;
        }

        double memoryTotalMb;
        double? memAvailMb;
        var memStatus = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (GlobalMemoryStatusEx(ref memStatus))
        {
            memoryTotalMb = memStatus.ullTotalPhys / (1024.0 * 1024.0);
            memAvailMb = memStatus.ullAvailPhys / (1024.0 * 1024.0);
        }
        else
        {
            memoryTotalMb = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024.0 * 1024.0);
            memAvailMb = ReadCounter(_memAvailCounter);
        }

        return new MachineSample(
            CpuPct: cpuPct,
            MemoryAvailMb: memAvailMb,
            CommitMb: commitMb,
            HardFaults: hardFaults.HasValue ? (int)hardFaults.Value : null,
            DiskReadMs: diskReadMs,
            DiskWriteMs: diskWriteMs,
            MemoryTotalMb: memoryTotalMb,
            DiskBusyPct: diskBusyPct,
            NetKbps: netKbps,
            GpuBusyPct: null);
    }

    private static double? ReadCounter(PerformanceCounter? counter)
    {
        if (counter == null) return null;
        try { return counter.NextValue(); }
        catch { return null; }
    }

    public void Dispose()
    {
        _cpuCounter?.Dispose();
        _memAvailCounter?.Dispose();
        _commitCounter?.Dispose();
        _hardFaultCounter?.Dispose();
        _diskReadCounter?.Dispose();
        _diskWriteCounter?.Dispose();
        _diskIdleCounter?.Dispose();
        if (_netCounters != null)
            foreach (var c in _netCounters) c.Dispose();
    }
}
