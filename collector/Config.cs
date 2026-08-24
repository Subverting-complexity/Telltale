using System.Text.Json;
using System.Text.RegularExpressions;

namespace Telltale.Collector;

public sealed partial class TelltaleConfig
{
    public int IntervalSeconds { get; set; } = 5;
    public string? DatabasePath { get; set; }
    public bool RecordCommandLines { get; set; } = true;
    public int MaxDatabaseSizeMb { get; set; } = 500;
    public int RawRetentionHours { get; set; } = 24;
    public int Rollup1mRetentionDays { get; set; } = 7;
    public int Rollup10mRetentionDays { get; set; } = 365;
    public int HealthRetentionDays { get; set; } = 7;
    public int RollupIntervalMinutes { get; set; } = 5;

    /// <summary>
    /// Whether to convert a database that predates the auto_vacuum ordering fix on
    /// the next start. The conversion is a full VACUUM: it rewrites the whole file
    /// and needs about as much free disk again, so it is off by default and the
    /// collector only logs that it is available.
    /// </summary>
    public bool VacuumOnStartup { get; set; }
    public ThresholdConfig Thresholds { get; set; } = new();

    public string ResolvedDatabasePath =>
        DatabasePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Telltale", "telltale.db");

    public static TelltaleConfig Load()
    {
        var configPath = Path.Combine(AppContext.BaseDirectory, "telltale.json");
        if (!File.Exists(configPath))
            configPath = Path.Combine(Environment.CurrentDirectory, "telltale.json");

        if (!File.Exists(configPath))
            return new TelltaleConfig();

        var json = File.ReadAllText(configPath);
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        return JsonSerializer.Deserialize<TelltaleConfig>(json, options) ?? new TelltaleConfig();
    }

    public List<string> Validate()
    {
        var errors = new List<string>();

        if (IntervalSeconds < 2 || IntervalSeconds > 60)
            errors.Add("intervalSeconds must be between 2 and 60.");

        if (MaxDatabaseSizeMb < 50)
            errors.Add("maxDatabaseSizeMb must be at least 50.");

        if (RawRetentionHours < 1 || RawRetentionHours > 168)
            errors.Add("rawRetentionHours must be between 1 and 168.");

        if (Rollup1mRetentionDays < 1 || Rollup1mRetentionDays > 90)
            errors.Add("rollup1mRetentionDays must be between 1 and 90.");

        if (Rollup10mRetentionDays < 7 || Rollup10mRetentionDays > 730)
            errors.Add("rollup10mRetentionDays must be between 7 and 730.");

        if (RollupIntervalMinutes < 1 || RollupIntervalMinutes > 60)
            errors.Add("rollupIntervalMinutes must be between 1 and 60.");

        // Each tier has to retain data for at least as long as the tier feeding it.
        // A shorter tier would be asked to promote or delete data the tier below has
        // not finished producing, which loses whatever arrives afterwards.
        if (Rollup1mRetentionDays * 24 < RawRetentionHours)
            errors.Add("rollup1mRetentionDays must cover at least rawRetentionHours.");

        if (Rollup10mRetentionDays < Rollup1mRetentionDays)
            errors.Add("rollup10mRetentionDays must be at least rollup1mRetentionDays.");

        return errors;
    }

    public static bool IsInSyncFolder(string path)
    {
        var normalized = Path.GetFullPath(path).Replace('\\', '/').ToLowerInvariant();
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            .Replace('\\', '/').ToLowerInvariant();

        string[] syncFolders = ["OneDrive", "Google Drive", "Dropbox", "iCloudDrive"];
        foreach (var folder in syncFolders)
        {
            var syncPath = $"{userProfile}/{folder.ToLowerInvariant()}";
            if (normalized.StartsWith(syncPath, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    [GeneratedRegex(@"(?i)(password|secret|token|api[_-]?key|bearer|authorization)[=:\s]+\S+")]
    public static partial Regex SecretPattern();

    public static string? RedactCommandLine(string? commandLine)
    {
        if (string.IsNullOrEmpty(commandLine))
            return commandLine;

        return SecretPattern().Replace(commandLine, m =>
            m.Groups[1].Value + "=***REDACTED***");
    }
}

public sealed class ThresholdConfig
{
    public double CpuPct { get; set; }
    public double PrivateMemoryMb { get; set; }
}
