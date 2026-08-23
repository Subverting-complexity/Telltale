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
    public void InvalidInterval_FailsValidation()
    {
        var config = new TelltaleConfig { IntervalSeconds = 1 };
        var errors = config.Validate();
        Assert.Contains(errors, e => e.Contains("intervalSeconds"));
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
    public void RedactCommandLine_HandlesNull()
    {
        Assert.Null(TelltaleConfig.RedactCommandLine(null));
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

public class DatabaseTests : IDisposable
{
    private readonly string _dbPath;
    private readonly Database _db;

    public DatabaseTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"telltale_test_{Guid.NewGuid()}.db");
        var logger = new TestLogger();
        _db = new Database(_dbPath, logger);
    }

    [Fact]
    public void CreateDatabase_SetsUpSchema()
    {
        Assert.True(File.Exists(_dbPath));
    }

    [Fact]
    public void GetOrCreateProcessInstance_InsertsAndReturns()
    {
        long ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        long id = _db.GetOrCreateProcessInstance(1234, 100, "test.exe", null, null, ts);
        Assert.True(id > 0);

        long id2 = _db.GetOrCreateProcessInstance(1234, 100, "test.exe", null, null, ts + 1000);
        Assert.Equal(id, id2);
    }

    [Fact]
    public void WriteSampleBatch_WritesRows()
    {
        long ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        long id = _db.GetOrCreateProcessInstance(1, 100, "test.exe", null, null, ts);

        var rows = new List<SampleRow>
        {
            new(id, 5.0, 100, 200, 50, 10, 100),
        };
        _db.WriteSampleBatch(ts, rows);
    }

    [Fact]
    public void WriteMachineSample_WritesRow()
    {
        long ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var sample = new MachineSample(50.0, 8000, 12000, 0, 1.0, 2.0, 16000, 30.0, 1000, null);
        _db.WriteMachineSample(ts, sample);
    }

    public void Dispose()
    {
        _db.Dispose();
        try { File.Delete(_dbPath); } catch { }
        try { File.Delete(_dbPath + "-wal"); } catch { }
        try { File.Delete(_dbPath + "-shm"); } catch { }
    }

    private class TestLogger : Microsoft.Extensions.Logging.ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => false;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
            TState state, Exception? exception, Func<TState, Exception?, string> formatter) { }
    }
}
