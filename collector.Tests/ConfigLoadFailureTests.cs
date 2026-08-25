using System.Text.Json;
using Telltale.Collector;

namespace Collector.Tests;

/// <summary>
/// What happens when telltale.json cannot be read at all.
/// </summary>
/// <remarks>
/// A malformed file used to leave an unhandled JsonException. The console build
/// turned that into a stack trace, which was at least visible. The windowed build
/// has no console, so it turned into nothing: the application did not appear and
/// said nothing about why. Reporting it as a configuration problem is what makes
/// both builds behave.
/// </remarks>
public class ConfigLoadFailureTests : IDisposable
{
    readonly string _folder = Path.Combine(Path.GetTempPath(), $"telltale-config-{Guid.NewGuid():N}");

    public ConfigLoadFailureTests()
    {
        Directory.CreateDirectory(_folder);
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder))
            Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public void AWellFormedFile_LoadsWithNoError()
    {
        var path = Path.Combine(_folder, "telltale.json");
        File.WriteAllText(path, """{"intervalSeconds": 7}""");

        var config = TelltaleConfig.LoadFrom(path);

        Assert.Null(config.LoadError);
        Assert.Equal(7, config.IntervalSeconds);
    }

    [Theory]
    [InlineData("{ this is not json")]
    [InlineData("""{"intervalSeconds": 5,}""")]
    [InlineData("""{"intervalSeconds": "five"}""")]
    public void AMalformedFile_IsReportedRatherThanThrown(string contents)
    {
        var path = Path.Combine(_folder, "telltale.json");
        File.WriteAllText(path, contents);

        var config = TelltaleConfig.LoadFrom(path);

        Assert.NotNull(config.LoadError);
        Assert.Contains("telltale.json", config.LoadError);
    }

    [Fact]
    public void AMalformedFile_FailsValidation()
    {
        // This is what turns it into a message the user sees, in a dialog for the
        // application and on the console for the recorder.
        var path = Path.Combine(_folder, "telltale.json");
        File.WriteAllText(path, "{ not json at all");

        var errors = TelltaleConfig.LoadFrom(path).Validate();

        Assert.Single(errors);
        Assert.Contains("could not be read", errors[0]);
    }

    [Fact]
    public void AMalformedFile_ReportsOnlyThat()
    {
        // Every other value is a default that was used because the file could not
        // be read, so listing those as well would bury the one real problem.
        var config = new TelltaleConfig { IntervalSeconds = 1, MaxDatabaseSizeMb = 1 };
        Assert.Equal(2, config.Validate().Count);

        var path = Path.Combine(_folder, "telltale.json");
        File.WriteAllText(path, "{");

        Assert.Single(TelltaleConfig.LoadFrom(path).Validate());
    }

    [Fact]
    public void NoFileAtAll_IsNotAnError()
    {
        // Running without a telltale.json is normal: the defaults are the shipped
        // configuration, and nothing has failed.
        var config = TelltaleConfig.LoadFrom(Path.Combine(_folder, "telltale.json"));

        Assert.Null(config.LoadError);
        Assert.Empty(config.Validate());
    }

    [Fact]
    public void TheLoadError_IsNotSomethingTheFileCanSet()
    {
        // It reports how reading went, so a file claiming to have been read badly
        // would be reporting on itself.
        var config = JsonSerializer.Deserialize<TelltaleConfig>(
            """{"loadError": "made up"}""",
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.Null(config!.LoadError);
    }
}
