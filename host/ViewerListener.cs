using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Telltale.Viewer;

namespace Telltale.App;

/// <summary>
/// The HTTP listener that serves the Telltale window, started when the window is
/// opened and stopped when the last one goes away.
/// </summary>
/// <remarks>
/// The recorder runs all day. The listener must not. Merging the two executables
/// would otherwise have left a socket open for every hour the machine is on, which
/// is more exposure than the two-process arrangement it replaces, not less.
/// </remarks>
sealed class ViewerListener : IAsyncDisposable
{
    /// <summary>
    /// How long a window may go without saying anything before it is presumed gone.
    /// </summary>
    /// <remarks>
    /// This is the backstop, not the normal path. A window that closes says so and
    /// the listener stops within moments. This covers a browser that was killed
    /// rather than closed, so the beacon never arrived.
    /// </remarks>
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(90);

    /// <summary>How long the last window must stay gone before the listener stops.</summary>
    public static readonly TimeSpan Settle = TimeSpan.FromSeconds(5);

    /// <summary>How long to wait for a first window that never arrives.</summary>
    public static readonly TimeSpan StartupGrace = TimeSpan.FromSeconds(60);

    readonly string _databasePath;
    readonly int _preferredPort;
    readonly RollingLogFile? _log;
    readonly ICaptureWipe? _wipe;
    readonly SemaphoreSlim _gate = new(1, 1);

    WebApplication? _app;
    SessionTracker? _session;

    /// <param name="wipe">
    /// How a window may throw recorded history away, or null to serve no wipe
    /// endpoint at all. Null is not a degraded mode: a listener with nothing to
    /// wipe through simply does not have the route, so the only build that offers
    /// it is the one holding the recorder's own connection.
    /// </param>
    public ViewerListener(
        string databasePath, int preferredPort, RollingLogFile? log = null, ICaptureWipe? wipe = null)
    {
        _databasePath = databasePath;
        _preferredPort = preferredPort;
        _log = log;
        _wipe = wipe;
    }

    /// <summary>The address being served, or null when nothing is listening.</summary>
    public string? Url { get; private set; }

    /// <summary>
    /// The address to open a window on, which carries this listener's token.
    /// </summary>
    /// <remarks>
    /// The token stops any other page the user has open from driving the session
    /// endpoints, which is the threat it was added for. It is not hidden from the
    /// machine itself: it goes to the browser as a command line argument, so any
    /// local process that can read another's arguments can read it, and Telltale's
    /// own recorder stores it when recordCommandLines is on. That is a small thing
    /// to lose, because it lives only as long as this listener and authorises
    /// nothing but "this window is open" and "this window has gone", both on
    /// loopback. Issue #90 covers closing it properly.
    /// </remarks>
    public string? WindowUrl { get; private set; }

    public bool IsRunning => _app is not null;

    /// <summary>Whether every window has gone and the listener should be stopped.</summary>
    public bool EveryWindowHasGone() => _session?.ShouldStop() ?? false;

    /// <summary>
    /// Says that a window is expected shortly, so the watchdog does not stop the
    /// listener before it arrives. Used when the machine wakes from sleep, where
    /// every window has been silent for as long as the machine was away.
    /// </summary>
    public void ExpectWindowBack() => _session?.ExpectWindow();

    /// <summary>
    /// Starts listening, or returns the address already being served.
    /// </summary>
    /// <remarks>
    /// A configured port that cannot be bound falls back to one the operating
    /// system chooses rather than failing. Telltale opens its own window, so it
    /// always knows its own address, and refusing to start because a development
    /// tool happened to take a port would be a poor trade.
    /// </remarks>
    public async Task<string> StartAsync(CancellationToken cancellationToken = default)
    {
        // ConfigureAwait(false) throughout, here and in every await below. These
        // are called from the message loop, and a continuation captured against it
        // never runs once Application.Run has returned. That would leave this gate
        // held forever and hang the shutdown that is waiting on it.
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (WindowUrl is not null)
            {
                // The caller is about to launch a browser at this address, so the
                // session has to expect a window even though it is already running.
                // Otherwise the watchdog can stop the listener between handing the
                // address out and the browser finishing with it.
                _session!.ExpectWindow();
                return WindowUrl;
            }

            try
            {
                return await StartOnAsync(_preferredPort, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (_preferredPort != 0 && IsPortUnavailable(ex))
            {
                _log?.Append($"Port {_preferredPort} is not available, letting Windows choose one.");
                return await StartOnAsync(0, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_app is null)
                return;

            var app = _app;
            _app = null;
            _session = null;
            Url = null;
            WindowUrl = null;

            try
            {
                await app.StopAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                // Already on its way down.
            }

            await app.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    async Task<string> StartOnAsync(int port, CancellationToken cancellationToken)
    {
        var session = new SessionTracker(IdleTimeout, Settle, StartupGrace);
        var token = WindowToken.New();
        var webRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        Directory.CreateDirectory(webRoot);

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory,
            WebRootPath = webRoot,
        });

        // Loopback only, asserted here rather than inherited from a Kestrel
        // default. AllowedHosts is set the same way: an appsettings.json does
        // arrive in this application's output, carried along by the viewer project
        // reference, but a security property that matters this much should not
        // depend on a file this project neither owns nor declares.
        builder.WebHost.UseUrls($"http://{ViewerDefaults.LoopbackAddress}:{port}");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AllowedHosts"] = "localhost;127.0.0.1;[::1]",
        });

        builder.Logging.ClearProviders();
        if (_log is not null)
            builder.Logging.AddProvider(new FileLoggerProvider(_log));

        // Request logging is filtered out for the same reason. A request line
        // carries its query string, and /api/processes and /api/process-group put
        // a search term and a process name there, so at Information every one of
        // those would be written to a file this application promises is safe.
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        // Except this one. A refused Host header is the DNS rebinding defence doing
        // its job, and it is worth knowing it fired. It carries a host name rather
        // than a query string, so it does not raise the concern above.
        builder.Logging.AddFilter("Microsoft.AspNetCore.HostFiltering", LogLevel.Information);

        // The lifetime's console banner is written for a console, and this
        // application has none.
        builder.Services.Configure<ConsoleLifetimeOptions>(
            options => options.SuppressStatusMessages = true);

        var app = builder.Build();

        app.MapTelltaleApi(_databasePath);

        // Only the single-process build maps these, and only a page holding this
        // listener's token can reach them. Without the token any page the user has
        // open in another tab could post to the close endpoint and take the window
        // out from under them, or poll the ping endpoint and hold the socket open
        // for the hours this design exists to close it. Neither needs to read the
        // response to work, so a browser sends both without asking us first.
        app.MapPost("/api/session/ping", (HttpRequest request) =>
            ForWindow(request, token, id => { session.Ping(id); }));

        app.MapPost("/api/session/closed", (HttpRequest request) =>
            ForWindow(request, token, id => { session.Close(id); }));

        // Mapped on the same terms and for the same reason, one step further: this
        // one destroys the recording rather than closing a window, so it is behind
        // the token too, and it only exists when there is a recorder to wipe
        // through. See CaptureWipeEndpoint.
        if (_wipe is not null)
            app.MapCaptureWipe(_wipe, token, _log);

        try
        {
            await app.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await app.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        var url = app.Urls.FirstOrDefault()
            ?? $"http://{ViewerDefaults.LoopbackAddress}:{port}";

        _session = session;
        Url = url;
        WindowUrl = $"{url}/?s={token}";
        // Assigned last: everything above has to be in place before IsRunning can
        // report true, or a caller could act on a half-built listener.
        _app = app;

        _log?.Append($"Telltale window listening on {url}");
        return WindowUrl;
    }

    /// <summary>
    /// Runs <paramref name="action"/> for the window named in the request, if the
    /// request carries this listener's token.
    /// </summary>
    /// <returns>
    /// Not Found when the token is wrong or the window is not named. Not Forbidden:
    /// there is nothing to be gained by confirming to a caller that guessed wrong
    /// that it had found the right endpoint.
    /// </returns>
    static IResult ForWindow(HttpRequest request, string token, Action<string> action)
    {
        string? windowId = request.Query["c"];

        if (string.IsNullOrEmpty(windowId) || !WindowToken.IsPresentedIn(request, token))
            return Results.NotFound();

        action(windowId);
        return Results.NoContent();
    }

    /// <summary>
    /// Whether a start failed because the address was taken rather than for some
    /// reason retrying on another port would not fix.
    /// </summary>
    static bool IsPortUnavailable(Exception error)
    {
        for (Exception? ex = error; ex is not null; ex = ex.InnerException)
        {
            if (ex is SocketException socket)
            {
                return socket.SocketErrorCode is SocketError.AddressAlreadyInUse
                    or SocketError.AccessDenied
                    or SocketError.AddressNotAvailable;
            }

            // Kestrel reports a bind failure as an IOException wrapping the reason.
            // Some of those reasons are not SocketExceptions, so an IOException on
            // its own is still treated as worth retrying elsewhere.
            if (ex is IOException && ex.InnerException is null)
                return true;
        }

        return error is IOException;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await StopAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Dispose();
        }
    }
}
