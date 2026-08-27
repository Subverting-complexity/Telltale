using Telltale.Collector;

namespace Collector.Tests;

/// <summary>
/// Covers what the collector gives up when the capture outgrows
/// <c>maxDatabaseSizeMb</c>.
///
/// The policy this replaced deleted the oldest day of the ten minute tables and
/// then the oldest day of the one minute tables, then returned whatever the size
/// happened to be. Three things were wrong with it: those readings were gone
/// rather than summarised, the one minute day had not been promoted yet so its
/// removal left a hole in the middle of a tier's span, and it never converged
/// because it made at most two passes.
///
/// No database here on purpose. The interesting cases are the ones where the
/// policy has to stop, and those are easier to state as facts about retentions
/// than to provoke by filling a file.
/// </summary>
public class SizePressureTests
{
    private const long HourMs = 3_600_000L;
    private const long DayMs = 24 * HourMs;

    private static readonly IReadOnlyDictionary<string, long> NoPressure =
        new Dictionary<string, long>();

    private static TelltaleConfig Config() => new();

    [Fact]
    public void WithNoPressureApplied_ATierKeepsWhatWasConfigured()
    {
        Assert.Equal(30 * DayMs,
            SizePressure.EffectiveRetentionMs(Config(), StorageTiers.TenMinute, NoPressure));
    }

    [Fact]
    public void PressureShorterThanTheConfiguredRetention_Wins()
    {
        var applied = new Dictionary<string, long> { [StorageTiers.TenMinute.SampleTable] = 9 * DayMs };

        Assert.Equal(9 * DayMs,
            SizePressure.EffectiveRetentionMs(Config(), StorageTiers.TenMinute, applied));
    }

    [Fact]
    public void PressureLongerThanTheConfiguredRetention_IsIgnored()
    {
        // Lowering maxDatabaseSizeMb, then raising telltale.json's retention, must not
        // let a stale pressure row hand back more than the configuration asks for.
        var applied = new Dictionary<string, long> { [StorageTiers.TenMinute.SampleTable] = 900 * DayMs };

        Assert.Equal(30 * DayMs,
            SizePressure.EffectiveRetentionMs(Config(), StorageTiers.TenMinute, applied));
    }

    [Fact]
    public void TheCoarsestTier_HasNoRetentionBecauseItKeepsEverything()
    {
        // The floor of the ladder. Nothing is promoted out of it and nothing is
        // trimmed from it, which is what makes keeping a recording indefinitely
        // affordable.
        Assert.Null(SizePressure.EffectiveRetentionMs(Config(), StorageTiers.OneWeek, NoPressure));
    }

    [Fact]
    public void TheFirstStep_TightensTheOldestBoundary()
    {
        // Detail is given up from the far end of the recording first. If something
        // went wrong this morning, this morning's fine detail is the last thing to go.
        PressureStep? step = SizePressure.NextStep(Config(), NoPressure);

        Assert.NotNull(step);
        Assert.Equal(StorageTiers.OneDay, step.Source);
        Assert.Equal(StorageTiers.OneWeek, step.Target);
    }

    [Fact]
    public void TheRawTier_IsNeverTightened()
    {
        // Twenty four hours of five second readings is plausibly the largest single
        // thing in the file, and therefore the largest lever, but shortening it costs
        // the live view the detail it exists for. Whether it ever belongs on the
        // ladder is its own decision, taken against a real recording.
        Assert.DoesNotContain(StorageTiers.Raw, SizePressure.Pullable);
    }

    [Fact]
    public void EachStep_MovesTowardTheFloorWithoutPassingIt()
    {
        var config = Config();
        var applied = new Dictionary<string, long>();

        // The floor is where the hourly tier is configured to hand over, which does
        // not move as the ladder is tightened.
        long floor = config.RetentionMsFor(StorageTiers.OneHour)!.Value;
        long previous = SizePressure.EffectiveRetentionMs(config, StorageTiers.OneDay, applied)!.Value;

        for (int i = 0; i < 40; i++)
        {
            PressureStep? step = SizePressure.NextStep(config, applied);
            Assert.NotNull(step);

            if (step.Source != StorageTiers.OneDay) break;

            Assert.True(step.RetentionMs < previous,
                $"Step {i} did not tighten: {previous} then {step.RetentionMs}.");
            Assert.True(step.RetentionMs >= floor,
                $"Step {i} went below the tier feeding it: {step.RetentionMs} against a floor of {floor}.");

            previous = step.RetentionMs;
            applied[step.Source.SampleTable] = step.RetentionMs;
        }

        Assert.Equal(floor, previous);
    }

    [Fact]
    public void OnceEveryTierIsAtItsFloor_ThereIsNoNextStep()
    {
        // The end of the ladder. The caller's answer to null is to say so and let the
        // file exceed its limit, rather than start deleting.
        var config = Config();
        var applied = new Dictionary<string, long>();

        for (int i = 0; i < 500; i++)
        {
            PressureStep? step = SizePressure.NextStep(config, applied);
            if (step is null) break;

            applied[step.Source.SampleTable] = step.RetentionMs;
        }

        Assert.Null(SizePressure.NextStep(config, applied));

        // Everything has come to rest one rung down: each tier now holds roughly what
        // the tier feeding it was configured to hold. The daily tier gives up two
        // years for the hourly tier's six months, and so on down to the one minute
        // tier, which comes to rest on the raw retention that pressure may not move.
        Assert.Equal(180 * DayMs, SizePressure.EffectiveRetentionMs(config, StorageTiers.OneDay, applied));
        Assert.Equal(30 * DayMs, SizePressure.EffectiveRetentionMs(config, StorageTiers.OneHour, applied));
        Assert.Equal(7 * DayMs, SizePressure.EffectiveRetentionMs(config, StorageTiers.TenMinute, applied));
        Assert.Equal(DayMs, SizePressure.EffectiveRetentionMs(config, StorageTiers.OneMinute, applied));
    }

    [Fact]
    public void TighteningTerminates_RatherThanApproachingTheFloorForever()
    {
        // Halving the remaining gap never reaches the floor on its own. Without the
        // final step straight to it, the loop would keep finding a step that frees
        // almost nothing and the capture would never settle.
        var config = Config();
        var applied = new Dictionary<string, long>();

        int steps = 0;
        while (SizePressure.NextStep(config, applied) is { } step)
        {
            applied[step.Source.SampleTable] = step.RetentionMs;
            steps++;

            Assert.True(steps < 500, "Pressure did not converge on the floor.");
        }

        Assert.True(steps > 0, "There was room to tighten, so some step should have been offered.");
    }
}
