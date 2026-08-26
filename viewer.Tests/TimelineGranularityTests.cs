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
        // One raw tier, forty hours wide. Nothing but the cap can move a request
        // here: the tier floor is 5,000 and the answer has to be 10,000, so
        // deleting the cap would leave this at 5,000 and fail.
        long span = 40 * Hour;
        var coverage = new Dictionary<string, TierCoverage>
        {
            ["machine"] = new TierCoverage(Now - span, Now),
        };

        var plan = TierSelection.Plan(Now - span, Now, isMachine: true, coverage, requestedBucketMs: 5 * Second);

        Assert.Equal(5 * Second, plan.TierFloorMs);

        // 144,000,000ms over 20,000 points is 7,200ms per point, which rounds up
        // to the next whole tier interval.
        Assert.Equal(7_200L, span / TierSelection.MaxRawOnlyPoints);
        Assert.Equal(10_000L, plan.EffectiveBucket);
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

        // And the floor is always something a response can actually carry.
        var week = PlanFor(Now - 7 * Day, Now, requested: null);
        Assert.InRange(7 * Day / week.SmallestServableBucket, 1, TierSelection.MaxRawOnlyPoints);
    }

    [Fact]
    public void TierFloorAndServableFloor_SeparateTheTwoReasonsARequestIsRefused()
    {
        // Only the tiers bind: the 10 minute rollup over one day is far inside the
        // point cap, so the two floors agree.
        var rollup = PlanFor(Now - 21 * Day, Now - 20 * Day, requested: null);
        Assert.Equal(600_000L, rollup.TierFloorMs);
        Assert.Equal(600_000L, rollup.SmallestServableBucket);

        // Only the cap binds: one raw tier over forty hours still stores five
        // second detail, but not that many points of it at once.
        long span = 40 * Hour;
        var wide = TierSelection.Plan(Now - span, Now, isMachine: true,
            new Dictionary<string, TierCoverage> { ["machine"] = new TierCoverage(Now - span, Now) });
        Assert.Equal(5 * Second, wide.TierFloorMs);
        Assert.Equal(10_000L, wide.SmallestServableBucket);
    }

    [Fact]
    public void AFallbackSliceKeepsTheCallersOwnBounds_AndStillDoesNotOverflow()
    {
        // No tier holds anything, so the plan falls back to a slice carrying the
        // caller's unvalidated from and to. This is the only path where a span
        // spanning the whole of long survives into the arithmetic, and the only
        // one that reaches the Int128 widening on ReadSpanMs.
        var plan = TierSelection.Plan(long.MinValue, long.MaxValue, isMachine: true,
            new Dictionary<string, TierCoverage>(), requestedBucketMs: 5 * Second);

        Assert.Equal(922_337_203_690_000L, plan.EffectiveBucket);
    }

    [Fact]
    public void AWindowSpanningTheWholeOfLong_StillProducesAFiniteBucket()
    {
        // The widest window expressible. The slices are clamped to what the tiers
        // hold, so the arithmetic here never sees the full span; the case that
        // does is AFallbackSliceKeepsTheCallersOwnBounds above.
        var plan = PlanFor(long.MinValue, long.MaxValue, requested: 5 * Second);

        Assert.True(plan.EffectiveBucket > 0);
        Assert.Equal(0, plan.EffectiveBucket % plan.CoarsestIntervalMs);
    }

    [Fact]
    public void SmallestBucketWithinCap_RefusesATierIntervalItCannotDivideBy()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TierSelection.SmallestBucketWithinCap(Day, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => TierSelection.SmallestBucketWithinCap(Day, -1));
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
