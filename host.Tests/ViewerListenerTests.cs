using System.Net;
using System.Net.Sockets;
using Telltale.App;
using Telltale.Viewer;

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

    /// <summary>The token Telltale puts in the URL it opens a window on.</summary>
    static string TokenOf(string windowUrl)
    {
        foreach (var pair in new Uri(windowUrl).Query.TrimStart('?').Split('&'))
        {
            var parts = pair.Split('=', 2);
            if (parts.Length == 2 && parts[0] == "s")
                return Uri.UnescapeDataString(parts[1]);
        }

        throw new InvalidOperationException($"No token in {windowUrl}");
    }

    static Task<HttpResponseMessage> Ping(HttpClient client, ViewerListener listener, string windowId) =>
        client.PostAsync(
            $"{listener.Url}/api/session/ping?s={TokenOf(listener.WindowUrl!)}&c={windowId}", content: null);

    static Task<HttpResponseMessage> Close(HttpClient client, ViewerListener listener, string windowId) =>
        client.PostAsync(
            $"{listener.Url}/api/session/closed?s={TokenOf(listener.WindowUrl!)}&c={windowId}", content: null);

    [Fact]
    public async Task It_listens_on_the_configured_port()
    {
        var port = FreePort();
        _listener = new ViewerListener(_databasePath, port);

        await _listener.StartAsync();

        Assert.Equal(port, PortOf(_listener.Url!));
        Assert.True(_listener.IsRunning);
    }

    [Fact]
    public async Task It_binds_loopback_and_nothing_else()
    {
        _listener = new ViewerListener(_databasePath, FreePort());

        await _listener.StartAsync();

        // Asserted on the address rather than left to a Kestrel default. A wildcard
        // bind would put a person's capture history on every interface the machine
        // has, which is the one thing this application must never do.
        Assert.Equal("127.0.0.1", new Uri(_listener.Url!).Host);
    }

    [Fact]
    public async Task It_refuses_a_request_that_arrives_under_another_name()
    {
        // A name that resolves to 127.0.0.1 is how a page reaches a loopback server
        // it is otherwise same-origin with. Host filtering is what turns that away,
        // and it only works because AllowedHosts is set rather than defaulted.
        _listener = new ViewerListener(_databasePath, FreePort());
        await _listener.StartAsync();

        using var client = new HttpClient();
        var request = new HttpRequestMessage(HttpMethod.Get, $"{_listener.Url}/api/health");
        request.Headers.Host = "telltale.evil.example.com";

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task It_serves_the_same_api_the_viewer_serves()
    {
        _listener = new ViewerListener(_databasePath, FreePort());
        await _listener.StartAsync();

        using var client = new HttpClient();
        var response = await client.GetAsync($"{_listener.Url}/api/health");

        Assert.True(response.IsSuccessStatusCode);
    }

    [Fact]
    public async Task A_taken_port_falls_back_to_one_Windows_chooses()
    {
        var taken = FreePort();
        using var squatter = new TcpListener(IPAddress.Loopback, taken);
        squatter.Start();

        _listener = new ViewerListener(_databasePath, taken);
        await _listener.StartAsync();

        Assert.NotEqual(taken, PortOf(_listener.Url!));
        Assert.Equal("127.0.0.1", new Uri(_listener.Url!).Host);

        // Falling back is only worth anything if the result actually works.
        using var client = new HttpClient();
        Assert.True((await client.GetAsync($"{_listener.Url}/api/health")).IsSuccessStatusCode);
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
        await _listener.StartAsync();
        var url = _listener.Url!;

        await _listener.StopAsync();

        Assert.False(_listener.IsRunning);
        Assert.Null(_listener.Url);
        Assert.Null(_listener.WindowUrl);

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

        await _listener.StartAsync();

        using var client = new HttpClient();
        Assert.True((await client.GetAsync($"{_listener.Url}/api/health")).IsSuccessStatusCode);
    }

    [Fact]
    public async Task A_restart_issues_a_new_token()
    {
        // The old one stops working with the listener that minted it, so a page
        // left open from a previous session cannot drive the new one.
        _listener = new ViewerListener(_databasePath, FreePort());
        await _listener.StartAsync();
        var first = TokenOf(_listener.WindowUrl!);

        await _listener.StopAsync();
        await _listener.StartAsync();

        Assert.NotEqual(first, TokenOf(_listener.WindowUrl!));
    }

    [Fact]
    public async Task The_window_url_carries_the_token_and_the_plain_url_does_not()
    {
        _listener = new ViewerListener(_databasePath, FreePort());
        await _listener.StartAsync();

        Assert.StartsWith(_listener.Url!, _listener.WindowUrl!);
        Assert.Contains("?s=", _listener.WindowUrl!);
        Assert.DoesNotContain("?", _listener.Url!);
    }

    [Fact]
    public async Task A_window_saying_it_closed_is_what_ends_the_session()
    {
        _listener = new ViewerListener(_databasePath, FreePort());
        await _listener.StartAsync();
        using var client = new HttpClient();

        Assert.True((await Ping(client, _listener, "window-a")).IsSuccessStatusCode);
        Assert.False(_listener.EveryWindowHasGone());

        Assert.True((await Close(client, _listener, "window-a")).IsSuccessStatusCode);

        // Not immediately: the last window has to stay gone for the settling
        // period, which is what makes a reload safe.
        Assert.False(_listener.EveryWindowHasGone());
        await Task.Delay(ViewerListener.Settle + TimeSpan.FromSeconds(1));
        Assert.True(_listener.EveryWindowHasGone());
    }

    [Fact]
    public async Task Closing_one_window_leaves_another_one_serving()
    {
        _listener = new ViewerListener(_databasePath, FreePort());
        await _listener.StartAsync();
        using var client = new HttpClient();

        await Ping(client, _listener, "window-a");
        await Ping(client, _listener, "window-b");
        await Close(client, _listener, "window-a");

        await Task.Delay(ViewerListener.Settle + TimeSpan.FromSeconds(1));

        Assert.False(_listener.EveryWindowHasGone());
    }

    [Fact]
    public async Task A_request_without_the_token_cannot_touch_the_session()
    {
        // This is any other page the user has open. A POST with no body and no
        // custom header is a request a browser sends cross-origin without asking
        // first, and the reply being unreadable does not undo the side effect. So
        // the side effect has to not happen.
        _listener = new ViewerListener(_databasePath, FreePort());
        await _listener.StartAsync();
        using var client = new HttpClient();

        await Ping(client, _listener, "window-a");

        var forged = await client.PostAsync(
            $"{_listener.Url}/api/session/closed?s=WRONG&c=window-a", content: null);

        Assert.Equal(HttpStatusCode.NotFound, forged.StatusCode);
        await Task.Delay(ViewerListener.Settle + TimeSpan.FromSeconds(1));
        Assert.False(_listener.EveryWindowHasGone());
    }

    [Fact]
    public async Task A_request_without_the_token_cannot_hold_the_listener_open()
    {
        // The other half of the same problem. A page that could ping would keep the
        // socket open for exactly the hours this design exists to close it.
        _listener = new ViewerListener(_databasePath, FreePort());
        await _listener.StartAsync();
        using var client = new HttpClient();

        var forged = await client.PostAsync(
            $"{_listener.Url}/api/session/ping?s=WRONG&c=window-a", content: null);

        Assert.Equal(HttpStatusCode.NotFound, forged.StatusCode);
    }

    [Fact]
    public async Task Reading_the_api_does_not_hold_the_listener_open()
    {
        // /api/health is reachable from a bare image tag on any page. If ordinary
        // requests counted as the window being there, that alone would keep the
        // socket up indefinitely. Only a window holding the token counts.
        _listener = new ViewerListener(_databasePath, FreePort());
        await _listener.StartAsync();
        using var client = new HttpClient();

        await Ping(client, _listener, "window-a");
        await Close(client, _listener, "window-a");

        var deadline = DateTime.UtcNow + ViewerListener.Settle + TimeSpan.FromSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            await client.GetAsync($"{_listener.Url}/api/health");
            await Task.Delay(200);
        }

        Assert.True(_listener.EveryWindowHasGone());
    }

    [Fact]
    public async Task A_session_request_that_names_no_window_is_refused()
    {
        _listener = new ViewerListener(_databasePath, FreePort());
        await _listener.StartAsync();
        using var client = new HttpClient();

        var response = await client.PostAsync(
            $"{_listener.Url}/api/session/ping?s={TokenOf(_listener.WindowUrl!)}", content: null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public void Nothing_has_gone_before_anything_has_started()
    {
        _listener = new ViewerListener(_databasePath, FreePort());

        Assert.False(_listener.IsRunning);
        Assert.False(_listener.EveryWindowHasGone());
    }

    [Fact]
    public void The_two_halves_agree_on_the_default_port()
    {
        // The recorder's configuration and the viewer's own default are declared
        // separately, because neither project may reference the other. Nothing but
        // this stops them drifting apart.
        Assert.Equal(
            Telltale.Collector.TelltaleConfig.DefaultViewerPort,
            ViewerDefaults.Port);
    }
}
