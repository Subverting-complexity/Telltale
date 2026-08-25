using System.Net;
using Microsoft.Extensions.Logging;

namespace Viewer.Tests;

/// <summary>
/// Covers the health endpoint when the configured database path itself is unusable.
///
/// Narrowing the file probe made this a distinct case. The handler has to keep
/// answering, because the frontend discards a failed health poll without saying
/// anything, so letting the failure escape as a 500 would make the status bar
/// disappear with nothing anywhere explaining why. It also has to say something,
/// because a misconfigured path is a standing fault rather than a transient one.
/// </summary>
public class UnusablePathHealthTests : IClassFixture<UnusablePathFactory>
{
    private readonly UnusablePathFactory _factory;
    private readonly HttpClient _client;

    public UnusablePathHealthTests(UnusablePathFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Health_StillAnswersWhenTheConfiguredPathIsUnusable()
    {
        var response = await _client.GetAsync("/api/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Health_ReportsAnUnusablePathRatherThanDiscardingIt()
    {
        await _client.GetAsync("/api/health");

        var warning = _factory.Logs.Entries.LastOrDefault(
            e => e.Level == LogLevel.Warning
                 && e.Message.Contains("path", StringComparison.OrdinalIgnoreCase));

        Assert.True(warning is not null,
            "Expected a warning about the configured path. Recorded warnings: " +
            string.Join(" | ", _factory.Logs.Entries
                .Where(e => e.Level == LogLevel.Warning)
                .Select(e => e.Message)));

        Assert.NotNull(warning!.Exception);
    }
}
