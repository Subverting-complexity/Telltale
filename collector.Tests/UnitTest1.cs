using Microsoft.Extensions.Logging;
using Telltale.Collector;
using Telltale.Collector.Interop;

namespace Collector.Tests;

public class ConfigTests
{
    [Fact]
    public void DefaultConfig_PassesValidation()
    {
        var config = new TelltaleConfig();
        var errors = config.Validate();
        Assert.Empty(errors);
    }

    [Fact]
    public void DefaultConfig_DoesNotRecordCommandLines()
    {
        // Storing a command line is something the user opts into. One can carry a
        // password, a token or a connection string, and the redaction applied when
        // recording is on masks only the patterns it knows about.
        Assert.False(new TelltaleConfig().RecordCommandLines);
    }

    [Fact]
    public void InvalidInterval_FailsValidation()
    {
        var config = new TelltaleConfig { IntervalSeconds = 1 };
        var errors = config.Validate();
        Assert.Contains(errors, e => e.Contains("intervalSeconds"));
    }

    [Fact]
    public void ShorterOneMinuteRetentionThanRaw_FailsValidation()
    {
        // Tier two would promote a ten minute bucket that tier one has not finished
        // filling, and the minutes still to come would be discarded.
        var config = new TelltaleConfig { RawRetentionHours = 72, Rollup1mRetentionDays = 2 };
        var errors = config.Validate();
        Assert.Contains(errors, e => e.Contains("rollup1mRetentionDays"));
    }

    [Fact]
    public void ShorterTenMinuteRetentionThanOneMinute_FailsValidation()
    {
        var config = new TelltaleConfig { Rollup1mRetentionDays = 30, Rollup10mRetentionDays = 14 };
        var errors = config.Validate();
        Assert.Contains(errors, e => e.Contains("rollup10mRetentionDays"));
    }

    [Fact]
    public void DefaultDatabasePath_IsUnderLocalAppData()
    {
        var config = new TelltaleConfig();
        Assert.Contains("Telltale", config.ResolvedDatabasePath);
        Assert.EndsWith("telltale.db", config.ResolvedDatabasePath);
    }

    [Fact]
    public void SyncFolderDetection_DetectsOneDrive()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var path = Path.Combine(userProfile, "OneDrive", "data", "telltale.db");
        Assert.True(TelltaleConfig.IsInSyncFolder(path));
    }

    [Fact]
    public void SyncFolderDetection_AllowsLocalAppData()
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Telltale", "telltale.db");
        Assert.False(TelltaleConfig.IsInSyncFolder(path));
    }

    [Fact]
    public void RedactCommandLine_RedactsPasswords()
    {
        var cmd = "myapp --password=secret123 --flag";
        var redacted = TelltaleConfig.RedactCommandLine(cmd);
        Assert.DoesNotContain("secret123", redacted);
        Assert.Contains("***REDACTED***", redacted);
    }

    [Fact]
    public void RedactCommandLine_RedactsApiKeys()
    {
        var cmd = "myapp --api-key=abc123def456";
        var redacted = TelltaleConfig.RedactCommandLine(cmd);
        Assert.DoesNotContain("abc123def456", redacted);
    }

    [Fact]
    public void RedactCommandLine_PreservesNonSecretArgs()
    {
        var cmd = "myapp --verbose --output=file.txt";
        var redacted = TelltaleConfig.RedactCommandLine(cmd);
        Assert.Equal(cmd, redacted);
    }

    [Fact]
    public void VacuumOnStartup_DefaultsToOff()
    {
        // Converting an existing database rewrites the whole file, so it has to be
        // something the operator asks for rather than something they inherit.
        Assert.False(new TelltaleConfig().VacuumOnStartup);
    }

    [Fact]
    public void VacuumOnStartup_BindsFromTheConfigFile()
    {
        // The warning the collector logs tells the operator to set this in
        // telltale.json, so the name in that file has to reach the property.
        var config = System.Text.Json.JsonSerializer.Deserialize<TelltaleConfig>(
            """{"vacuumOnStartup": true}""",
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.True(config!.VacuumOnStartup);
    }

    [Fact]
    public void RedactCommandLine_HandlesNull()
    {
        Assert.Null(TelltaleConfig.RedactCommandLine(null));
    }
}

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

public class NtDefsTests
{
    [Fact]
    public void StructLayout_ValidatesCorrectly()
    {
        Assert.True(NtDefs.ValidateLayout());
    }
}

public class NativeSamplerTests
{
    [Fact]
    public void Sample_ReturnsProcesses()
    {
        var sampler = new NativeSampler();
        var results = sampler.Sample();
        Assert.NotEmpty(results);
        Assert.Contains(results, p => p.Pid == Environment.ProcessId);
    }

    [Fact]
    public void Sample_IncludesProcessNames()
    {
        var sampler = new NativeSampler();
        var results = sampler.Sample();
        var self = results.FirstOrDefault(p => p.Pid == Environment.ProcessId);
        Assert.NotNull(self);
        Assert.False(string.IsNullOrEmpty(self.Name));
    }
}
