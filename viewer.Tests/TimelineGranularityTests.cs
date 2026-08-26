using Telltale.Viewer;

namespace Viewer.Tests;

/// <summary>
/// The rules that turn a caller's requested bucket into one the selected tiers
/// can actually serve. A request is always answered, never refused, so every
/// case here is about which way it is moved and by how much.
/// </summary>
public class TimelineGranularityTests
{
    const long Second = 1_000L;
    const long Minute = 60_000L;
    const long Hour = 3_600_000L;
    const long Day = 86_400_000L;

    const long Now = 1_700_000_000_000L;

    /// <summary>The same shape as a live database: raw for a day, then rollups.</summary>
    static Dictionary<string, TierCoverage> Tiered(long now) => new()
    {
        ["machine"] = new TierCoverage(now - Day, now),
        ["machine_1m"] = new TierCoverage(now - 7 * Day, now - Day - Minute),
        ["machine_10m"] = new TierCoverage(now - 30 * Day, now - 7 * Day - Minute),
    };

    static TierPlan PlanFor(long from, long to, long? requested) =>
        TierSelection.Plan(from, to, isMachine: true, Tiered(Now), requested);

    [Fact]
    public void NoRequest_LeavesTheAutomaticChoiceAlone()
    {
        // A day of raw rows is the case the automatic rule serves unaggregated,
        // and asking for nothing must not change that.
        var day = PlanFor(Now - Day, Now, requested: null);
        Assert.Null(day.RequestedBucket);
        Assert.True(day.ServesFullResolution);
        Assert.Equal(0, day.EffectiveBucket);

        // A month spans all three tiers and is bucketed, and the effective bucket
        // is the automatic one rather than something new.
        var month = PlanFor(Now - 30 * Day, Now, requested: null);
        Assert.Null(month.RequestedBucket);
        Assert.False(month.ServesFullResolution);
        Assert.Equal(month.Bucket, month.EffectiveBucket);
    }

    [Fact]
    public void AnHourlyBucketOverADay_IsServedAsAsked()
    {
        var plan = PlanFor(Now - Day, Now, requested: Hour);

        Assert.Equal(Hour, plan.RequestedBucket);
        Assert.Equal(Hour, plan.EffectiveBucket);

        // The automatic choice for the same window is full resolution, so this is
        // genuinely the request being honoured rather than a coincidence.
        Assert.True(plan.ServesFullResolution);
    }

    [Fact]
    public void ABucketFinerThanTheTiersHold_IsRaisedToWhatTheyHold()
    {
        // Three weeks back is served by the 10 minute rollup alone. Five seconds
        // of detail was thrown away when those rows were promoted.
        var plan = PlanFor(Now - 21 * Day, Now - 20 * Day, requested: 5 * Second);

        Assert.Equal("machine_10m", plan.Resolution);
        Assert.Equal(600_000L, plan.EffectiveBucket);
        Assert.Equal(5 * Second, plan.RequestedBucket);
    }

    [Fact]
    public void ABucketThatWouldReturnTooManyPoints_IsRaisedToTheCap()
    {
        // A week of five second buckets is about 120,000 points, six times what a
        // single response is allowed to carry.
        var plan = PlanFor(Now - 7 * Day, Now, requested: 5 * Second);

        long points = 7 * Day / plan.EffectiveBucket;
        Assert.InRange(points, 1, TierSelection.MaxRawOnlyPoints);

        // And it stopped at the cap rather than overshooting to something coarser.
        Assert.True(plan.EffectiveBucket <= 2 * TierSelection.SmallestBucketWithinCap(7 * Day, plan.CoarsestIntervalMs),
            $"expected the cap floor, got {plan.EffectiveBucket}ms");
    }

    [Fact]
    public void ABucketBetweenTwoTierIntervals_IsRoundedDownToAWholeOne()
    {
        // 95 seconds against a 60 second tier. Rounding up would give less detail
        // than asked for; leaving it would average part of a rollup row against
        // whole ones.
        var plan = PlanFor(Now - 3 * Day, Now - 2 * Day, requested: 95 * Second);

        Assert.Equal("machine_1m", plan.Resolution);
        Assert.Equal(Minute, plan.EffectiveBucket);
    }

    [Fact]
    public void ARawTierAskedForItsOwnInterval_IsServedUnbucketed()
    {
        var plan = PlanFor(Now - 6 * Hour, Now, requested: 5 * Second);

        Assert.True(plan.IsSingleRawTier);
        Assert.Equal(0, plan.EffectiveBucket);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData(long.MinValue)]
    public void AnImpossibleRequest_ReadsAsNoRequest(long requested)
    {
        var plan = PlanFor(Now - Day, Now, requested);

        Assert.Null(plan.RequestedBucket);
        Assert.Equal(0, plan.EffectiveBucket);
    }

    [Fact]
    public void AnEnormousRequest_IsHonouredRatherThanOverflowing()
    {
        // Nothing in the UI asks for this, but the endpoint takes whatever number
        // it is given and the rounding arithmetic must not wrap.
        var plan = PlanFor(Now - Day, Now, requested: long.MaxValue);

        Assert.True(plan.EffectiveBucket > 0);
        Assert.Equal(0, plan.EffectiveBucket % plan.CoarsestIntervalMs);
    }

    [Fact]
    public void SmallestServableBucket_IsWhatAnImpossiblyFineRequestClampsTo()
    {
        // On a raw day it is zero, meaning the window can be served exactly as
        // recorded, so no option a caller could offer is out of reach.
        Assert.Equal(0, PlanFor(Now - Day, Now, requested: null).SmallestServableBucket);

        // On the 10 minute rollup it is that tier's interval.
        Assert.Equal(600_000L, PlanFor(Now - 21 * Day, Now - 20 * Day, requested: null).SmallestServableBucket);

        // Over a week the cap binds before the tiers do, so the floor is higher
        // than any single tier's interval.
        var week = PlanFor(Now - 7 * Day, Now, requested: null);
        Assert.True(week.SmallestServableBucket >= week.CoarsestIntervalMs);
        Assert.InRange(7 * Day / week.SmallestServableBucket, 1, TierSelection.MaxRawOnlyPoints);
    }

    [Fact]
    public void AWindowSpanningTheWholeOfLong_StillProducesAFiniteBucket()
    {
        // The widest window expressible. Both the span and the cap arithmetic
        // overflow a 64 bit subtraction, so this is the request that would come
        // back unbounded if either were left narrow.
        var plan = PlanFor(long.MinValue, long.MaxValue, requested: 5 * Second);

        Assert.True(plan.EffectiveBucket > 0);
        Assert.Equal(0, plan.EffectiveBucket % plan.CoarsestIntervalMs);
    }

    [Fact]
    public void SmallestBucketWithinCap_RoundsUpToAWholeTierInterval()
    {
        // A span needing 12,960ms per point against a five second tier.
        Assert.Equal(15_000L, TierSelection.SmallestBucketWithinCap(3 * Day, 5 * Second));

        // Nothing to divide leaves the caller's own floor standing.
        Assert.Equal(0, TierSelection.SmallestBucketWithinCap(0, 5 * Second));
        Assert.Equal(0, TierSelection.SmallestBucketWithinCap(-1, 5 * Second));
    }
}
