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
///
/// These are regression cover rather than proof of the narrowing itself. They pass
/// against the code as it was before, because a bare catch returned the same empty
/// shapes. Proving the other half, that a non-SqliteException now escapes instead of
/// being disguised as an empty capture, needs a way to inject a fault into the query
/// path, and there is no seam for one.
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
        // Without this, every test below would also pass against a database that is
        // merely empty, which exercises the success path and proves nothing about the
        // failure handlers.
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

        var root = await ParseAsync(response);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("min").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("max").ValueKind);
    }

    [Fact]
    public async Task Health_ReportsTheCollectorStoppedWhenTheDatabaseCannotBeRead()
    {
        var response = await _client.GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var root = await ParseAsync(response);
        Assert.False(root.GetProperty("collectorRunning").GetBoolean());
        Assert.Equal(0, root.GetProperty("lastSampleTs").GetInt64());
    }

    [Fact]
    public async Task Health_StillReportsTheFileSizeWhenTheDatabaseCannotBeRead()
    {
        // The size comes from a file probe rather than a query, so it survives the
        // database being unreadable. This is the half of the health response that the
        // narrowed file-system handler protects.
        //
        // The fixture file is deliberately several megabytes so the reported size
        // rounds above zero. A probe that failed would leave the size at its default
        // of zero, so without that this assertion could not tell the two apart.
        var response = await _client.GetAsync("/api/health");
        var root = await ParseAsync(response);

        double expected = Math.Round(BrokenDatabaseFactory.FillerBytes / (1024.0 * 1024.0), 1);

        Assert.True(expected > 0, "The fixture must be large enough to report a non-zero size.");
        Assert.Equal(expected, root.GetProperty("dbSizeMb").GetDouble());
    }

    [Theory]
    [InlineData("/api/timeline?from=0&to=99999999999999", "points")]
    [InlineData("/api/processes?from=0&to=99999999999999", "processes")]
    [InlineData("/api/process/1?from=0&to=99999999999999", "points")]
    [InlineData("/api/process-group/testapp.exe?from=0&to=99999999999999", "points")]
    [InlineData("/api/alerts?days=1", "alerts")]
    [InlineData("/api/baselines?names=testapp.exe", "baselines")]
    [InlineData("/api/heatmap?from=0&to=99999999999999&metric=cpu", "buckets")]
    public async Task EveryQueryEndpoint_AnswersWithItsEmptyShapeWhenTheDatabaseCannotBeRead(
        string url, string collection)
    {
        var response = await _client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var items = (await ParseAsync(response)).GetProperty(collection);
        Assert.Equal(JsonValueKind.Array, items.ValueKind);
        Assert.Equal(0, items.GetArrayLength());
    }

    static async Task<JsonElement> ParseAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
}
