using System.Diagnostics;
using System.Management;

namespace Telltale.Collector;

/// <summary>
/// Reads process paths through the managed <see cref="Process"/> API and command
/// lines through a single WMI query covering every process at once.
/// </summary>
/// <remarks>
/// The one-query-per-batch shape is the point of this class. The collector used to
/// run <c>SELECT CommandLine FROM Win32_Process WHERE ProcessId = n</c> once per
/// process per tick. Each of those costs in the order of tens of milliseconds, so
/// on a machine with several hundred processes a single tick took longer than the
/// five second interval and nothing was recorded at all.
/// </remarks>
public sealed class WmiProcessIdentitySource : IProcessIdentitySource
{
    private readonly ILogger<WmiProcessIdentitySource> _logger;

    public WmiProcessIdentitySource(ILogger<WmiProcessIdentitySource> logger)
    {
        _logger = logger;
    }

    public string? GetPath(int pid)
    {
        try
        {
            using var proc = Process.GetProcessById(pid);
            return proc.MainModule?.FileName;
        }
        catch (Exception ex)
        {
            // Protected and already-exited processes both land here, and both are
            // ordinary rather than a fault. The path stays null and sampling carries on.
            _logger.LogDebug("Cannot read the executable path of process {Pid}: {Message}",
                pid, ex.Message);
            return null;
        }
    }

    public IReadOnlyDictionary<int, string?> GetCommandLines(IReadOnlyCollection<int> pids)
    {
        var found = new Dictionary<int, string?>(pids.Count);
        if (pids.Count == 0)
            return found;

        var wanted = pids as HashSet<int> ?? new HashSet<int>(pids);

        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT ProcessId, CommandLine FROM Win32_Process");
            using var results = searcher.Get();

            foreach (var row in results)
            {
                using (row)
                {
                    if (row["ProcessId"] is not uint rawPid)
                        continue;

                    int pid = (int)rawPid;
                    if (wanted.Contains(pid))
                        found[pid] = row["CommandLine"]?.ToString();
                }
            }
        }
        catch (Exception ex)
        {
            // WMI can be disabled, throttled, or broken on a given machine. Losing
            // command lines is a degraded recording, not a reason to stop sampling.
            _logger.LogDebug("Cannot read process command lines from WMI: {Message}", ex.Message);
        }

        return found;
    }
}
