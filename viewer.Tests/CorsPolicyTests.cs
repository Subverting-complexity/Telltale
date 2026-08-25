using System.Net;
using System.Net.Http;

namespace Viewer.Tests;

/// <summary>
/// The viewer serves a record of every process the user has run. It used to
/// answer with a CORS policy that allowed any origin, which let any page the
/// user happened to visit read that history through their browser (issue #39).
///
/// Nothing legitimate needs the policy: the shipped build serves the frontend
/// from the viewer's own wwwroot, and during development Vite proxies /api from
/// its own server, so in both cases the browser only ever sees one origin.
///
/// Removing the policy alone is not the whole fix. The attack that survives it
/// is DNS rebinding, where a hostname the attacker controls is repointed at
/// loopback so the request counts as same-origin and no CORS check applies.
/// What stops that is host filtering, which the narrowed AllowedHosts setting
/// turns on, so these tests cover both halves.
/// </summary>
public class CorsPolicyTests : IClassFixture<TelltaleTestFactory>
{
    private readonly HttpClient _client;

    public CorsPolicyTests(TelltaleTestFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task ApiRequestFromAnotherOrigin_IsAnsweredButNotGrantedReadAccess()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/range");
        request.Headers.Add("Origin", "https://example.com");

        var response = await _client.SendAsync(request);

        // Asserting the status as well as the header, so that an endpoint which
        // has simply broken cannot be mistaken for one that is refusing access.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Without the allow-origin header a browser refuses to hand the response
        // body to the calling page, whatever the status code says.
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task PreflightFromAnotherOrigin_IsNotGrantedReadAccess()
    {
        var request = new HttpRequestMessage(HttpMethod.Options, "/api/range");
        request.Headers.Add("Origin", "https://example.com");
        request.Headers.Add("Access-Control-Request-Method", "GET");

        var response = await _client.SendAsync(request);

        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.False(response.Headers.Contains("Access-Control-Allow-Methods"));
    }

    [Fact]
    public async Task RequestForAHostTheViewerIsNotReachedOn_IsRejected()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/range");
        request.Headers.Host = "evil.example.com";

        var response = await _client.SendAsync(request);

        // This is the DNS rebinding case: the browser believes it is talking to
        // evil.example.com, which has been repointed at loopback, so the request
        // is same-origin and no CORS policy would have applied to it.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("localhost:5111")]
    [InlineData("localhost:5173")]   // the Vite dev proxy forwards the original host
    [InlineData("127.0.0.1:5111")]
    [InlineData("[::1]:5111")]
    public async Task RequestForALoopbackHost_IsAnswered(string host)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/range");
        request.Headers.Host = host;

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
