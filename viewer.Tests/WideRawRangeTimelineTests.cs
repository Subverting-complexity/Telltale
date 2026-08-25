using System.Net;
using System.Text.Json;
using Telltale.Viewer;

namespace Viewer.Tests;

/// <summary>
/// Drives the real /api/timeline endpoint over HTTP against a database whose raw
/// table covers more than the raw-only exemption allows.
///
/// The other tests around this behaviour either exercise the rule on its own or
/// run a copy of the handler's query. This one runs the handler itself, so
/// putting the old unbounded condition back turns it red.
/// </summary>
public class WideRawRangeTimelineTests : IClassFixture<WideRawRangeTestFactory>
{
    private readonly HttpClient _client;

    public WideRawRangeTimelineTests(WideRawRangeTestFactory factory)
    {
        _client = factory.CreateClient();
    }

    private async Task<JsonElement> GetTimeline(long from, long to)
    {
        var response = await _client.GetAsync($"/api/timeline?from={from}&to={to}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    [Fact]
    public async Task WideRawOnlyRange_ReturnsABoundedNumberOfPoints()
    {
        JsonElement root = await GetTimeline(WideRawRangeTestFactory.MinTs, WideRawRangeTestFactory.MaxTs);

        // Only the raw table holds anything, which is the case the endpoint used
        // to hand back in full.
        Assert.Equal("machine", root.GetProperty("resolution").GetString());

        int points = root.GetProperty("points").GetArrayLength();

        // Bucketing rounds down to a whole tier interval, so the ceiling is twice
        // the target the cap aims for.
        Assert.InRange(points, 1, 2 * TierSelection.MaxPoints);

        // And it is genuinely fewer than the rows behind it, so this fails rather
        // than passing vacuously if the endpoint stops aggregating.
        Assert.True(points < WideRawRangeTestFactory.SeededRowCount / 5,
            $"expected the range to be bucketed, got {points} points from "
            + $"{WideRawRangeTestFactory.SeededRowCount} seeded rows");
    }

    [Fact]
    public async Task ARangeSpanningTheWholeOfLong_IsStillBounded()
    {
        // The widest window a caller can express. Both the exemption bound and
        // the bucket computation subtract these, and a 64 bit subtraction across
        // them overflows, so this is the request that reads as narrowest unless
        // both are widened. Nothing in the UI produces it, but the endpoint takes
        // whatever from and to it is given.
        JsonElement root = await GetTimeline(long.MinValue, long.MaxValue);

        int points = root.GetProperty("points").GetArrayLength();

        Assert.InRange(points, 1, 2 * TierSelection.MaxPoints);
        Assert.True(points < WideRawRangeTestFactory.SeededRowCount / 5,
            $"expected the range to be bucketed, got {points} points from "
            + $"{WideRawRangeTestFactory.SeededRowCount} seeded rows");
    }

    [Fact]
    public async Task ADayInsideAWideRawTable_KeepsItsNativeResolution()
    {
        // The exemption is measured from the rows read, not the range asked for,
        // so a day-wide request keeps full detail even though the table around it
        // is far wider. This is the guarantee the day view depends on.
        long to = WideRawRangeTestFactory.MaxTs;
        long from = to - 86_400_000L + 1;

        JsonElement root = await GetTimeline(from, to);

        int points = root.GetProperty("points").GetArrayLength();
        long expected = 86_400_000L / WideRawRangeTestFactory.IntervalMs;

        Assert.InRange(points, expected - 1, expected + 1);
    }
}
