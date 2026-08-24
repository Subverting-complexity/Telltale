using Telltale.Viewer;

namespace Viewer.Tests;

public class TierSelectionTests
{
    const long Minute = 60_000L;
    const long Hour = 3_600_000L;
    const long Day = 86_400_000L;

    // A database shaped like a live one: raw holds the last 24 hours, the 1m
    // rollup holds the week before that, the 10m rollup everything older.
    static Dictionary<string, TierCoverage> Tiered(long now) => new()
    {
        ["machine"] = new TierCoverage(now - Day, now),
        ["machine_1m"] = new TierCoverage(now - 7 * Day, now - Day - Minute),
        ["machine_10m"] = new TierCoverage(now - 30 * Day, now - 7 * Day - Minute),
    };

    [Fact]
    public void RangeInsideRawTier_ReadsOnlyRawTable()
    {
        long now = 1_700_000_000_000L;
        var plan = TierSelection.Plan(now - 6 * Hour, now, isMachine: true, Tiered(now));

        Assert.True(plan.IsSingleRawTier);
        Assert.Equal("machine", Assert.Single(plan.Slices).Table);
    }

    [Fact]
    public void RangeSpanningRetentionBoundary_ReadsBothTiers()
    {
        long now = 1_700_000_000_000L;
        var plan = TierSelection.Plan(now - 2 * Day, now, isMachine: true, Tiered(now));

        Assert.Equal(new[] { "machine_1m", "machine" }, plan.Slices.Select(s => s.Table));
        Assert.False(plan.IsSingleRawTier);
    }

    [Fact]
    public void SlicesAreOrderedOldestFirstAndDoNotOverlap()
    {
        long now = 1_700_000_000_000L;
        var plan = TierSelection.Plan(now - 10 * Day, now, isMachine: true, Tiered(now));

        Assert.Equal(3, plan.Slices.Count);
        for (int i = 1; i < plan.Slices.Count; i++)
        {
            Assert.True(plan.Slices[i - 1].From <= plan.Slices[i].From);
            Assert.True(plan.Slices[i - 1].To < plan.Slices[i].From);
        }
    }

    [Fact]
    public void OverlappingTiers_FinerResolutionClaimsTheOverlap()
    {
        long now = 1_700_000_000_000L;
        var coverage = new Dictionary<string, TierCoverage>
        {
            ["machine"] = new TierCoverage(now - Day, now),
            ["machine_1m"] = new TierCoverage(now - 3 * Day, now),
        };

        var plan = TierSelection.Plan(now - 3 * Day, now, isMachine: true, coverage);

        var raw = plan.Slices.Single(s => s.Table == "machine");
        var rollup = plan.Slices.Single(s => s.Table == "machine_1m");

        Assert.Equal(now - Day, raw.From);
        Assert.Equal(now, raw.To);
        Assert.True(rollup.To < raw.From);
    }

    [Fact]
    public void MixedTiers_ShareOneBucketSizeSoResolutionDoesNotChangeMidSeries()
    {
        long now = 1_700_000_000_000L;
        var plan = TierSelection.Plan(now - 2 * Day, now, isMachine: true, Tiered(now));

        Assert.True(plan.Bucket > 0);
        Assert.True(plan.Bucket >= plan.CoarsestIntervalMs);
        Assert.Equal(0, plan.Bucket % plan.CoarsestIntervalMs);
    }

    [Fact]
    public void ShortSingleTierRange_IsNotBucketed()
    {
        long now = 1_700_000_000_000L;
        var plan = TierSelection.Plan(now - 30 * Minute, now, isMachine: true, Tiered(now));

        Assert.Equal(0, plan.Bucket);
    }

    // A machine whose raw table has been kept for a week, which RawRetentionHours
    // allows and which makes a week-long request raw-only.
    static Dictionary<string, TierCoverage> WeekOfRaw(long now) => new()
    {
        ["machine"] = new TierCoverage(now - 7 * Day, now),
    };

    [Fact]
    public void DayOfRawSamples_IsServedAtFullResolution()
    {
        long now = 1_700_000_000_000L;
        var plan = TierSelection.Plan(now - Day, now, isMachine: true, Tiered(now));

        Assert.True(plan.ServesFullResolution);
    }

    [Fact]
    public void DayTheClocksGoBack_IsStillServedAtFullResolution()
    {
        // That day is 25 hours long, so a bound written as a window width would
        // quietly drop the day view to bucketed resolution once a year.
        long now = 1_700_000_000_000L;
        var coverage = new Dictionary<string, TierCoverage>
        {
            ["machine"] = new TierCoverage(now - 25 * Hour, now),
        };

        var plan = TierSelection.Plan(now - 25 * Hour, now, isMachine: true, coverage);

        Assert.True(plan.ServesFullResolution);
    }

    [Fact]
    public void WeekOfRawSamples_IsNotServedAtFullResolution()
    {
        long now = 1_700_000_000_000L;
        var plan = TierSelection.Plan(now - 7 * Day, now, isMachine: true, WeekOfRaw(now));

        Assert.True(plan.IsSingleRawTier);
        Assert.False(plan.ServesFullResolution);
    }

    [Fact]
    public void WeekOfRawSamples_IsBucketedDownToTheOrdinaryPointCap()
    {
        long now = 1_700_000_000_000L;
        long from = now - 7 * Day;
        var plan = TierSelection.Plan(from, now, isMachine: true, WeekOfRaw(now));

        // Without the bound this window returns every 5 second row it holds,
        // which is roughly 121,000 points in one response.
        Assert.True(plan.Bucket > 0);
        Assert.InRange((now - from) / plan.Bucket, 1, 2 * TierSelection.MaxRawOnlyPoints);
        Assert.True((now - from) / plan.Bucket < 7 * Day / TierSelection.NativeIntervalMs("machine"));
    }

    [Fact]
    public void ARawOnlyWindowOverTheBoundAlwaysHasABucketToFallBackOn()
    {
        // The timeline handler only aggregates when a bucket exists, so a window
        // that loses the exemption without gaining a bucket would still return
        // every row. The two thresholds are far enough apart that it cannot.
        long now = 1_700_000_000_000L;
        long from = now - (TierSelection.MaxRawOnlyPoints + 1) * TierSelection.NativeIntervalMs("machine");
        var coverage = new Dictionary<string, TierCoverage>
        {
            ["machine"] = new TierCoverage(from, now),
        };

        var plan = TierSelection.Plan(from, now, isMachine: true, coverage);

        Assert.False(plan.ServesFullResolution);
        Assert.True(plan.Bucket > 0);
    }

    [Fact]
    public void MixedTierRange_IsNeverServedAtFullResolution()
    {
        long now = 1_700_000_000_000L;
        var plan = TierSelection.Plan(now - 2 * Day, now, isMachine: true, Tiered(now));

        Assert.False(plan.ServesFullResolution);
    }

    [Fact]
    public void FullResolutionIsMeasuredFromTheRowsRead_NotTheRangeAsked()
    {
        // Asking for a year when only the last six hours exist reads six hours of
        // rows, so it keeps full resolution.
        long now = 1_700_000_000_000L;
        var coverage = new Dictionary<string, TierCoverage>
        {
            ["machine"] = new TierCoverage(now - 6 * Hour, now),
        };

        var plan = TierSelection.Plan(now - 365 * Day, now, isMachine: true, coverage);

        Assert.True(plan.ServesFullResolution);
    }

    [Fact]
    public void EmptyDatabase_FallsBackToRawTierAndReturnsUsablePlan()
    {
        long now = 1_700_000_000_000L;
        var plan = TierSelection.Plan(now - 2 * Day, now, isMachine: true, new Dictionary<string, TierCoverage>());

        Assert.Equal("machine", Assert.Single(plan.Slices).Table);
        Assert.True(plan.IsSingleRawTier);
    }

    [Fact]
    public void RangeOutsideAllCoverage_FallsBackToRawTier()
    {
        long now = 1_700_000_000_000L;
        var plan = TierSelection.Plan(now - 400 * Day, now - 399 * Day, isMachine: true, Tiered(now));

        Assert.Equal("machine", Assert.Single(plan.Slices).Table);
    }

    [Fact]
    public void ProcessTiersAreSelectedForNonMachineQueries()
    {
        long now = 1_700_000_000_000L;
        var coverage = new Dictionary<string, TierCoverage>
        {
            ["sample"] = new TierCoverage(now - Day, now),
            ["sample_1m"] = new TierCoverage(now - 7 * Day, now - Day - Minute),
        };

        var plan = TierSelection.Plan(now - 2 * Day, now, isMachine: false, coverage);

        Assert.Equal(new[] { "sample_1m", "sample" }, plan.Slices.Select(s => s.Table));
    }

    [Fact]
    public void ResolutionNamesEveryTierRead()
    {
        long now = 1_700_000_000_000L;
        var plan = TierSelection.Plan(now - 2 * Day, now, isMachine: true, Tiered(now));

        Assert.Equal("machine_1m,machine", plan.Resolution);
    }

    [Fact]
    public void PlanWithNoSlicesIsRejected()
    {
        Assert.Throws<ArgumentException>(() => new TierPlan(Array.Empty<TierSlice>(), 0));
    }

    [Fact]
    public void ResolutionDoesNotRepeatATierServingTwoSlices()
    {
        var plan = new TierPlan(
            new[]
            {
                new TierSlice("machine_1m", 0, 99),
                new TierSlice("machine", 100, 199),
                new TierSlice("machine_1m", 200, 299),
            },
            60_000);

        Assert.Equal("machine_1m,machine", plan.Resolution);
    }

    [Theory]
    [InlineData("machine", 5_000)]
    [InlineData("sample", 5_000)]
    [InlineData("machine_1m", 60_000)]
    [InlineData("sample_1m", 60_000)]
    [InlineData("machine_10m", 600_000)]
    [InlineData("sample_10m", 600_000)]
    public void NativeIntervalMatchesTierGranularity(string table, long expected)
    {
        Assert.Equal(expected, TierSelection.NativeIntervalMs(table));
    }
}
