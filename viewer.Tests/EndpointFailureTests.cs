using System.Net;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Viewer.Tests;

/// <summary>
/// Drives the endpoints whose failure handlers used to catch every exception type
/// against a capture database that cannot be read.
///
/// The point of these is that narrowing those handlers did not change what a caller
/// sees. The frontend treats an unreadable capture as an empty one, so each endpoint
/// still has to answer with its documented empty shape rather than a 500.
/// </summary>
public class EndpointFailureTests : IClassFixture<BrokenDatabaseFactory>
{
    private readonly BrokenDatabaseFactory _factory;
    private readonly HttpClient _client;

    public EndpointFailureTests(BrokenDatabaseFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public void TheFixtureReallyIsUnreadable()
    {
        // Without this, every test below would also pass against a database that
        // is merely empty, which exercises the success path and proves nothing
        // about the failure handlers.
        var ex = Record.Exception(() =>
        {
            using var conn = new SqliteConnection($"Data Source={_factory.DbPath};Mode=ReadOnly");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='sample'";
            cmd.ExecuteScalar();
        });

        Assert.IsType<SqliteException>(ex);
    }

    [Fact]
    public async Task Range_ReportsAnEmptyRangeWhenTheDatabaseCannotBeRead()
    {
        var response = await _client.GetAsync("/api/range");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var root = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(JsonValueKind.Null, root.GetProperty("min").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("max").ValueKind);
    }

    [Fact]
    public async Task Health_ReportsTheCollectorStoppedWhenTheDatabaseCannotBeRead()
    {
        var response = await _client.GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var root = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.False(root.GetProperty("collectorRunning").GetBoolean());
        Assert.Equal(0, root.GetProperty("lastSampleTs").GetInt64());
    }

    [Fact]
    public async Task Health_StillReportsTheFileSizeWhenTheDatabaseCannotBeRead()
    {
        // The size comes from a file probe rather than a query, so it survives the
        // database being unreadable. This is the half of the health response that
        // the narrowed file-system handler protects.
        var response = await _client.GetAsync("/api/health");
        var root = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        Assert.True(root.TryGetProperty("dbSizeMb", out var size));
        Assert.True(size.GetDouble() >= 0);
    }

    [Theory]
    [InlineData("/api/timeline?from=0&to=99999999999999")]
    [InlineData("/api/processes?from=0&to=99999999999999")]
    [InlineData("/api/process/1?from=0&to=99999999999999")]
    [InlineData("/api/process-group/testapp.exe?from=0&to=99999999999999")]
    [InlineData("/api/alerts?days=1")]
    [InlineData("/api/baselines")]
    [InlineData("/api/heatmap?from=0&to=99999999999999&metric=cpu")]
    public async Task EveryQueryEndpoint_AnswersRatherThanFailingWhenTheDatabaseCannotBeRead(string url)
    {
        var response = await _client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
