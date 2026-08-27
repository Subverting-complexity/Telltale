using System.Net;
using System.Text.Json;

namespace Viewer.Tests;

/// <summary>
/// Covers the <c>latest</c> form of /api/processes, which answers what was
/// running at the newest reading in the window rather than what the window
/// averages out to. The seeded data ranks the two processes differently under
/// the two questions, so a query that silently aggregated would fail here.
/// </summary>
public class LatestReadingEndpointTests : IClassFixture<LatestReadingTestFactory>
{
    private readonly HttpClient _client;

    public LatestReadingEndpointTests(LatestReadingTestFactory factory)
    {
        _client = factory.CreateClient();
    }

    static string Window => $"from={LatestReadingTestFactory.FirstTs}&to={LatestReadingTestFactory.LatestTs}";

    [Fact]
    public async Task Range_RanksBySustainedCpu()
    {
        var root = await Get($"/api/processes?{Window}&sort=cpu");

        var names = Names(root);
        Assert.Equal(LatestReadingTestFactory.SteadyName, names[0]);

        double cpu = Find(root, LatestReadingTestFactory.SteadyName).GetProperty("cpuPct").GetDouble();
        Assert.InRange(cpu, LatestReadingTestFactory.SteadyCpuPct - 0.5, LatestReadingTestFactory.SteadyCpuPct + 0.5);
    }

    [Fact]
    public async Task Latest_RanksByTheNewestReadingInstead()
    {
        var root = await Get($"/api/processes?{Window}&sort=cpu&latest=true");

        var names = Names(root);
        Assert.Equal(LatestReadingTestFactory.SpikyName, names[0]);

        double cpu = Find(root, LatestReadingTestFactory.SpikyName).GetProperty("cpuPct").GetDouble();
        Assert.InRange(cpu, LatestReadingTestFactory.SpikyPeakCpuPct - 0.5, LatestReadingTestFactory.SpikyPeakCpuPct + 0.5);
    }

    [Fact]
    public async Task Latest_ReportsMemoryAndIoAtThatInstantRatherThanPeakAndTotal()
    {
        var root = await Get($"/api/processes?{Window}&latest=true");
        var steady = Find(root, LatestReadingTestFactory.SteadyName);

        // Over the range these would be the peak and the sum of sixty readings.
        Assert.Equal(LatestReadingTestFactory.SteadyPrivateMb, steady.GetProperty("privateMb").GetDouble(), 3);
        Assert.Equal(LatestReadingTestFactory.SteadyIoKb, steady.GetProperty("ioKb").GetDouble(), 3);
    }

    [Fact]
    public async Task Latest_SaysWhichReadingItAnswered()
    {
        var root = await Get($"/api/processes?{Window}&latest=true");

        Assert.Equal(LatestReadingTestFactory.LatestTs, root.GetProperty("latestTs").GetInt64());
    }

    [Fact]
    public async Task Range_LeavesTheReadingTimestampNull()
    {
        var root = await Get($"/api/processes?{Window}");

        Assert.Equal(JsonValueKind.Null, root.GetProperty("latestTs").ValueKind);
    }

    [Fact]
    public async Task Latest_HonoursTheEndOfTheWindowRatherThanTheRecording()
    {
        // Half the readings back. "Latest" means the newest reading in the window
        // on screen, not the newest one recorded, or a range that ends in the past
        // could never be read at all.
        long midpoint = LatestReadingTestFactory.FirstTs
            + (LatestReadingTestFactory.SampleCount / 2) * LatestReadingTestFactory.IntervalMs;

        var root = await Get($"/api/processes?from={LatestReadingTestFactory.FirstTs}&to={midpoint}&latest=true&sort=cpu");

        Assert.Equal(midpoint, root.GetProperty("latestTs").GetInt64());
        // spiky.exe is still idle at the midpoint, so steady.exe leads there.
        Assert.Equal(LatestReadingTestFactory.SteadyName, Names(root)[0]);
    }

    [Fact]
    public async Task Latest_OnAWindowWithNoReadingsReturnsNothing()
    {
        long from = LatestReadingTestFactory.BeforeAnythingTs;
        long to = from + 3_600_000L;

        var response = await _client.GetAsync($"/api/processes?from={from}&to={to}&latest=true");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var root = await ParseJson(response);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("latestTs").ValueKind);
        Assert.Equal(0, root.GetProperty("processes").GetArrayLength());
    }

    [Fact]
    public async Task Latest_AppliesTheNameFilter()
    {
        var root = await Get($"/api/processes?{Window}&latest=true&q=spiky");

        var names = Names(root);
        Assert.Equal([LatestReadingTestFactory.SpikyName], names);
    }

    [Fact]
    public async Task Latest_UngroupedReturnsTheInstanceAtThatReading()
    {
        var root = await Get($"/api/processes?{Window}&latest=true&group=false&sort=cpu");

        Assert.False(root.GetProperty("grouped").GetBoolean());
        Assert.Equal(LatestReadingTestFactory.LatestTs, root.GetProperty("latestTs").GetInt64());

        var top = root.GetProperty("processes").EnumerateArray().First();
        Assert.Equal(LatestReadingTestFactory.SpikyName, top.GetProperty("name").GetString());
        Assert.InRange(
            top.GetProperty("cpuPct").GetDouble(),
            LatestReadingTestFactory.SpikyPeakCpuPct - 0.5,
            LatestReadingTestFactory.SpikyPeakCpuPct + 0.5);
    }

    [Fact]
    public async Task LatestFalse_ReadsTheRange()
    {
        var explicitly = await Get($"/api/processes?{Window}&latest=false&sort=cpu");

        Assert.Equal(LatestReadingTestFactory.SteadyName, Names(explicitly)[0]);
        Assert.Equal(JsonValueKind.Null, explicitly.GetProperty("latestTs").ValueKind);
    }

    static string[] Names(JsonElement root) =>
        [.. root.GetProperty("processes").EnumerateArray().Select(p => p.GetProperty("name").GetString()!)];

    static JsonElement Find(JsonElement root, string name) =>
        root.GetProperty("processes").EnumerateArray()
            .First(p => p.GetProperty("name").GetString() == name);

    async Task<JsonElement> Get(string url)
    {
        var response = await _client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ParseJson(response);
    }

    static async Task<JsonElement> ParseJson(HttpResponseMessage response)
    {
        var json = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(json).RootElement.Clone();
    }
}
