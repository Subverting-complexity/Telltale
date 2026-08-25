using System.Diagnostics;
using Telltale.Collector;

namespace Collector.Tests;

/// <summary>
/// Covers the one place a CPU percentage is worked out. Both callers depend on
/// it, and before issue #93 the collector's own figure was not worked out at all
/// but written as a hardcoded zero, which read as "the recorder is using no CPU"
/// for the whole life of every recording.
/// </summary>
public class CpuRateTests
{
    /// <summary>One second of wall clock, in the units Stopwatch counts in.</summary>
    private static readonly long OneSecondOfStopwatchTicks = Stopwatch.Frequency;

    /// <summary>One second of processor time, in hundreds of nanoseconds.</summary>
    private const long OneSecondOfProcessorTime = 10_000_000L;

    [Fact]
    public void OneCoreFullyUsedForOneSecond_ReadsAsOneHundredPercent()
    {
        double? pct = CpuRate.PercentOfOneCore(OneSecondOfProcessorTime, OneSecondOfStopwatchTicks);

        Assert.NotNull(pct);
        Assert.Equal(100.0, pct.Value, 6);
    }

    [Fact]
    public void TwoCoresFullyUsedForOneSecond_ReadsAsTwoHundredPercent()
    {
        // The figure is a share of one core, so a process spread across two cores
        // reads over 100. This is the denominator issue #94 is about, and it is
        // asserted here so a change to it cannot pass unnoticed.
        double? pct = CpuRate.PercentOfOneCore(2 * OneSecondOfProcessorTime, OneSecondOfStopwatchTicks);

        Assert.NotNull(pct);
        Assert.Equal(200.0, pct.Value, 6);
    }

    [Fact]
    public void HalfACoreOverTwoSeconds_ReadsAsFiftyPercent()
    {
        double? pct = CpuRate.PercentOfOneCore(OneSecondOfProcessorTime, 2 * OneSecondOfStopwatchTicks);

        Assert.NotNull(pct);
        Assert.Equal(50.0, pct.Value, 6);
    }

    [Fact]
    public void AProcessThatUsedNoTime_ReadsAsZeroRatherThanNothing()
    {
        // Zero is a real measurement here: time passed and the process used none
        // of it. Only an unmeasurable interval gives null.
        double? pct = CpuRate.PercentOfOneCore(0, OneSecondOfStopwatchTicks);

        Assert.NotNull(pct);
        Assert.Equal(0.0, pct.Value);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NoTimeBetweenTheTwoReadings_IsNotAMeasurement(long elapsedTicks)
    {
        Assert.Null(CpuRate.PercentOfOneCore(OneSecondOfProcessorTime, elapsedTicks));
    }

    [Fact]
    public void ProcessorTimeThatWentBackwards_IsNotAMeasurement()
    {
        // Two readings that cannot be of the same process. Reporting a negative
        // percentage would be worse than reporting nothing.
        Assert.Null(CpuRate.PercentOfOneCore(-OneSecondOfProcessorTime, OneSecondOfStopwatchTicks));
    }
}
