using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using Microsoft.Data.Sqlite;
using Telltale.App;
using Telltale.Collector;

namespace Host.Tests;

/// <summary>
/// The wipe endpoint as it actually runs, over a real socket, because everything
/// worth asserting about it is about what the socket does: who is refused, what
/// shape of request is rejected before anything is deleted, and what a failure
/// underneath looks like from outside.
/// </summary>
public class CaptureWipeEndpointTests : IAsyncLifetime
{
    readonly string _databasePath = Path.Combine(
        Path.GetTempPath(), $"telltale-wipe-{Guid.NewGuid():N}", "telltale.db");

    readonly FakeCaptureWipe _wipe = new();

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

    static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

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

    async Task<ViewerListener> Started(ICaptureWipe? wipe)
    {
        _listener = new ViewerListener(_databasePath, FreePort(), log: null, wipe: wipe);
        await _listener.StartAsync();
        return _listener;
    }

    static StringContent Body(string json) => new(json, Encoding.UTF8, "application/json");

    Task<HttpResponseMessage> Post(HttpClient client, string token, string json) =>
        client.PostAsync($"{_listener!.Url}{CaptureWipeEndpoint.Path}?s={token}", Body(json));

    [Fact]
    public async Task A_request_without_the_window_token_is_refused_and_nothing_is_deleted()
    {
        var listener = await Started(_wipe);
        using var client = new HttpClient();

        var response = await client.PostAsync(
            $"{listener.Url}{CaptureWipeEndpoint.Path}", Body("""{"scope":"all"}"""));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Null(_wipe.Called);
    }

    [Fact]
    public async Task A_request_with_the_wrong_token_is_refused_and_nothing_is_deleted()
    {
        var listener = await Started(_wipe);
        using var client = new HttpClient();

        var response = await client.PostAsync(
            $"{listener.Url}{CaptureWipeEndpoint.Path}?s=not-the-token", Body("""{"scope":"all"}"""));

        // Not Found rather than Forbidden, so a caller that guessed wrong is not
        // told it found the right endpoint.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Null(_wipe.Called);
    }

    [Fact]
    public async Task A_listener_with_no_recorder_to_wipe_through_does_not_serve_the_route()
    {
        var listener = await Started(wipe: null);
        using var client = new HttpClient();

        var response = await client.PostAsync(
            $"{listener.Url}{CaptureWipeEndpoint.Path}?s={TokenOf(listener.WindowUrl!)}",
            Body("""{"scope":"all"}"""));

        // This is the viewer executable's arrangement: it opens the capture file
        // read-only, so there is nowhere for a wipe to go and no route to ask on.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task The_route_answers_a_post_and_not_a_get()
    {
        var listener = await Started(_wipe);
        using var client = new HttpClient();

        var response = await client.GetAsync(
            $"{listener.Url}{CaptureWipeEndpoint.Path}?s={TokenOf(listener.WindowUrl!)}");

        // A GET is what a browser issues for a link, an image or a prefetch, and
        // none of those should be able to delete a recording by being followed.
        // The viewer's fallback answers every unmatched /api path, so this comes
        // back Not Found rather than Method Not Allowed, which suits: it does not
        // confirm to the caller that the route is there under another verb.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Null(_wipe.Called);
    }

    [Fact]
    public async Task Scope_all_wipes_everything_and_reports_what_went()
    {
        _wipe.Result = new CaptureWipeResult(1234, 56789);
        var listener = await Started(_wipe);
        using var client = new HttpClient();

        var response = await Post(client, TokenOf(listener.WindowUrl!), """{"scope":"all"}""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("all", _wipe.Called);

        var body = await response.Content.ReadFromJsonAsync<WipeReply>();
        Assert.Equal(1234, body!.RowsDeleted);
        Assert.Equal(56789, body.BytesFreed);
    }

    [Fact]
    public async Task Scope_range_wipes_the_span_it_was_given()
    {
        var listener = await Started(_wipe);
        using var client = new HttpClient();

        var response = await Post(
            client, TokenOf(listener.WindowUrl!), """{"scope":"range","from":1000,"to":2000}""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("range", _wipe.Called);
        Assert.Equal(1000, _wipe.From);
        Assert.Equal(2000, _wipe.To);
    }

    [Fact]
    public async Task A_wipe_that_deleted_nothing_is_a_normal_answer_rather_than_a_failure()
    {
        _wipe.Result = new CaptureWipeResult(0, 0);
        var listener = await Started(_wipe);
        using var client = new HttpClient();

        var response = await Post(
            client, TokenOf(listener.WindowUrl!), """{"scope":"range","from":1000,"to":2000}""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<WipeReply>();
        Assert.Equal(0, body!.RowsDeleted);
    }

    [Theory]
    // A scope that was not named at all. Inferring "everything" from a range that
    // failed to serialise is the worst way that mistake could land.
    [InlineData("""{}""")]
    [InlineData("""{"scope":"something-else"}""")]
    // A range missing one of its ends, or ending before it starts.
    [InlineData("""{"scope":"range"}""")]
    [InlineData("""{"scope":"range","from":1000}""")]
    [InlineData("""{"scope":"range","from":2000,"to":1000}""")]
    public async Task A_request_that_does_not_say_what_to_delete_is_rejected(string json)
    {
        var listener = await Started(_wipe);
        using var client = new HttpClient();

        var response = await Post(client, TokenOf(listener.WindowUrl!), json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(_wipe.Called);
    }

    [Fact]
    public async Task The_reply_uses_the_field_names_the_window_reads()
    {
        _wipe.Result = new CaptureWipeResult(7, 8);
        var listener = await Started(_wipe);
        using var client = new HttpClient();

        var response = await Post(client, TokenOf(listener.WindowUrl!), """{"scope":"all"}""");
        var body = await response.Content.ReadAsStringAsync();

        // Asserted on the raw text rather than by deserialising. ReadFromJsonAsync
        // matches property names case insensitively, so a reply that had drifted to
        // PascalCase would still round trip here while the window, which reads the
        // fields by name, quietly got undefined for both of them.
        Assert.Contains("\"rowsDeleted\"", body);
        Assert.Contains("\"bytesFreed\"", body);
    }

    [Fact]
    public async Task A_refusal_names_its_reason_in_the_field_the_window_reads()
    {
        var listener = await Started(_wipe);
        using var client = new HttpClient();

        var response = await Post(client, TokenOf(listener.WindowUrl!), """{}""");
        var body = await response.Content.ReadAsStringAsync();

        // Same reason. Without the field being called this, every refusal reaches
        // the person as a bare status code instead of the sentence written for it.
        Assert.Contains("\"error\"", body);
    }

    [Fact]
    public async Task A_body_sent_as_something_other_than_json_is_rejected_rather_than_throwing()
    {
        var listener = await Started(_wipe);
        using var client = new HttpClient();

        var response = await client.PostAsync(
            $"{listener.Url}{CaptureWipeEndpoint.Path}?s={TokenOf(listener.WindowUrl!)}",
            new StringContent("""{"scope":"all"}""", Encoding.UTF8, "text/plain"));

        // A well formed body under the wrong content type is still a bad request,
        // not a server error. Left to the JSON reader it would throw out of the
        // handler and reach the window as a bare 500 with nothing to show.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(_wipe.Called);
    }

    [Fact]
    public async Task A_body_that_is_not_json_is_rejected_rather_than_throwing()
    {
        var listener = await Started(_wipe);
        using var client = new HttpClient();

        var response = await Post(client, TokenOf(listener.WindowUrl!), "not json at all");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(_wipe.Called);
    }

    [Fact]
    public async Task A_busy_database_is_reported_as_a_conflict_the_window_can_show()
    {
        _wipe.Throw = new SqliteException("database is locked", 5);
        var listener = await Started(_wipe);
        using var client = new HttpClient();

        var response = await Post(client, TokenOf(listener.WindowUrl!), """{"scope":"all"}""");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("busy", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_recorder_that_is_shutting_down_says_so_rather_than_failing_silently()
    {
        _wipe.Throw = new ObjectDisposedException("Database");
        var listener = await Started(_wipe);
        using var client = new HttpClient();

        var response = await Post(client, TokenOf(listener.WindowUrl!), """{"scope":"all"}""");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    sealed record WipeReply(long RowsDeleted, long BytesFreed);

    /// <summary>
    /// Stands in for the recorder's database, so these tests are about the endpoint
    /// rather than about SQLite.
    /// </summary>
    sealed class FakeCaptureWipe : ICaptureWipe
    {
        public CaptureWipeResult Result { get; set; } = new(0, 0);

        /// <summary>Thrown instead of answering, for the failure cases.</summary>
        public Exception? Throw { get; set; }

        /// <summary>Which call arrived, or null when none did.</summary>
        public string? Called { get; private set; }

        public long From { get; private set; }

        public long To { get; private set; }

        public CaptureWipeResult All()
        {
            Called = "all";
            return Throw is null ? Result : throw Throw;
        }

        public CaptureWipeResult Range(long fromMs, long toMs)
        {
            Called = "range";
            From = fromMs;
            To = toMs;
            return Throw is null ? Result : throw Throw;
        }
    }
}
