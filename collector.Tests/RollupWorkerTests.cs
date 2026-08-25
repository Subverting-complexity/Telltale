using Microsoft.Extensions.Logging;
using Telltale.Collector;

namespace Collector.Tests;

public class RollupWorkerTests
{
    [Theory]
    [InlineData(1, LogLevel.Error)]
    [InlineData(2, LogLevel.Error)]
    [InlineData(3, LogLevel.Critical)]
    [InlineData(4, LogLevel.Critical)]
    [InlineData(40, LogLevel.Critical)]
    public void FailureLevel_EscalatesOnceFailuresPersist(int consecutiveFailures, LogLevel expected)
    {
        // A single failure is usually transient. A run of them means nothing is being
        // aggregated and the raw tables are no longer being trimmed, which is what the
        // raised severity is there to surface.
        Assert.Equal(expected, RollupWorker.LevelForConsecutiveFailures(consecutiveFailures));
    }

    [Fact]
    public void FailureLevel_IsNotCriticalBeforeTheThreshold()
    {
        Assert.Equal(LogLevel.Error,
            RollupWorker.LevelForConsecutiveFailures(RollupWorker.ConsecutiveFailuresBeforeCritical - 1));
        Assert.Equal(LogLevel.Critical,
            RollupWorker.LevelForConsecutiveFailures(RollupWorker.ConsecutiveFailuresBeforeCritical));
    }
}
