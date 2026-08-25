using System.Diagnostics;

namespace Telltale.Collector;

/// <summary>
/// Turns two readings of processor time into a rate.
///
/// Every CPU figure the collector records is worked out this way, for a sampled
/// process and for the collector itself alike, so the two are directly
/// comparable and the formula lives in one place rather than beside each caller.
/// </summary>
public static class CpuRate
{
    /// <summary>
    /// One second in the units Windows reports processor time in, which are
    /// hundreds of nanoseconds.
    /// </summary>
    private const double ProcessorTimeTicksPerSecond = 10_000_000.0;

    /// <summary>
    /// Processor time used over wall clock time elapsed, as a percentage of one
    /// core. A process using two cores fully for the whole period reads 200.
    ///
    /// The two arguments are counted in different units and that is deliberate,
    /// because the two clocks are. Processor time comes from Windows in hundreds
    /// of nanoseconds; elapsed time comes from <see cref="Stopwatch"/>, whose tick
    /// is whatever <see cref="Stopwatch.Frequency"/> says it is on this machine.
    /// </summary>
    /// <param name="processorTimeTicksDelta">
    /// Processor time used between the two readings, in hundreds of nanoseconds.
    /// </param>
    /// <param name="elapsedStopwatchTicksDelta">
    /// Wall clock time between the two readings, in <see cref="Stopwatch"/> ticks.
    /// </param>
    /// <returns>
    /// The percentage, or null when no rate can be worked out: no time passed
    /// between the readings, or the processor time went backwards, which means
    /// the counter was reset or the two readings are not of the same thing.
    /// Null rather than zero, because zero is a measurement and this is the
    /// absence of one.
    /// </returns>
    public static double? PercentOfOneCore(long processorTimeTicksDelta, long elapsedStopwatchTicksDelta)
    {
        if (elapsedStopwatchTicksDelta <= 0 || processorTimeTicksDelta < 0) return null;

        double elapsedSeconds = (double)elapsedStopwatchTicksDelta / Stopwatch.Frequency;

        return processorTimeTicksDelta / ProcessorTimeTicksPerSecond / elapsedSeconds * 100.0;
    }
}
