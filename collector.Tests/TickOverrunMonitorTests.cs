using Microsoft.Extensions.Logging;
using Telltale.Collector;

namespace Collector.Tests;

/// <summary>
/// The collector falling behind used to be invisible: the process stayed up, nothing
/// was raised, and the only symptom was a viewer with nothing in it. These tests
/// cover the decision that makes it visible, and the throttling that stops it burying
/// everything else once the machine stays behind.
/// </summary>
public class TickOverrunMonitorTests
{
    private const double Interval = 5.0;

    private static TimeSpan Seconds(double s) => TimeSpan.FromSeconds(s);

    [Fact]
    public void ATickInsideTheIntervalSaysNothing()
    {
        var monitor = new TickOverrunMonitor();

        var report = monitor.Record(Seconds(1.2), Interval);

        Assert.Equal(TickOutcome.KeepingUp, report.Outcome);
        Assert.Equal(0, monitor.ConsecutiveOverruns);
    }

    [Fact]
    public void ATickThatTakesExactlyTheIntervalIsKeepingUp()
    {
        var monitor = new TickOverrunMonitor();

        // The boundary matters: a tick that fills its interval exactly has not missed
        // the next one, so calling it an overrun would report a problem that is not
        // there on any machine running close to the limit.
        var report = monitor.Record(Seconds(Interval), Interval);

        Assert.Equal(TickOutcome.KeepingUp, report.Outcome);
    }

    [Fact]
    public void TheFirstOverrunIsReportedAsAWarning()
    {
        var monitor = new TickOverrunMonitor();

        var report = monitor.Record(Seconds(7), Interval);

        Assert.Equal(TickOutcome.Overrun, report.Outcome);
        Assert.Equal(1, report.ConsecutiveOverruns);
        Assert.Equal(LogLevel.Warning,
            TickOverrunMonitor.LevelForConsecutiveOverruns(report.ConsecutiveOverruns));
    }

    [Fact]
    public void OverrunsThatKeepHappeningEscalateToAnError()
    {
        var monitor = new TickOverrunMonitor();

        monitor.Record(Seconds(7), Interval);
        monitor.Record(Seconds(7), Interval);
        var third = monitor.Record(Seconds(7), Interval);

        Assert.Equal(TickOutcome.Overrun, third.Outcome);
        Assert.Equal(3, third.ConsecutiveOverruns);
        Assert.Equal(LogLevel.Error,
            TickOverrunMonitor.LevelForConsecutiveOverruns(third.ConsecutiveOverruns));
    }

    [Fact]
    public void ASustainedRunOfOverrunsGoesQuietAndThenRepeats()
    {
        var monitor = new TickOverrunMonitor();
        var reported = new List<int>();

        for (int i = 0; i < TickOverrunMonitor.RepeatEvery * 2; i++)
        {
            var report = monitor.Record(Seconds(7), Interval);
            if (report.Outcome == TickOutcome.Overrun)
                reported.Add(report.ConsecutiveOverruns);
        }

        // The first few each get a line so a problem starting is visible immediately.
        // After that it repeats periodically: at a five second interval, reporting
        // every tick would be roughly seventeen thousand lines a day.
        Assert.Equal(new[] { 1, 2, 3, 12, 24 }, reported);
    }

    [Fact]
    public void CatchingUpIsReportedOnceAndResetsTheRun()
    {
        var monitor = new TickOverrunMonitor();

        monitor.Record(Seconds(7), Interval);
        monitor.Record(Seconds(7), Interval);
        var recovery = monitor.Record(Seconds(1), Interval);

        Assert.Equal(TickOutcome.Recovered, recovery.Outcome);
        Assert.Equal(2, recovery.ConsecutiveOverruns);
        Assert.Equal(0, monitor.ConsecutiveOverruns);

        // And it only says so once.
        Assert.Equal(TickOutcome.KeepingUp, monitor.Record(Seconds(1), Interval).Outcome);
    }

    [Fact]
    public void AnOverrunAfterRecoveringStartsCountingAgain()
    {
        var monitor = new TickOverrunMonitor();

        monitor.Record(Seconds(7), Interval);
        monitor.Record(Seconds(7), Interval);
        monitor.Record(Seconds(7), Interval);
        monitor.Record(Seconds(1), Interval);

        var report = monitor.Record(Seconds(7), Interval);

        Assert.Equal(1, report.ConsecutiveOverruns);
        Assert.Equal(LogLevel.Warning,
            TickOverrunMonitor.LevelForConsecutiveOverruns(report.ConsecutiveOverruns));
    }

    [Theory]
    [InlineData(1, LogLevel.Warning)]
    [InlineData(2, LogLevel.Warning)]
    [InlineData(3, LogLevel.Error)]
    [InlineData(40, LogLevel.Error)]
    public void LevelRisesOnceOverrunsPersist(int consecutiveOverruns, LogLevel expected)
    {
        Assert.Equal(expected, TickOverrunMonitor.LevelForConsecutiveOverruns(consecutiveOverruns));
    }
}
