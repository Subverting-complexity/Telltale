using Telltale.Collector;

namespace Collector.Tests;

/// <summary>
/// The checks that run before recording starts, now that they return the message to
/// report instead of writing it to a console and exiting.
/// </summary>
/// <remarks>
/// This is what lets the same checks serve both builds. The collector executable
/// prints what comes back; the single-process application, which has no console,
/// shows it in a dialog. If these ever went back to writing to standard error, the
/// windowed build would fail with nothing said anywhere.
/// </remarks>
public class CollectorStartupTests
{
    [Fact]
    public void AValidConfiguration_HasNothingToReport()
    {
        var config = new TelltaleConfig
        {
            DatabasePath = Path.Combine(Path.GetTempPath(), "telltale-startup-test", "telltale.db"),
        };

        Assert.Null(CollectorStartup.DescribeConfigurationProblem(config));
    }

    [Fact]
    public void AnInvalidSetting_IsReportedWithTheSettingNamed()
    {
        var config = new TelltaleConfig
        {
            DatabasePath = Path.Combine(Path.GetTempPath(), "telltale-startup-test", "telltale.db"),
            IntervalSeconds = 1,
        };

        var problem = CollectorStartup.DescribeConfigurationProblem(config);

        Assert.NotNull(problem);
        Assert.Contains("intervalSeconds", problem);
    }

    [Fact]
    public void EveryInvalidSetting_IsReported_NotJustTheFirst()
    {
        var config = new TelltaleConfig
        {
            DatabasePath = Path.Combine(Path.GetTempPath(), "telltale-startup-test", "telltale.db"),
            IntervalSeconds = 1,
            MaxDatabaseSizeMb = 1,
        };

        var problem = CollectorStartup.DescribeConfigurationProblem(config);

        Assert.NotNull(problem);
        Assert.Contains("intervalSeconds", problem);
        Assert.Contains("maxDatabaseSizeMb", problem);
    }

    [Fact]
    public void ADatabaseInASyncFolder_IsReportedWithTheReasonAndTheFix()
    {
        var oneDrive = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "OneDrive", "Telltale", "telltale.db");

        var problem = CollectorStartup.DescribeConfigurationProblem(
            new TelltaleConfig { DatabasePath = oneDrive });

        Assert.NotNull(problem);
        Assert.Contains("cloud sync folder", problem);
        Assert.Contains("databasePath", problem);
    }

    [Fact]
    public void AnEmptyDatabasePath_IsReportedRatherThanThrown()
    {
        // IsInSyncFolder calls Path.GetFullPath, which throws on an empty path.
        // Validation has to catch that first, or the message never reaches the user.
        var problem = CollectorStartup.DescribeConfigurationProblem(
            new TelltaleConfig { DatabasePath = "" });

        Assert.NotNull(problem);
        Assert.Contains("databasePath", problem);
    }
}
