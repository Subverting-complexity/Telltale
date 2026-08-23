using Microsoft.AspNetCore.Mvc.Testing;
using System.Net;
using System.Text.Json;

namespace Viewer.Tests;

public class ViewerApiTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public ViewerApiTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task GetRange_ReturnsJson()
    {
        var response = await _client.GetAsync("/api/range");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("min", out _));
        Assert.True(doc.RootElement.TryGetProperty("max", out _));
    }

    [Fact]
    public async Task GetRange_ReturnsNullsWhenNoDatabase()
    {
        var response = await _client.GetAsync("/api/range");
        var json = await response.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("min").ValueKind);
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("max").ValueKind);
    }

    [Fact]
    public async Task GetHealth_ReturnsJson()
    {
        var response = await _client.GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("collectorRunning", out _));
        Assert.True(doc.RootElement.TryGetProperty("dbSizeMb", out _));
    }

    [Fact]
    public async Task GetHealth_ReportsNotRunningWhenEmpty()
    {
        var response = await _client.GetAsync("/api/health");
        var json = await response.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.GetProperty("collectorRunning").GetBoolean());
    }

    [Fact]
    public async Task GetHealth_IncludesAllFields()
    {
        var response = await _client.GetAsync("/api/health");
        var json = await response.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("lastSampleTs", out _));
        Assert.True(root.TryGetProperty("sampleCostMs", out _));
        Assert.True(root.TryGetProperty("processCount", out _));
        Assert.True(root.TryGetProperty("storedCount", out _));
        Assert.True(root.TryGetProperty("dbSizeMb", out _));
    }

    [Fact]
    public async Task GetTimeline_RequiresParameters()
    {
        var response = await _client.GetAsync("/api/timeline");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GetProcesses_RequiresParameters()
    {
        var response = await _client.GetAsync("/api/processes");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GetAlerts_ReturnsJsonWithDefaultPeriod()
    {
        var response = await _client.GetAsync("/api/alerts");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(json);
        Assert.Equal(1, doc.RootElement.GetProperty("period").GetInt32());
        Assert.Equal(JsonValueKind.Array, doc.RootElement.GetProperty("alerts").ValueKind);
    }

    [Fact]
    public async Task GetAlerts_AcceptsDaysParameter()
    {
        var response = await _client.GetAsync("/api/alerts?days=30");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(json);
        Assert.Equal(30, doc.RootElement.GetProperty("period").GetInt32());
    }

    [Fact]
    public async Task GetAlerts_ClampsPeriodToValidRange()
    {
        var response = await _client.GetAsync("/api/alerts?days=999");
        var json = await response.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(json);
        Assert.Equal(365, doc.RootElement.GetProperty("period").GetInt32());
    }

    [Fact]
    public async Task GetAlerts_EmptyWhenNoData()
    {
        var response = await _client.GetAsync("/api/alerts?days=7");
        var json = await response.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(json);
        Assert.Empty(doc.RootElement.GetProperty("alerts").EnumerateArray().ToList());
    }

    [Theory]
    [InlineData("/api/range")]
    [InlineData("/api/health")]
    [InlineData("/api/alerts")]
    public async Task Endpoints_ReturnValidJson(string url)
    {
        var response = await _client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var content = await response.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(content);
        Assert.NotNull(doc);
    }
}
