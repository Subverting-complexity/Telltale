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
    /// <summary>
    /// How many consecutive failed lookups are each worth their own log line before
    /// the message repeats only every <see cref="RepeatFailureEvery"/> lookups. WMI
    /// being unavailable is a standing condition rather than an event, so saying so
    /// once every tick for as long as it lasts would drown out everything else.
    /// </summary>
    public const int ConsecutiveFailuresBeforeQuieting = 2;

    /// <summary>How often a standing failure is repeated once it has quieted.</summary>
    public const int RepeatFailureEvery = 60;

    private readonly ILogger<WmiProcessIdentitySource> _logger;
    private readonly TimeSpan _queryTimeout;

    private int _consecutiveFailures;

    public WmiProcessIdentitySource(ILogger<WmiProcessIdentitySource> logger, TelltaleConfig config)
    {
        _logger = logger;

        // WMI's default is to wait forever, and a query that outlives the sampling
        // interval has already cost the tick it was serving. This bounds the
        // enumeration only. The connect inside searcher.Get() uses the default
        // ConnectionOptions timeout, which is not bounded, so a WMI service hung at
        // connect can still stall the sampling loop. That gap is #67.
        _queryTimeout = TimeSpan.FromSeconds(config.IntervalSeconds);
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

    public IReadOnlyDictionary<int, string?>? GetCommandLines(IReadOnlyCollection<int> pids)
    {
        if (pids.Count == 0)
            return new Dictionary<int, string?>();

        var wanted = pids as HashSet<int> ?? new HashSet<int>(pids);

        try
        {
            var options = new System.Management.EnumerationOptions
            {
                Timeout = _queryTimeout,
                ReturnImmediately = true,
            };
            using var searcher = new ManagementObjectSearcher(
                new ManagementScope(), new ObjectQuery("SELECT ProcessId, CommandLine FROM Win32_Process"),
                options);
            using var results = searcher.Get();

            var found = ShapeRows(ReadRows(results), wanted, out int unreadable);

            if (unreadable > 0)
            {
                // Win32_Process.ProcessId is documented as a uint32, so this should
                // never fire. If it ever does, every row is being dropped and command
                // lines quietly stop being recorded, which is exactly the kind of
                // silent wrong answer a native boundary has to report rather than
                // swallow.
                _logger.LogWarning(
                    "Skipped {Count} process row(s) from WMI whose ProcessId was not a number. "
                    + "Command lines for those processes are not being recorded.", unreadable);
            }

            if (_consecutiveFailures > 0)
            {
                _logger.LogInformation(
                    "Process command line lookups are working again after {Failures} failed attempt(s).",
                    _consecutiveFailures);
                _consecutiveFailures = 0;
            }

            return found;
        }
        catch (Exception ex)
        {
            // WMI can be disabled, throttled, or broken on a given machine, and the
            // query can time out. Losing command lines is a degraded recording rather
            // than a reason to stop sampling, so this is not rethrown. It is logged at
            // warning because the user asked for command lines and is not getting
            // them, and reporting that at debug would be silence in practice.
            _consecutiveFailures++;

            if (ShouldReportFailure(_consecutiveFailures))
            {
                _logger.LogWarning(
                    "Cannot read process command lines from WMI ({Failures} attempt(s) in a row): "
                    + "{Message}. Sampling continues without them.",
                    _consecutiveFailures, ex.Message);
            }

            return null;
        }
    }

    /// <summary>
    /// Whether a failed lookup is worth its own log line, given how many have failed
    /// in a row.
    /// </summary>
    public static bool ShouldReportFailure(int consecutiveFailures) =>
        consecutiveFailures <= ConsecutiveFailuresBeforeQuieting
        || consecutiveFailures % RepeatFailureEvery == 0;

    /// <summary>
    /// Turns the raw WMI rows into a pid-to-command-line map, keeping only the pids
    /// that were asked for.
    /// </summary>
    /// <param name="unreadable">
    /// How many rows were dropped because their ProcessId was not the documented
    /// unsigned integer. Reported rather than swallowed, because a wrong type here
    /// drops every row and looks identical to a machine with no command lines.
    /// </param>
    /// <remarks>
    /// Separated from the query so the shaping can be tested. The WMI call itself
    /// cannot be, which is why as little logic as possible sits inside it.
    /// </remarks>
    public static Dictionary<int, string?> ShapeRows(
        IEnumerable<(object? ProcessId, object? CommandLine)> rows,
        IReadOnlyCollection<int> wanted,
        out int unreadable)
    {
        var wantedSet = wanted as HashSet<int> ?? new HashSet<int>(wanted);
        var found = new Dictionary<int, string?>(wantedSet.Count);
        unreadable = 0;

        foreach (var (rawPid, rawCommandLine) in rows)
        {
            if (rawPid is not uint pid)
            {
                unreadable++;
                continue;
            }

            int id = (int)pid;
            if (wantedSet.Contains(id))
                found[id] = rawCommandLine?.ToString();
        }

        return found;
    }

    /// <summary>
    /// Pulls the two fields off each WMI row and disposes it, so the shaping above
    /// never has to know about <see cref="ManagementBaseObject"/>.
    /// </summary>
    private static IEnumerable<(object? ProcessId, object? CommandLine)> ReadRows(
        ManagementObjectCollection results)
    {
        foreach (var row in results)
        {
            using (row)
                yield return (row["ProcessId"], row["CommandLine"]);
        }
    }
}
