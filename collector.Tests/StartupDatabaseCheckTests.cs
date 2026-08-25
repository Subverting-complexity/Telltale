using Telltale.Collector;

namespace Collector.Tests;

/// <summary>
/// Covers the decision the collector makes about a database before it records
/// anything, and the wording it uses to explain a refusal.
///
/// The few lines in <c>Program.cs</c> that print that refusal and exit are not
/// covered here. Launching the executable and watching it exit is slow and
/// unreliable on a build server, which is the reason the judgement was put in a
/// plain function in the first place.
/// </summary>
public class StartupDatabaseCheckTests
{
    /// <summary>
    /// Built rather than written out, because the tests run on Windows and on
    /// Linux and a literal Windows path is not valid on both.
    /// </summary>
    private static readonly string DbPath =
        Path.Combine(Path.GetTempPath(), "Telltale", "telltale.db");

    [Theory]
    [InlineData(2, 2)]
    [InlineData(1, 2)]
    [InlineData(0, 2)]
    public void VersionThisBuildUnderstands_IsNotRefused(int databaseVersion, int buildVersion)
    {
        // Older versions have already been migrated forward by the time the
        // check runs, so only a newer one is a reason to stop.
        Assert.Null(StartupDatabaseCheck.RefusalForNewerDatabase(databaseVersion, buildVersion, DbPath));
    }

    [Fact]
    public void VersionFromANewerBuild_IsRefused()
    {
        Assert.NotNull(StartupDatabaseCheck.RefusalForNewerDatabase(3, 2, DbPath));
    }

    [Fact]
    public void Refusal_NamesBothVersionsAndTheFile()
    {
        string refusal = StartupDatabaseCheck.RefusalForNewerDatabase(3, 2, DbPath)!;

        Assert.Contains("version 3", refusal);
        Assert.Contains("version 2", refusal);
        Assert.Contains(DbPath, refusal);
    }

    [Fact]
    public void Refusal_NamesBothWaysOut()
    {
        // Reporting the numbers alone leaves the user with a collector that
        // will not run and nothing to do about it. However the wording is
        // rephrased later, it has to keep pointing at the build that owns this
        // database and at the setting that starts a clean recording elsewhere.
        string refusal = StartupDatabaseCheck.RefusalForNewerDatabase(3, 2, DbPath)!;

        Assert.Contains("TelltaleCapture.exe", refusal);
        Assert.Contains("databasePath", refusal);
        Assert.Contains("telltale.json", refusal);
    }

    [Fact]
    public void OpenFailure_NamesTheFileAndTheUnderlyingError()
    {
        string described = StartupDatabaseCheck.DescribeOpenFailure(
            DbPath, new IOException("The device is not ready."));

        Assert.Contains(DbPath, described);
        Assert.Contains("The device is not ready.", described);
    }

    [Fact]
    public void OpenFailure_SaysWhatToCheck()
    {
        // This is read after a collector that stopped recording without saying
        // anything, quite possibly days later. The SQLite error on its own is
        // rarely enough to act on, so the message names the things that cause
        // it and the setting that moves the recording elsewhere.
        string described = StartupDatabaseCheck.DescribeOpenFailure(
            DbPath, new IOException("The device is not ready."));

        Assert.Contains("databasePath", described);
        Assert.Contains("telltale.json", described);
    }
}
