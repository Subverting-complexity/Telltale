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
/// opened and stopped when it goes away.
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

    readonly string _databasePath;
    readonly int _preferredPort;
    readonly RollingLogFile? _log;
    readonly SemaphoreSlim _gate = new(1, 1);

    WebApplication? _app;
    SessionTracker? _session;

    public ViewerListener(string databasePath, int preferredPort, RollingLogFile? log = null)
    {
        _databasePath = databasePath;
        _preferredPort = preferredPort;
        _log = log;
    }

    /// <summary>The address the window is served on, or null when nothing is listening.</summary>
    public string? Url { get; private set; }

    public bool IsRunning => _app is not null;

    /// <summary>Whether the window has gone away and the listener should be stopped.</summary>
    public bool WindowHasGone() => _session?.ShouldStop() ?? false;

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
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (Url is not null)
            {
                _session!.Touch();
                return Url;
            }

            try
            {
                return await StartOnAsync(_preferredPort, cancellationToken);
            }
            catch (Exception ex) when (_preferredPort != 0 && IsPortUnavailable(ex))
            {
                _log?.Append($"Port {_preferredPort} is not available, letting Windows choose one.");
                return await StartOnAsync(0, cancellationToken);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_app is null)
                return;

            var app = _app;
            _app = null;
            _session = null;
            Url = null;

            try
            {
                await app.StopAsync();
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                // Already on its way down.
            }

            await app.DisposeAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    async Task<string> StartOnAsync(int port, CancellationToken cancellationToken)
    {
        var session = new SessionTracker(IdleTimeout);
        var webRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        Directory.CreateDirectory(webRoot);

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory,
            WebRootPath = webRoot,
        });

        // Loopback only, asserted here rather than inherited from a Kestrel
        // default. AllowedHosts is set for the same reason: the shipped viewer
        // carried it in appsettings.json, and this host has no appsettings.json to
        // carry it in.
        builder.WebHost.UseUrls($"http://{ViewerDefaults.LoopbackAddress}:{port}");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AllowedHosts"] = "localhost;127.0.0.1;[::1]",
        });

        builder.Logging.ClearProviders();
        if (_log is not null)
            builder.Logging.AddProvider(new FileLoggerProvider(_log));

        // Same reason as the recorder: the lifetime's console banner is written
        // for a console, and this application has none.
        builder.Services.Configure<Microsoft.Extensions.Hosting.ConsoleLifetimeOptions>(
            options => options.SuppressStatusMessages = true);

        var app = builder.Build();

        // Registered before anything that can answer, so every request counts as
        // the window still being there. The session endpoints are excluded because
        // one of them is the window saying it has gone.
        app.Use(async (context, next) =>
        {
            if (!context.Request.Path.StartsWithSegments("/api/session"))
                session.Touch();
            await next(context);
        });

        app.MapTelltaleApi(_databasePath);

        // Only the single-process build maps these. The viewer executable does not,
        // because it has no listener lifetime to manage, and the page treats a 404
        // from them as "nothing to tell".
        app.MapPost("/api/session/ping", () =>
        {
            session.Touch();
            return Results.NoContent();
        });

        app.MapPost("/api/session/closed", () =>
        {
            session.MarkClosed();
            return Results.NoContent();
        });

        try
        {
            await app.StartAsync(cancellationToken);
        }
        catch
        {
            await app.DisposeAsync();
            throw;
        }

        _app = app;
        _session = session;
        Url = app.Urls.FirstOrDefault()
            ?? $"http://{ViewerDefaults.LoopbackAddress}:{port}";
        _log?.Append($"Telltale window listening on {Url}");
        return Url;
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
        await StopAsync();
        _gate.Dispose();
    }
}
