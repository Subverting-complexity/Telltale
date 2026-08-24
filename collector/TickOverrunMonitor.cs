namespace Telltale.Collector;

/// <summary>What a finished sampling tick means for the collector's ability to keep up.</summary>
public enum TickOutcome
{
    /// <summary>Inside the interval, and so was the tick before it. Nothing to say.</summary>
    KeepingUp,

    /// <summary>Inside the interval after one or more that were not. Worth one line.</summary>
    Recovered,

    /// <summary>Over the interval, and worth reporting.</summary>
    Overrun,

    /// <summary>
    /// Over the interval, but the run of overruns has already been reported recently.
    /// </summary>
    OverrunAgain,
}

/// <summary>
/// What a tick meant, and the run of overruns that reading belongs to.
/// </summary>
public readonly record struct TickReport(TickOutcome Outcome, int ConsecutiveOverruns);

/// <summary>
/// Watches how long each sampling tick takes and decides when that is worth saying
/// out loud.
/// </summary>
/// <remarks>
/// <para>
/// A tick that takes longer than the sampling interval means the collector cannot
/// keep up, and the recorded history will have gaps. That used to be invisible: the
/// process stayed up, nothing was raised, and the only symptom was a viewer with
/// nothing in it.
/// </para>
/// <para>
/// This is a separate type rather than a few fields on <see cref="CollectorWorker"/>
/// so the decision can be tested without running a sampling loop. It is not thread
/// safe and expects to be driven from that loop alone.
/// </para>
/// </remarks>
public sealed class TickOverrunMonitor
{
    /// <summary>
    /// Once this many ticks in a row have overrun, the report is an error rather than
    /// a warning. One slow tick is usually a busy moment on the machine. A run of them
    /// is the collector falling behind.
    /// </summary>
    public const int ConsecutiveOverrunsBeforeError = 3;

    /// <summary>
    /// How often a sustained run of overruns is repeated once every tick is
    /// overrunning. At the default five second interval, reporting each one would be
    /// roughly seventeen thousand lines a day and would bury everything else.
    /// </summary>
    public const int RepeatEvery = 12;

    private int _consecutiveOverruns;

    /// <summary>How many ticks in a row have now overrun. Zero when keeping up.</summary>
    public int ConsecutiveOverruns => _consecutiveOverruns;

    /// <summary>
    /// Records how long a tick took and says what, if anything, should be reported.
    /// A tick that takes exactly the interval is keeping up, not overrunning.
    /// </summary>
    /// <returns>
    /// The outcome, together with the run of overruns it refers to: how many have now
    /// overrun in a row, or for <see cref="TickOutcome.Recovered"/> how many had
    /// overrun before this tick caught up.
    /// </returns>
    public TickReport Record(TimeSpan tickDuration, double intervalSeconds)
    {
        if (tickDuration.TotalSeconds <= intervalSeconds)
        {
            if (_consecutiveOverruns == 0)
                return new TickReport(TickOutcome.KeepingUp, 0);

            int recoveredFrom = _consecutiveOverruns;
            _consecutiveOverruns = 0;
            return new TickReport(TickOutcome.Recovered, recoveredFrom);
        }

        _consecutiveOverruns++;
        var outcome = ShouldReport(_consecutiveOverruns)
            ? TickOutcome.Overrun
            : TickOutcome.OverrunAgain;
        return new TickReport(outcome, _consecutiveOverruns);
    }

    /// <summary>
    /// Whether an overrunning tick is worth its own line, given how many have overrun
    /// in a row. The first few each get one, so a problem starting is visible
    /// immediately; after that the message repeats periodically instead.
    /// </summary>
    public static bool ShouldReport(int consecutiveOverruns) =>
        consecutiveOverruns <= ConsecutiveOverrunsBeforeError
        || consecutiveOverruns % RepeatEvery == 0;

    /// <summary>
    /// The level an overrunning tick should be reported at, given how many ticks have
    /// now overrun in a row.
    /// </summary>
    public static LogLevel LevelForConsecutiveOverruns(int consecutiveOverruns) =>
        consecutiveOverruns >= ConsecutiveOverrunsBeforeError
            ? LogLevel.Error
            : LogLevel.Warning;
}
