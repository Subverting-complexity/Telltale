using System.Diagnostics;

namespace Telltale.Collector;

public sealed class ProcessSampler : IProcessSampler
{
    private readonly ILogger _logger;
    private readonly HashSet<int> _warnedPids = [];

    public bool IsNative => false;

    public ProcessSampler(ILogger logger)
    {
        _logger = logger;
        _logger.LogWarning(
            "Using managed process sampler (degraded mode). " +
            "Enumeration is slower and some system processes may be inaccessible.");
    }

    public List<ProcessSnapshot> Sample()
    {
        var results = new List<ProcessSnapshot>(256);
        var processes = Process.GetProcesses();

        foreach (var proc in processes)
        {
            try
            {
                long createTimeTicks = 0;
                try
                {
                    createTimeTicks = proc.StartTime.ToUniversalTime().ToFileTimeUtc();
                }
                catch (Exception) when (!_warnedPids.Contains(proc.Id))
                {
                    _warnedPids.Add(proc.Id);
                }

                long ioRead = 0, ioWrite = 0;
                int handleCount = 0;

                try { handleCount = proc.HandleCount; } catch { }

                results.Add(new ProcessSnapshot(
                    Pid: proc.Id,
                    CreateTimeTicks: createTimeTicks,
                    Name: proc.ProcessName,
                    KernelTime: proc.PrivilegedProcessorTime.Ticks,
                    UserTime: proc.UserProcessorTime.Ticks,
                    WorkingSetBytes: proc.WorkingSet64,
                    PrivateBytes: proc.PrivateMemorySize64,
                    IoReadBytes: ioRead,
                    IoWriteBytes: ioWrite,
                    IoOtherBytes: 0,
                    ThreadCount: proc.Threads.Count,
                    HandleCount: handleCount));
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                if (_warnedPids.Add(proc.Id))
                {
                    _logger.LogDebug("Cannot access process {Pid} ({Name}): {Message}",
                        proc.Id, proc.ProcessName, ex.Message);
                }
            }
            finally
            {
                proc.Dispose();
            }
        }

        return results;
    }
}
