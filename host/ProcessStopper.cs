using System.Diagnostics;

namespace Telltale.App;

/// <summary>Stops a running process by the name of its executable.</summary>
interface IProcessStopper
{
    /// <summary>Whether anything with this image name is running in this session.</summary>
    bool IsRunning(string imageName);

    /// <summary>
    /// Asks everything with this image name to close, and makes it close if asking
    /// does not work.
    /// </summary>
    /// <returns>False if something with that name is still running afterwards.</returns>
    bool Stop(string imageName, TimeSpan timeout);
}

/// <summary>
/// Stops processes by image name, asking first and insisting second.
/// </summary>
/// <remarks>
/// By name rather than by path on purpose. A recorder started from a development
/// build holds the same lock as one started from the deployed folder, so leaving it
/// alone because it is somewhere unexpected would not help.
///
/// Only processes in the caller's own session are touched. Two people logged into
/// the same machine each record into their own database, so another session's
/// recorder is not in the way and is not ours to stop.
///
/// The calling process is never included. Telltale.exe --quit asks about
/// Telltale.exe, so without that it would find itself, conclude Telltale was still
/// running however long it waited, and report a stop that had in fact worked.
/// </remarks>
sealed class ImageNameProcessStopper : IProcessStopper
{
    /// <summary>
    /// How long a process that accepted a close request gets to act on it.
    /// </summary>
    /// <remarks>
    /// Only spent when something actually accepted one. A console application has
    /// no window of its own, because the console belongs to the host process rather
    /// than to it, so a close request has nowhere to go and comes straight back.
    /// Waiting the timeout out in that case would stall every start by the full
    /// budget and then force the process anyway.
    /// </remarks>
    public static readonly TimeSpan GracePeriod = TimeSpan.FromSeconds(5);

    readonly RollingLogFile? _log;

    public ImageNameProcessStopper(RollingLogFile? log = null) => _log = log;

    public bool IsRunning(string imageName) => RunningIds(imageName).Count > 0;

    /// <summary>
    /// The ids of everything this stopper would act on, which is everything with
    /// that image name in this session apart from the calling process.
    /// </summary>
    public IReadOnlyList<int> RunningIds(string imageName)
    {
        var ids = new List<int>();
        foreach (var process in Find(imageName))
        {
            using (process)
            {
                ids.Add(process.Id);
            }
        }

        return ids;
    }

    public bool Stop(string imageName, TimeSpan timeout)
    {
        // Asked first, so a recorder gets to close its database and checkpoint the
        // write-ahead log. SQLite survives being stopped part way through a write,
        // but it then has to recover the log on the next start, and that is work
        // nobody needs to pay for when asking would have done.
        var asked = 0;
        foreach (var process in Find(imageName))
        {
            using (process)
            {
                if (Try(() => process.CloseMainWindow()))
                    asked++;
            }
        }

        if (asked > 0)
        {
            var graceDeadline = DateTime.UtcNow + Shorter(GracePeriod, timeout);
            if (WaitUntilGone(imageName, graceDeadline))
                return true;

            _log?.Append($"{imageName} did not close on request, stopping it.");
        }
        else if (IsRunning(imageName))
        {
            _log?.Append($"{imageName} has no window to close, stopping it.");
        }

        foreach (var process in Find(imageName))
        {
            using (process)
            {
                Try(() => process.Kill());
            }
        }

        // A deadline of its own. Reusing the one the asking phase spent would leave
        // this with no time at all, so a process that has just been stopped but is
        // still in the process table would be reported as still running, and the
        // caller would give up on a machine where nothing is now in its way.
        return WaitUntilGone(imageName, DateTime.UtcNow + Shorter(GracePeriod, timeout));
    }

    static TimeSpan Shorter(TimeSpan a, TimeSpan b) => a < b ? a : b;

    bool WaitUntilGone(string imageName, DateTime deadline)
    {
        while (true)
        {
            if (!IsRunning(imageName))
                return true;
            if (DateTime.UtcNow >= deadline)
                return false;
            Thread.Sleep(100);
        }
    }

    /// <summary>Everything with this image name in the caller's own session.</summary>
    static Process[] Find(string imageName)
    {
        try
        {
            using var self = Process.GetCurrentProcess();
            var session = self.SessionId;
            var ownId = self.Id;

            var matches = new List<Process>();
            foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(imageName)))
            {
                var mine = false;
                try
                {
                    mine = process.SessionId == session && process.Id != ownId;
                }
                catch (Exception ex) when (ex is InvalidOperationException
                                              or System.ComponentModel.Win32Exception)
                {
                    // Gone between being listed and being asked about.
                }

                if (mine)
                    matches.Add(process);
                else
                    process.Dispose();
            }

            return [.. matches];
        }
        catch (InvalidOperationException)
        {
            return [];
        }
    }

    /// <summary>
    /// Runs something that may fail because the process has already gone, or is not
    /// ours to touch.
    /// </summary>
    /// <returns>What the action returned, or false if it could not be run.</returns>
    static bool Try(Func<bool> action)
    {
        try
        {
            return action();
        }
        catch (Exception ex) when (ex is InvalidOperationException
                                      or NotSupportedException
                                      or AggregateException
                                      or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    static void Try(Action action) => Try(() =>
    {
        action();
        return true;
    });
}
