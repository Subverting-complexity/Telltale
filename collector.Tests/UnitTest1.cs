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
    public void ShippedConfigFile_DoesNotRecordCommandLines()
    {
        // The property initialiser above is only reached when no telltale.json is
        // found, and on a published build one always is: publish.bat copies the
        // repository's own file next to the executable, and TelltaleConfig.Load reads
        // it in preference to the code default. So the shipped file is what actually
        // decides, and it has to be pinned separately or the two silently drift apart.
        Assert.True(
            File.Exists(Path.Combine(AppContext.BaseDirectory, "telltale.json")),
            "telltale.json was not copied next to the tests, so this asserts nothing.");

        Assert.False(TelltaleConfig.Load().RecordCommandLines);
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
    public void EmptyDatabasePath_FailsValidation()
    {
        // Reported as a configuration error rather than left to Path.GetFullPath,
        // which throws before the collector has any way to say what went wrong.
        var config = new TelltaleConfig { DatabasePath = "" };
        var errors = config.Validate();
        Assert.Contains(errors, e => e.Contains("databasePath"));
    }

    [Fact]
    public void WhitespaceDatabasePath_FailsValidation()
    {
        var config = new TelltaleConfig { DatabasePath = "   " };
        var errors = config.Validate();
        Assert.Contains(errors, e => e.Contains("databasePath"));
    }

    [Fact]
    public void UnsetDatabasePath_PassesValidation()
    {
        // Null is not a problem: ResolvedDatabasePath supplies the default. The
        // control that stops the check above rejecting the ordinary case.
        var config = new TelltaleConfig { DatabasePath = null };
        var errors = config.Validate();
        Assert.DoesNotContain(errors, e => e.Contains("databasePath"));
    }

    [Fact]
    public void OrdinaryDatabasePath_PassesValidation()
    {
        var config = new TelltaleConfig
        {
            DatabasePath = Path.Combine(Path.GetTempPath(), "Telltale", "telltale.db"),
        };
        var errors = config.Validate();
        Assert.DoesNotContain(errors, e => e.Contains("databasePath"));
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
    public void DefaultViewerPort_IsTheDocumentedOne()
    {
        // The default is quoted in the README, in publish.bat and in the Vite dev
        // proxy. Pinning it here means changing it breaks a test rather than
        // silently leaving three places saying something that is no longer true.
        Assert.Equal(41821, new TelltaleConfig().ViewerPort);
        Assert.Equal(41821, TelltaleConfig.DefaultViewerPort);
    }

    [Fact]
    public void ShippedConfigFile_UsesTheDefaultViewerPort()
    {
        Assert.True(
            File.Exists(Path.Combine(AppContext.BaseDirectory, "telltale.json")),
            "telltale.json was not copied next to the tests, so this asserts nothing.");

        Assert.Equal(TelltaleConfig.DefaultViewerPort, TelltaleConfig.Load().ViewerPort);
    }

    [Fact]
    public void ViewerPort_BindsFromTheConfigFile()
    {
        var config = System.Text.Json.JsonSerializer.Deserialize<TelltaleConfig>(
            """{"viewerPort": 40000}""",
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.Equal(40000, config!.ViewerPort);
    }

    [Fact]
    public void ViewerPortOfZero_PassesValidation()
    {
        // Zero means "let Windows choose", which is the same thing the host falls
        // back to when a configured port turns out to be taken.
        Assert.Empty(new TelltaleConfig { ViewerPort = 0 }.Validate());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(80)]
    [InlineData(1023)]
    [InlineData(65536)]
    [InlineData(-1)]
    public void ViewerPortOutsideTheUsableRange_FailsValidation(int port)
    {
        var errors = new TelltaleConfig { ViewerPort = port }.Validate();

        Assert.Contains(errors, e => e.Contains("viewerPort"));
    }

    [Theory]
    [InlineData(1024)]
    [InlineData(41821)]
    [InlineData(65535)]
    public void ViewerPortInsideTheUsableRange_PassesValidation(int port)
    {
        Assert.Empty(new TelltaleConfig { ViewerPort = port }.Validate());
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
