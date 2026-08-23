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
