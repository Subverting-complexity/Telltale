using System.Text.Json;

namespace Viewer.Tests;

public class RangeWithDataTests : IClassFixture<SeededTelltaleTestFactory>
{
    private readonly HttpClient _client;

    public RangeWithDataTests(SeededTelltaleTestFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task GetRange_ReturnsKnownRangeWhenDatabaseHasData()
    {
        var response = await _client.GetAsync("/api/range");
        var json = await response.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(json);

        Assert.Equal(SeededTelltaleTestFactory.MinTs, doc.RootElement.GetProperty("min").GetInt64());
        Assert.Equal(SeededTelltaleTestFactory.MaxTs, doc.RootElement.GetProperty("max").GetInt64());
    }
}
