using System.Net;
using System.Text.Json;
using Telltale.Viewer;

namespace Viewer.Tests;

/// <summary>
/// Drives the real /api/timeline endpoint with a granularity attached, against a
/// database of raw rows wide enough that a request can be clamped by the point
/// cap as well as honoured outright.
/// </summary>
public class TimelineGranularityEndpointTests : IClassFixture<WideRawRangeTestFactory>
{
    const long Hour = 3_600_000L;
    const long Day = 86_400_000L;

    private readonly HttpClient _client;

    public TimelineGranularityEndpointTests(WideRawRangeTestFactory factory)
    {
        _client = factory.CreateClient();
    }

    private async Task<JsonElement> Get(string query)
    {
        var response = await _client.GetAsync($"/api/timeline?{query}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    static long DayFrom => WideRawRangeTestFactory.MaxTs - Day + 1;

    [Fact]
    public async Task AnHourlyBucketOverADay_ReturnsAboutTwentyFourPoints()
    {
        JsonElement root = await Get($"from={DayFrom}&to={WideRawRangeTestFactory.MaxTs}&bucket={Hour}");

        Assert.Equal(Hour, root.GetProperty("bucketMs").GetInt64());
        Assert.Equal(Hour, root.GetProperty("bucketRequestMs").GetInt64());

        int points = root.GetProperty("points").GetArrayLength();
        Assert.InRange(points, 24, 25);

        // Every timestamp sits on an hour boundary, so the points are genuinely
        // grouped rather than a subset of the stored rows.
        foreach (JsonElement point in root.GetProperty("points").EnumerateArray())
            Assert.Equal(0, point.GetProperty("ts").GetInt64() % Hour);
    }

    [Fact]
    public async Task TheSameDayWithNoBucket_KeepsItsStoredResolution()
    {
        JsonElement root = await Get($"from={DayFrom}&to={WideRawRangeTestFactory.MaxTs}");

        // Zero is the endpoint saying the points are the recorded samples rather
        // than an aggregate of them.
        Assert.Equal(0, root.GetProperty("bucketMs").GetInt64());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("bucketRequestMs").ValueKind);
        Assert.Equal(0, root.GetProperty("minBucketMs").GetInt64());

        // The tiers still store five second detail even where the window is too
        // wide to hand all of it back, which is what tells the two limits apart.
        Assert.Equal(5_000, root.GetProperty("tierFloorMs").GetInt64());

        int points = root.GetProperty("points").GetArrayLength();
        Assert.InRange(points, Day / WideRawRangeTestFactory.IntervalMs - 1, Day / WideRawRangeTestFactory.IntervalMs + 1);
    }

    [Fact]
    public async Task AFineBucketOverTheWholeRecording_IsWidenedToStayWithinTheCap()
    {
        JsonElement root = await Get(
            $"from={WideRawRangeTestFactory.MinTs}&to={WideRawRangeTestFactory.MaxTs}&bucket=5000");

        long bucket = root.GetProperty("bucketMs").GetInt64();
        Assert.True(bucket > 5000, $"expected the request to be widened, got {bucket}ms");
        Assert.Equal(5000, root.GetProperty("bucketRequestMs").GetInt64());

        // The floor the caller was moved to is reported, so a caller can tell what
        // it is allowed to ask for next time, and the tier floor sits below it,
        // which is how a caller knows the cap moved this rather than retention.
        Assert.Equal(bucket, root.GetProperty("minBucketMs").GetInt64());
        Assert.True(root.GetProperty("tierFloorMs").GetInt64() < bucket);

        int points = root.GetProperty("points").GetArrayLength();
        Assert.InRange(points, 1, TierSelection.MaxRawOnlyPoints);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("-5000")]
    [InlineData("")]
    public async Task ABucketThatIsNotAWidth_IsIgnoredRatherThanRefused(string bucket)
    {
        JsonElement root = await Get($"from={DayFrom}&to={WideRawRangeTestFactory.MaxTs}&bucket={bucket}");

        Assert.Equal(JsonValueKind.Null, root.GetProperty("bucketRequestMs").ValueKind);
        Assert.Equal(0, root.GetProperty("bucketMs").GetInt64());
        Assert.True(root.GetProperty("points").GetArrayLength() > 0);
    }

    [Fact]
    public async Task AWindowWithNothingRecorded_ReportsNoFloorAtAll()
    {
        // Long before the recording starts. There is nothing to serve, so there is
        // nothing constraining what could be asked for either.
        long before = WideRawRangeTestFactory.MinTs - 30 * 86_400_000L;
        JsonElement root = await Get($"from={before}&to={before + 86_400_000L}");

        Assert.Empty(root.GetProperty("points").EnumerateArray());
        Assert.Equal(0, root.GetProperty("minBucketMs").GetInt64());
        Assert.Equal(0, root.GetProperty("bucketMs").GetInt64());
    }

    [Fact]
    public async Task ACoarseBucketOverTheWholeRecording_IsHonoured()
    {
        // Six hours across forty is well inside every bound, so nothing should
        // move it.
        JsonElement root = await Get(
            $"from={WideRawRangeTestFactory.MinTs}&to={WideRawRangeTestFactory.MaxTs}&bucket={6 * Hour}");

        Assert.Equal(6 * Hour, root.GetProperty("bucketMs").GetInt64());
        Assert.InRange(root.GetProperty("points").GetArrayLength(), 6, 8);
    }
}
