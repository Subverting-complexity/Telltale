using System.Net;
using Microsoft.Extensions.Logging;

namespace Viewer.Tests;

/// <summary>
/// Checks that an endpoint which cannot read the capture database says so.
///
/// Every handler answers a failed query with an empty result, which is right for
/// the frontend but indistinguishable from a capture that genuinely holds nothing.
/// These tests are what stops that failure going unrecorded again: the empty
/// response is still required, and now a warning carrying the exception has to be
/// there beside it.
/// </summary>
public class EndpointFailureLoggingTests : IClassFixture<BrokenDatabaseFactory>
{
    private readonly BrokenDatabaseFactory _factory;
    private readonly HttpClient _client;

    public EndpointFailureLoggingTests(BrokenDatabaseFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Theory]
    [InlineData("/api/timeline?from=0&to=99999999999999", "/api/timeline")]
    [InlineData("/api/processes?from=0&to=99999999999999", "/api/processes")]
    [InlineData("/api/process/1?from=0&to=99999999999999", "/api/process/{id:long}")]
    [InlineData("/api/process-group/testapp.exe?from=0&to=99999999999999", "/api/process-group/{name}")]
    [InlineData("/api/alerts?days=1", "/api/alerts")]
    [InlineData("/api/baselines?names=testapp.exe", "/api/baselines")]
    [InlineData("/api/heatmap?from=0&to=99999999999999&metric=cpu", "/api/heatmap")]
    [InlineData("/api/range", "/api/range")]
    [InlineData("/api/health", "/api/health")]
    public async Task AnUnreadableDatabaseIsReportedRatherThanDiscarded(string url, string endpoint)
    {
        var response = await _client.GetAsync(url);

        // Logging the reason must not change what the caller receives. The empty
        // shape each endpoint returns is asserted in EndpointFailureTests; what
        // matters here is that adding the log did not turn any of them into a 500.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var warning = _factory.Logs.Entries.LastOrDefault(
            e => e.Level == LogLevel.Warning && e.Message.Contains(endpoint, StringComparison.Ordinal));

        Assert.True(warning is not null,
            $"Expected a warning naming {endpoint}. Recorded warnings: " +
            string.Join(" | ", _factory.Logs.Entries
                .Where(e => e.Level == LogLevel.Warning)
                .Select(e => e.Message)));

        // The exception itself has to travel with the message. Without it the log
        // says a query failed but not what SQLite objected to, which is the part
        // that makes a corrupt or unexpected database diagnosable.
        Assert.NotNull(warning!.Exception);
    }
}
