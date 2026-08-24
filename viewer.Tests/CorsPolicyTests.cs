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
/// </summary>
public class CorsPolicyTests : IClassFixture<TelltaleTestFactory>
{
    private readonly HttpClient _client;

    public CorsPolicyTests(TelltaleTestFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task ApiRequestFromAnotherOrigin_IsNotGrantedReadAccess()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/range");
        request.Headers.Add("Origin", "https://example.com");

        var response = await _client.SendAsync(request);

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
}
