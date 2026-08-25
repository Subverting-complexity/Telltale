namespace Telltale.App;

/// <summary>
/// Lets a second launch of Telltale reach the copy that is already running.
/// </summary>
/// <remarks>
/// Telltale spends most of its life with no window, so starting it again is the
/// natural way to ask for one. Refusing with "already running", which is what both
/// of the old executables did, told the user something was there without telling
/// them how to reach it.
/// </remarks>
sealed class SecondInstanceSignal : IDisposable
{
    readonly EventWaitHandle _requested;
    readonly EventWaitHandle _stopping = new(false, EventResetMode.ManualReset);
    Thread? _listener;

    /// <summary>Creates the handle the running instance waits on.</summary>
    /// <remarks>
    /// The handle resets itself, so launches arriving faster than they are consumed
    /// collapse into one. That is what we want here: opening the window twice in a
    /// row is the same as opening it once.
    /// </remarks>
    public SecondInstanceSignal(string name)
    {
        _requested = new EventWaitHandle(false, EventResetMode.AutoReset, name);
    }

    /// <summary>
    /// Asks an instance that is already running to show its window.
    /// </summary>
    /// <returns>
    /// False when there is nothing listening, which means the other process is
    /// starting up or shutting down rather than ready. The caller exits either way:
    /// a second recorder must not start.
    /// </returns>
    public static bool TrySignal(string name)
    {
        try
        {
            using var handle = EventWaitHandle.OpenExisting(name);
            return handle.Set();
        }
        catch (Exception ex) when (ex is WaitHandleCannotBeOpenedException
                                      or UnauthorizedAccessException
                                      or IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// Runs <paramref name="onRequested"/> on a background thread each time another
    /// launch asks for the window.
    /// </summary>
    public void Listen(Action onRequested)
    {
        if (_listener is not null)
            throw new InvalidOperationException("Already listening.");

        _listener = new Thread(() =>
        {
            WaitHandle[] handles = [_requested, _stopping];
            while (WaitHandle.WaitAny(handles) == 0)
                onRequested();
        })
        {
            IsBackground = true,
            Name = "Telltale second instance",
        };
        _listener.Start();
    }

    public void Dispose()
    {
        _stopping.Set();
        _listener?.Join(TimeSpan.FromSeconds(2));
        _requested.Dispose();
        _stopping.Dispose();
    }
}
