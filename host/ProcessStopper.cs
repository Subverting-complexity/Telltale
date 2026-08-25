using System.Diagnostics;

namespace Telltale.App;

/// <summary>Stops a running process by the name of its executable.</summary>
interface IProcessStopper
{
    /// <summary>Whether anything with this image name is running.</summary>
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
/// build holds the same lock as one started from the deployed folder, so leaving
/// it alone because it is somewhere unexpected would not help.
/// </remarks>
sealed class ImageNameProcessStopper : IProcessStopper
{
    readonly RollingLogFile? _log;

    public ImageNameProcessStopper(RollingLogFile? log = null) => _log = log;

    public bool IsRunning(string imageName) => Find(imageName).Length > 0;

    public bool Stop(string imageName, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        // Asked first, so a recorder gets to close its database and checkpoint the
        // write-ahead log. SQLite survives being killed part way through a write,
        // but it then has to recover the log on the next start, and that is work
        // nobody needs to pay for when asking politely would have done.
        foreach (var process in Find(imageName))
        {
            using (process)
            {
                Try(() => process.CloseMainWindow());
            }
        }

        if (WaitUntilGone(imageName, deadline))
            return true;

        _log?.Append($"{imageName} did not close on request, stopping it.");
        foreach (var process in Find(imageName))
        {
            using (process)
            {
                Try(() => process.Kill(entireProcessTree: true));
            }
        }

        return WaitUntilGone(imageName, deadline);
    }

    bool WaitUntilGone(string imageName, DateTime deadline)
    {
        while (DateTime.UtcNow < deadline)
        {
            if (!IsRunning(imageName))
                return true;
            Thread.Sleep(200);
        }

        return !IsRunning(imageName);
    }

    static Process[] Find(string imageName)
    {
        try
        {
            return Process.GetProcessesByName(Path.GetFileNameWithoutExtension(imageName));
        }
        catch (InvalidOperationException)
        {
            return [];
        }
    }

    /// <summary>
    /// Runs something that may fail because the process has already gone, or
    /// belongs to another user.
    /// </summary>
    static void Try(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (ex is InvalidOperationException
                                      or NotSupportedException
                                      or System.ComponentModel.Win32Exception)
        {
        }
    }
}
