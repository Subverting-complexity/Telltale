namespace Telltale.App;

/// <summary>
/// The single-recorder lock, taken over from the executable Telltale replaces.
/// </summary>
/// <remarks>
/// Telltale and the older TelltaleCapture.exe record the same thing into the same
/// database, so exactly one of them may run. They share a lock name, which is what
/// makes that true.
///
/// Finding the old recorder holding it is the expected state during the changeover,
/// not a failure: a Startup shortcut that still points at TelltaleCapture.exe starts
/// it at every logon. Telltale is its replacement, so it stops it and takes over
/// rather than refusing to start and leaving the user to work out which of two
/// similarly named executables is in the way.
/// </remarks>
static class RecorderLock
{
    /// <summary>How long the old recorder gets to stop before it is made to.</summary>
    public static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(15);

    /// <summary>The outcome of trying to take the lock.</summary>
    /// <param name="Mutex">The held lock, or null when it could not be taken.</param>
    /// <param name="TookOver">Whether something else had to be stopped first.</param>
    /// <param name="Problem">What to tell the user, or null when there is nothing wrong.</param>
    public readonly record struct Result(Mutex? Mutex, bool TookOver, string? Problem);

    /// <summary>
    /// Takes the recorder lock, stopping the executable Telltale replaces if it is
    /// holding it.
    /// </summary>
    /// <param name="acquire">
    /// Takes the lock, returning null when something else already holds it. A
    /// parameter so the orchestration can be tested without two real processes.
    /// </param>
    /// <param name="timeout">
    /// How long the old recorder gets to stop, and how long the lock is then waited
    /// for. A parameter so a test does not have to wait the real one out.
    /// </param>
    public static Result Acquire(
        Func<Mutex?> acquire,
        IProcessStopper stopper,
        string replacedImageName,
        RollingLogFile? log = null,
        TimeSpan? timeout = null)
    {
        var wait = timeout ?? StopTimeout;
        var mutex = acquire();
        if (mutex is not null)
            return new Result(mutex, TookOver: false, Problem: null);

        if (!stopper.IsRunning(replacedImageName))
        {
            // Something holds the lock and it is not the executable we know how to
            // replace. Another copy of Telltale is the likely answer, and stopping
            // an unknown holder is not a decision to make on a guess.
            return new Result(null, false, string.Join(Environment.NewLine,
                "Telltale cannot start because something else is already recording.",
                "",
                "The recorder lock is held, but TelltaleCapture.exe is not running,",
                "so this is not the older recorder Telltale replaces. Check for",
                "another copy of Telltale.exe."));
        }

        log?.Append($"{replacedImageName} holds the recorder lock. Stopping it and taking over.");
        if (!stopper.Stop(replacedImageName, wait))
        {
            return new Result(null, false, string.Join(Environment.NewLine,
                $"Telltale could not stop {replacedImageName}, which is recording",
                "into the same database.",
                "",
                "Two recorders would both write to it, so this one has not started.",
                $"Stop {replacedImageName} yourself and try again."));
        }

        // The lock is released when the process that held it goes, but the handle
        // and the process do not disappear in the same instant, so this is worth a
        // few attempts rather than one.
        var deadline = DateTime.UtcNow + wait;
        while (true)
        {
            mutex = acquire();
            if (mutex is not null)
            {
                log?.Append($"Recorder lock taken over from {replacedImageName}.");
                return new Result(mutex, TookOver: true, Problem: null);
            }

            if (DateTime.UtcNow >= deadline)
            {
                return new Result(null, false, string.Join(Environment.NewLine,
                    $"Telltale stopped {replacedImageName} but could not take the",
                    "recorder lock afterwards.",
                    "",
                    "Something else claimed it in between. Try again in a moment."));
            }

            Thread.Sleep(200);
        }
    }

    /// <summary>Takes a named lock, or returns null when someone else holds it.</summary>
    public static Mutex? TryTake(string name)
    {
        var mutex = new Mutex(true, name, out bool createdNew);
        if (createdNew)
            return mutex;

        mutex.Dispose();
        return null;
    }
}
