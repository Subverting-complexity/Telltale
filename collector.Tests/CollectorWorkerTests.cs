using Microsoft.Extensions.Logging;
using Telltale.Collector;

namespace Collector.Tests;

public class CollectorWorkerTests
{
    [Theory]
    [InlineData(1, LogLevel.Warning)]
    [InlineData(2, LogLevel.Warning)]
    [InlineData(3, LogLevel.Error)]
    [InlineData(4, LogLevel.Error)]
    [InlineData(40, LogLevel.Error)]
    public void OverrunLevel_RisesOnceTheCollectorKeepsMissingItsInterval(
        int consecutiveOverruns, LogLevel expected)
    {
        // A single long tick is usually a busy moment on the machine. A run of them
        // means sampling cannot keep up, which used to show only as a viewer with
        // nothing in it and no error anywhere.
        Assert.Equal(expected, CollectorWorker.LevelForConsecutiveOverruns(consecutiveOverruns));
    }

    [Fact]
    public void OverrunLevel_IsNotAnErrorBeforeTheThreshold()
    {
        Assert.Equal(LogLevel.Warning,
            CollectorWorker.LevelForConsecutiveOverruns(
                CollectorWorker.ConsecutiveOverrunsBeforeError - 1));
        Assert.Equal(LogLevel.Error,
            CollectorWorker.LevelForConsecutiveOverruns(
                CollectorWorker.ConsecutiveOverrunsBeforeError));
    }
}
