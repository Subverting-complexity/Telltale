using System.Net;
using System.Text.Json;

namespace Viewer.Tests;

/// <summary>
/// Drives the real viewer endpoints over HTTP against a database seeded with
/// known values, so a change to the aggregate expressions in Program.cs turns
/// these tests red even if the TierSql-level tests stay green.
/// </summary>
public class EndpointDataTests : IClassFixture<EndpointTestFactory>
{
    private readonly HttpClient _client;

    public EndpointDataTests(EndpointTestFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Alerts_ReturnsProcessWithKnownCpuAboveThreshold()
    {
        var response = await _client.GetAsync("/api/alerts?days=365");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var root = await ParseJson(response);
        var alerts = root.GetProperty("alerts");
        Assert.True(alerts.GetArrayLength() > 0, "Expected at least one alert from the seeded data");

        var alert = alerts.EnumerateArray()
            .First(a => a.GetProperty("name").GetString() == EndpointTestFactory.TestProcessName);

        double avgCpu = alert.GetProperty("avgCpuPct").GetDouble();
        Assert.InRange(avgCpu, EndpointTestFactory.RawCpuPct - 1, EndpointTestFactory.RawCpuPct + 1);

        double peakCpu = alert.GetProperty("peakCpuPct").GetDouble();
        Assert.True(peakCpu >= EndpointTestFactory.RawCpuPct - 1,
            $"Expected peak CPU near {EndpointTestFactory.RawCpuPct}, got {peakCpu}");

        long sampleCount = alert.GetProperty("sampleCount").GetInt64();
        Assert.True(sampleCount >= EndpointTestFactory.SampleCount,
            $"Expected at least {EndpointTestFactory.SampleCount} samples, got {sampleCount}");
    }

    [Fact]
    public async Task Alerts_ReturnsReasons()
    {
        var root = await ParseJson(await _client.GetAsync("/api/alerts?days=365"));
        var alert = root.GetProperty("alerts").EnumerateArray()
            .First(a => a.GetProperty("name").GetString() == EndpointTestFactory.TestProcessName);

        var reasons = alert.GetProperty("reasons");
        Assert.True(reasons.GetArrayLength() > 0, "Expected at least one reason for the alert");
    }

    [Fact]
    public async Task Heatmap_ReturnsBucketsWithKnownValues()
    {
        long from = EndpointTestFactory.FourHoursAgo;
        long to = EndpointTestFactory.Now;

        var response = await _client.GetAsync($"/api/heatmap?from={from}&to={to}&metric=cpu");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var root = await ParseJson(response);
        Assert.Equal("cpu", root.GetProperty("metric").GetString());

        var buckets = root.GetProperty("buckets");
        Assert.True(buckets.GetArrayLength() > 0, "Expected at least one heatmap bucket");

        var bucket = buckets.EnumerateArray().First();
        double avg = bucket.GetProperty("avg").GetDouble();
        Assert.True(avg > 0, $"Expected a non-zero average, got {avg}");

        long count = bucket.GetProperty("count").GetInt64();
        Assert.True(count > 0, $"Expected a non-zero sample count, got {count}");
    }

    [Fact]
    public async Task Processes_ReturnsGroupedProcessWithKnownValues()
    {
        long from = EndpointTestFactory.FourHoursAgo;
        long to = EndpointTestFactory.Now;

        var response = await _client.GetAsync($"/api/processes?from={from}&to={to}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var root = await ParseJson(response);
        Assert.True(root.GetProperty("grouped").GetBoolean());

        var processes = root.GetProperty("processes");
        Assert.True(processes.GetArrayLength() > 0, "Expected at least one process");

        var proc = processes.EnumerateArray()
            .First(p => p.GetProperty("name").GetString() == EndpointTestFactory.TestProcessName);

        double cpuPct = proc.GetProperty("cpuPct").GetDouble();
        Assert.InRange(cpuPct, EndpointTestFactory.RawCpuPct - 1, EndpointTestFactory.RawCpuPct + 1);

        double privateMb = proc.GetProperty("privateMb").GetDouble();
        Assert.True(privateMb > 0, $"Expected non-zero memory, got {privateMb}");
    }

    [Fact]
    public async Task Processes_UngroupedReturnsInstanceWithKnownValues()
    {
        long from = EndpointTestFactory.FourHoursAgo;
        long to = EndpointTestFactory.Now;

        var response = await _client.GetAsync($"/api/processes?from={from}&to={to}&group=false");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var root = await ParseJson(response);
        Assert.False(root.GetProperty("grouped").GetBoolean());

        var processes = root.GetProperty("processes");
        Assert.True(processes.GetArrayLength() > 0, "Expected at least one process instance");

        var proc = processes.EnumerateArray()
            .First(p => p.GetProperty("name").GetString() == EndpointTestFactory.TestProcessName);

        double cpuPct = proc.GetProperty("cpuPct").GetDouble();
        Assert.InRange(cpuPct, EndpointTestFactory.RawCpuPct - 1, EndpointTestFactory.RawCpuPct + 1);
    }

    [Fact]
    public async Task Timeline_ReturnsPointsWithKnownValues()
    {
        long from = EndpointTestFactory.FourHoursAgo;
        long to = EndpointTestFactory.Now;

        var response = await _client.GetAsync($"/api/timeline?from={from}&to={to}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var root = await ParseJson(response);
        Assert.True(root.TryGetProperty("resolution", out _));

        var points = root.GetProperty("points");
        Assert.True(points.GetArrayLength() > 0, "Expected at least one timeline point");

        var point = points.EnumerateArray().First();
        Assert.True(point.TryGetProperty("ts", out _));
        Assert.True(point.TryGetProperty("cpuPct", out _));
        Assert.True(point.TryGetProperty("memoryAvailMb", out _));
    }

    [Fact]
    public async Task ProcessDetail_ReturnsPointsForKnownInstance()
    {
        long from = EndpointTestFactory.FourHoursAgo;
        long to = EndpointTestFactory.Now;

        var response = await _client.GetAsync($"/api/process/1?from={from}&to={to}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var root = await ParseJson(response);
        var info = root.GetProperty("info");
        Assert.Equal(EndpointTestFactory.TestProcessName, info.GetProperty("name").GetString());
        Assert.Equal(EndpointTestFactory.TestProcessPath, info.GetProperty("path").GetString());

        var points = root.GetProperty("points");
        Assert.True(points.GetArrayLength() > 0, "Expected at least one point for the process");
    }

    [Fact]
    public async Task ProcessGroup_ReturnsPointsForKnownName()
    {
        long from = EndpointTestFactory.FourHoursAgo;
        long to = EndpointTestFactory.Now;

        var response = await _client.GetAsync(
            $"/api/process-group/{EndpointTestFactory.TestProcessName}?from={from}&to={to}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var root = await ParseJson(response);
        Assert.Equal(EndpointTestFactory.TestProcessName, root.GetProperty("name").GetString());

        var points = root.GetProperty("points");
        Assert.True(points.GetArrayLength() > 0, "Expected at least one group point");
    }

    private static async Task<JsonElement> ParseJson(HttpResponseMessage response)
    {
        var json = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(json).RootElement.Clone();
    }
}
