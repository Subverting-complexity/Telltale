using System.Net;
using System.Net.Sockets;
using Telltale.App;

namespace Host.Tests;

/// <summary>
/// The listener as it actually runs: a real Kestrel server, on a real socket,
/// serving the same endpoints the viewer executable serves.
/// </summary>
/// <remarks>
/// These are integration tests rather than unit tests on purpose. Everything worth
/// asserting here is about what the socket does, and a stubbed server would prove
/// none of it.
/// </remarks>
public class ViewerListenerTests : IAsyncLifetime
{
    readonly string _databasePath = Path.Combine(
        Path.GetTempPath(), $"telltale-listener-{Guid.NewGuid():N}", "telltale.db");

    ViewerListener? _listener;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_listener is not null)
            await _listener.DisposeAsync();

        var folder = Path.GetDirectoryName(_databasePath);
        if (folder is not null && Directory.Exists(folder))
            Directory.Delete(folder, recursive: true);
    }

    /// <summary>A port nothing is listening on right now.</summary>
    static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    static int PortOf(string url) => new Uri(url).Port;

    [Fact]
    public async Task It_listens_on_the_configured_port()
    {
        var port = FreePort();
        _listener = new ViewerListener(_databasePath, port);

        var url = await _listener.StartAsync();

        Assert.Equal(port, PortOf(url));
        Assert.True(_listener.IsRunning);
    }

    [Fact]
    public async Task It_binds_loopback_and_nothing_else()
    {
        _listener = new ViewerListener(_databasePath, FreePort());

        var url = await _listener.StartAsync();

        // Asserted on the address rather than left to a Kestrel default. A wildcard
        // bind would put a person's capture history on every interface the machine
        // has, which is the one thing this application must never do.
        Assert.Equal("127.0.0.1", new Uri(url).Host);
    }

    [Fact]
    public async Task It_serves_the_same_api_the_viewer_serves()
    {
        _listener = new ViewerListener(_databasePath, FreePort());
        var url = await _listener.StartAsync();

        using var client = new HttpClient();
        var response = await client.GetAsync($"{url}/api/health");

        Assert.True(response.IsSuccessStatusCode);
    }

    [Fact]
    public async Task A_taken_port_falls_back_to_one_Windows_chooses()
    {
        var taken = FreePort();
        using var squatter = new TcpListener(IPAddress.Loopback, taken);
        squatter.Start();

        _listener = new ViewerListener(_databasePath, taken);
        var url = await _listener.StartAsync();

        Assert.NotEqual(taken, PortOf(url));

        // Falling back is only worth anything if the result actually works.
        using var client = new HttpClient();
        Assert.True((await client.GetAsync($"{url}/api/health")).IsSuccessStatusCode);
    }

    [Fact]
    public async Task Starting_again_returns_the_address_already_being_served()
    {
        _listener = new ViewerListener(_databasePath, FreePort());

        var first = await _listener.StartAsync();
        var second = await _listener.StartAsync();

        Assert.Equal(first, second);
    }

    [Fact]
    public async Task Stopping_closes_the_socket()
    {
        _listener = new ViewerListener(_databasePath, FreePort());
        var url = await _listener.StartAsync();

        await _listener.StopAsync();

        Assert.False(_listener.IsRunning);
        Assert.Null(_listener.Url);

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync($"{url}/api/health"));
    }

    [Fact]
    public async Task Stopping_twice_is_not_an_error()
    {
        _listener = new ViewerListener(_databasePath, FreePort());
        await _listener.StartAsync();

        await _listener.StopAsync();
        await _listener.StopAsync();

        Assert.False(_listener.IsRunning);
    }

    [Fact]
    public async Task It_can_be_started_again_after_being_stopped()
    {
        _listener = new ViewerListener(_databasePath, FreePort());
        await _listener.StartAsync();
        await _listener.StopAsync();

        var url = await _listener.StartAsync();

        using var client = new HttpClient();
        Assert.True((await client.GetAsync($"{url}/api/health")).IsSuccessStatusCode);
    }

    [Fact]
    public async Task The_window_saying_it_closed_is_what_ends_the_session()
    {
        _listener = new ViewerListener(_databasePath, FreePort());
        var url = await _listener.StartAsync();
        using var client = new HttpClient();

        Assert.False(_listener.WindowHasGone());

        var closed = await client.PostAsync($"{url}/api/session/closed", content: null);

        Assert.True(closed.IsSuccessStatusCode);
        Assert.True(_listener.WindowHasGone());
    }

    [Fact]
    public async Task An_ordinary_request_revives_a_session_that_said_it_was_closing()
    {
        // This is a reload: the page beacons on its way out and the page that
        // replaces it starts asking for data. That second page is a live window.
        _listener = new ViewerListener(_databasePath, FreePort());
        var url = await _listener.StartAsync();
        using var client = new HttpClient();

        await client.PostAsync($"{url}/api/session/closed", content: null);
        Assert.True(_listener.WindowHasGone());

        await client.GetAsync($"{url}/api/health");

        Assert.False(_listener.WindowHasGone());
    }

    [Fact]
    public async Task A_keepalive_does_not_undo_a_close_that_follows_it()
    {
        _listener = new ViewerListener(_databasePath, FreePort());
        var url = await _listener.StartAsync();
        using var client = new HttpClient();

        await client.PostAsync($"{url}/api/session/ping", content: null);
        await client.PostAsync($"{url}/api/session/closed", content: null);

        Assert.True(_listener.WindowHasGone());
    }

    [Fact]
    public void Nothing_has_gone_before_anything_has_started()
    {
        _listener = new ViewerListener(_databasePath, FreePort());

        Assert.False(_listener.IsRunning);
        Assert.False(_listener.WindowHasGone());
    }
}
