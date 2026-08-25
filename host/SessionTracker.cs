namespace Telltale.App;

/// <summary>
/// Decides when the Telltale window has gone away, so the HTTP listener can be
/// shut down again.
/// </summary>
/// <remarks>
/// There is no reliable way to watch the window itself. Starting a browser with
/// <c>--app</c> hands the request to the browser that is already running and
/// returns a process handle that exits within moments, so waiting on that process
/// would report the window closed while it is still on screen.
///
/// What the page can do is say so. It pings while it is open and sends one beacon
/// as it goes away, and every other request it makes counts as a ping too, so a
/// page whose keepalive has failed still holds the listener open. The timeout is
/// the backstop for the case where the beacon never arrives, which is what happens
/// when a browser is killed rather than closed.
/// </remarks>
sealed class SessionTracker
{
    readonly TimeSpan _idleTimeout;
    readonly Func<DateTimeOffset> _now;
    readonly Lock _gate = new();

    DateTimeOffset _lastSeen;
    bool _closed;

    public SessionTracker(TimeSpan idleTimeout, Func<DateTimeOffset>? now = null)
    {
        _idleTimeout = idleTimeout;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _lastSeen = _now();
    }

    /// <summary>Records that the window is still there.</summary>
    /// <remarks>
    /// This clears a previous close as well as extending the deadline. A reload
    /// fires the page's close beacon and then asks for everything again, and that
    /// second page is a live window, not the one that went away.
    /// </remarks>
    public void Touch()
    {
        lock (_gate)
        {
            _lastSeen = _now();
            _closed = false;
        }
    }

    /// <summary>Records that the window said it was closing.</summary>
    public void MarkClosed()
    {
        lock (_gate)
        {
            _closed = true;
        }
    }

    /// <summary>Whether the listener should now be stopped.</summary>
    public bool ShouldStop()
    {
        lock (_gate)
        {
            return _closed || _now() - _lastSeen >= _idleTimeout;
        }
    }
}
