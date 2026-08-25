namespace Telltale.App;

/// <summary>
/// Keeps track of which Telltale windows are open, so the HTTP listener can be
/// shut down once the last one has gone.
/// </summary>
/// <remarks>
/// There is no reliable way to watch a window from here. Starting a browser with
/// <c>--app</c> hands the request to the browser that is already running and
/// returns a process handle that exits within moments, so waiting on that process
/// would report the window closed while it is still on screen.
///
/// What a window can do is say so. Each one pings while it is open and sends one
/// beacon on the way out, and it identifies itself when it does, so closing one
/// window does not shut the listener down under another one that is still open.
/// The timeout is the backstop for a browser that was killed rather than closed,
/// and so never got to send its beacon.
/// </remarks>
sealed class SessionTracker
{
    /// <summary>
    /// The most windows tracked at once. Only a page holding this listener's token
    /// can add one, so the cap is insurance against a bug rather than a defence,
    /// and the oldest is evicted rather than the newest refused.
    /// </summary>
    const int MaxWindows = 64;

    readonly TimeSpan _idleTimeout;
    readonly TimeSpan _settle;
    readonly TimeSpan _startupGrace;
    readonly Func<DateTimeOffset> _now;
    readonly Lock _gate = new();
    readonly Dictionary<string, DateTimeOffset> _windows = new(StringComparer.Ordinal);

    readonly DateTimeOffset _startedAt;
    DateTimeOffset? _emptySince;
    bool _everSawAWindow;

    /// <param name="idleTimeout">How long a window may go quiet before it is presumed gone.</param>
    /// <param name="settle">
    /// How long the last window must stay gone before the listener stops. This is
    /// what makes a reload safe: the page beacons on its way out and the page that
    /// replaces it takes a moment to say hello, and without a settling period the
    /// gap between the two looks exactly like the window closing.
    /// </param>
    /// <param name="startupGrace">
    /// How long to wait for the first window when none has ever arrived, which is
    /// what happens when no browser could be started at all.
    /// </param>
    public SessionTracker(
        TimeSpan idleTimeout,
        TimeSpan settle,
        TimeSpan startupGrace,
        Func<DateTimeOffset>? now = null)
    {
        _idleTimeout = idleTimeout;
        _settle = settle;
        _startupGrace = startupGrace;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _startedAt = _now();
    }

    /// <summary>Records that the window with this id is still open.</summary>
    public void Ping(string windowId)
    {
        lock (_gate)
        {
            if (!_windows.ContainsKey(windowId) && _windows.Count >= MaxWindows)
                EvictOldest();

            _windows[windowId] = _now();
            _everSawAWindow = true;
            _emptySince = null;
        }
    }

    /// <summary>Records that the window with this id has gone.</summary>
    public void Close(string windowId)
    {
        lock (_gate)
        {
            if (!_windows.Remove(windowId))
                return;

            // The settling period starts when the last window said goodbye, not
            // when something next gets round to asking. Otherwise the wait is the
            // settling period plus however long until the next poll, and a caller
            // that never asks would never notice at all.
            if (_windows.Count == 0 && _everSawAWindow)
                _emptySince ??= _now();
        }
    }

    /// <summary>Whether the listener should now be stopped.</summary>
    public bool ShouldStop()
    {
        lock (_gate)
        {
            var now = _now();

            DateTimeOffset? lastWentQuiet = null;
            foreach (var (id, lastSeen) in _windows.ToArray())
            {
                if (now - lastSeen < _idleTimeout)
                    continue;

                _windows.Remove(id);

                // Dated from when the window actually went quiet rather than from
                // now, so that a window we only just got round to noticing does not
                // buy itself another settling period on top of the timeout it has
                // already spent.
                var wentQuiet = lastSeen + _idleTimeout;
                if (lastWentQuiet is null || wentQuiet > lastWentQuiet)
                    lastWentQuiet = wentQuiet;
            }

            if (_windows.Count > 0)
            {
                _emptySince = null;
                return false;
            }

            if (!_everSawAWindow)
                return now - _startedAt >= _startupGrace;

            _emptySince ??= lastWentQuiet ?? now;
            return now - _emptySince.Value >= _settle;
        }
    }

    /// <summary>How many windows are currently believed to be open.</summary>
    public int OpenWindows
    {
        get
        {
            lock (_gate)
            {
                return _windows.Count;
            }
        }
    }

    void EvictOldest()
    {
        var oldest = _windows.OrderBy(w => w.Value).First().Key;
        _windows.Remove(oldest);
    }
}
