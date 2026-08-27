using System.Net;
using System.Text.Json;

namespace Viewer.Tests;

/// <summary>
/// Drives /api/baselines over HTTP against a database whose rollup values are
/// known exactly.
///
/// The endpoint used to run one query per requested name. It now runs one query
/// for the whole list and groups by name, which is the kind of change that
/// looks harmless and quietly mixes two processes into one row, so these tests
/// ask about several names at once and check each answer separately.
/// </summary>
public class BaselineEndpointTests : IClassFixture<BaselineTestFactory>
{
    readonly HttpClient _client;

    public BaselineEndpointTests(BaselineTestFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task SeveralNamesInOneCall_EachKeepsItsOwnFigures()
    {
        var byName = await GetBaselines(
            $"{BaselineTestFactory.SteadyProcessName},{BaselineTestFactory.SwingingProcessName}");

        Assert.Equal(2, byName.Count);

        var steady = byName[BaselineTestFactory.SteadyProcessName];
        Assert.Equal(BaselineTestFactory.SteadyCpuPct, steady.GetProperty("avgCpu").GetDouble(), 2);
        Assert.Equal(0, steady.GetProperty("stddevCpu").GetDouble(), 2);
        Assert.Equal(BaselineTestFactory.SteadyPrivateMb, steady.GetProperty("avgMemoryMb").GetDouble(), 2);
        Assert.Equal(BaselineTestFactory.SteadyIoKb, steady.GetProperty("avgIoKb").GetDouble(), 2);

        var swinging = byName[BaselineTestFactory.SwingingProcessName];
        Assert.Equal(BaselineTestFactory.SwingingCpuMean, swinging.GetProperty("avgCpu").GetDouble(), 2);
        Assert.Equal(BaselineTestFactory.SwingingCpuStdDev, swinging.GetProperty("stddevCpu").GetDouble(), 2);
        Assert.Equal(BaselineTestFactory.SwingingPrivateMb, swinging.GetProperty("avgMemoryMb").GetDouble(), 2);
        Assert.Equal(BaselineTestFactory.SwingingIoKb, swinging.GetProperty("avgIoKb").GetDouble(), 2);
    }

    [Fact]
    public async Task AskingForOneName_AnswersOnlyAboutThatName()
    {
        var byName = await GetBaselines(BaselineTestFactory.SwingingProcessName);

        Assert.Equal(BaselineTestFactory.SwingingProcessName, Assert.Single(byName).Key);
        Assert.Equal(
            BaselineTestFactory.SwingingCpuMean,
            byName[BaselineTestFactory.SwingingProcessName].GetProperty("avgCpu").GetDouble(),
            2);
    }

    [Fact]
    public async Task ProcessRecordedUnderSeveralInstances_IsOneAnswerAcrossAllOfThem()
    {
        // The grouping mistake this refactor could actually make is grouping by
        // instance rather than by name, and every other test here would pass if
        // it did, because they use processes with a single instance each. A
        // process that restarts has several, which over seven days is the
        // ordinary case rather than the exception.
        var byName = await GetBaselines(BaselineTestFactory.RestartedProcessName);

        var restarted = byName[BaselineTestFactory.RestartedProcessName];

        // One row, not one per run.
        Assert.Single(byName);

        // The mean of both runs together. Either run on its own would report
        // its own figure instead, and neither run on its own has enough points
        // to clear the 24 hour minimum, so grouping by instance would drop this
        // process from the answer altogether.
        Assert.Equal(
            BaselineTestFactory.RestartedCombinedCpuMean,
            restarted.GetProperty("avgCpu").GetDouble(),
            2);

        double expectedHours = Math.Round(2 * BaselineTestFactory.RestartedRunPoints / 60.0, 1);
        Assert.Equal(expectedHours, restarted.GetProperty("dataHours").GetDouble(), 1);
    }

    [Fact]
    public async Task ProcessBelowTheDataMinimum_IsLeftOutEntirely()
    {
        // The 24 hour minimum has to survive as a per-name cut. A HAVING clause
        // written against the whole result rather than each group would let this
        // process through on the strength of the other two.
        var byName = await GetBaselines(
            $"{BaselineTestFactory.SteadyProcessName},{BaselineTestFactory.ShortHistoryProcessName}");

        Assert.True(byName.ContainsKey(BaselineTestFactory.SteadyProcessName));
        Assert.False(byName.ContainsKey(BaselineTestFactory.ShortHistoryProcessName),
            "A process with under 24 hours of rollup data must be absent, not present with zeroes.");
    }

    [Fact]
    public async Task UnknownNameAlongsideAKnownOne_DoesNotDisturbTheKnownOne()
    {
        var byName = await GetBaselines(
            $"nosuchprocess.exe,{BaselineTestFactory.SteadyProcessName}");

        Assert.Equal(BaselineTestFactory.SteadyProcessName, Assert.Single(byName).Key);
    }

    [Fact]
    public async Task RepeatedName_IsReportedOnce()
    {
        // Grouping by name collapses a duplicate rather than answering twice.
        var name = BaselineTestFactory.SteadyProcessName;
        var byName = await GetBaselines($"{name},{name}");

        Assert.Equal(name, Assert.Single(byName).Key);
    }

    [Fact]
    public async Task ReportedHoursMatchTheNumberOfMinutesRecorded()
    {
        var byName = await GetBaselines(BaselineTestFactory.SteadyProcessName);

        double expectedHours = Math.Round(BaselineTestFactory.LongHistoryPoints / 60.0, 1);
        Assert.Equal(
            expectedHours,
            byName[BaselineTestFactory.SteadyProcessName].GetProperty("dataHours").GetDouble(),
            1);
    }

    [Fact]
    public async Task PastTheFiftyNameCap_TheExtraNamesAreIgnored()
    {
        // Fifty filler names come first, so the real one falls outside the cap
        // and the response is empty. This pins the cap to the same fifty names
        // it always applied to, before any grouping happens.
        var filler = string.Join(",", Enumerable.Range(0, 50).Select(i => $"filler{i}.exe"));
        var byName = await GetBaselines($"{filler},{BaselineTestFactory.SteadyProcessName}");

        Assert.Empty(byName);
    }

    [Fact]
    public async Task NoNamesAtAll_IsAnEmptyAnswerRatherThanAnError()
    {
        var response = await _client.GetAsync("/api/baselines");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Empty(doc.RootElement.GetProperty("baselines").EnumerateArray());
    }

    /// <summary>
    /// Calls the endpoint and returns the baselines keyed by process name, so a
    /// test can assert about one process without depending on the row order.
    /// </summary>
    async Task<Dictionary<string, JsonElement>> GetBaselines(string names)
    {
        var response = await _client.GetAsync($"/api/baselines?names={Uri.EscapeDataString(names)}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Parsed into owned elements: the JsonDocument is disposed on the way
        // out of this method, and a JsonElement does not outlive its document.
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("baselines").EnumerateArray()
            .ToDictionary(b => b.GetProperty("name").GetString()!, b => b.Clone());
    }
}
