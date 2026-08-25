using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Telltale.Collector;

public sealed partial class TelltaleConfig
{
    public int IntervalSeconds { get; set; } = 5;
    public string? DatabasePath { get; set; }
    /// <summary>
    /// Whether a process's command line is recorded alongside its name. Off by
    /// default: a command line can carry a password, a token or a connection string,
    /// and the redaction applied when this is on masks a fixed set of patterns rather
    /// than everything. Storing it is therefore something the user opts into.
    /// </summary>
    public bool RecordCommandLines { get; set; } = false;
    public int MaxDatabaseSizeMb { get; set; } = 500;
    public int RawRetentionHours { get; set; } = 24;
    public int Rollup1mRetentionDays { get; set; } = 7;
    public int Rollup10mRetentionDays { get; set; } = 365;
    public int HealthRetentionDays { get; set; } = 7;
    public int RollupIntervalMinutes { get; set; } = 5;

    /// <summary>
    /// Whether to convert a database that predates the auto_vacuum ordering fix on
    /// the next start. The conversion is a full VACUUM: it rewrites the whole file
    /// and needs roughly twice its size in free disk while it runs, so it is off by
    /// default and the collector only logs that it is available.
    /// </summary>
    public bool VacuumOnStartup { get; set; }

    /// <summary>
    /// The loopback port the Telltale window is served on. Zero lets the operating
    /// system pick one.
    /// </summary>
    /// <remarks>
    /// The recorder never reads this. It lives here because telltale.json is the
    /// one file a user edits, and modelling it twice would mean two validation
    /// paths reporting the same mistake differently. The viewer executable does not
    /// read it either, so nothing about the boundary between the two projects
    /// changes: only the host, which composes both, acts on it.
    ///
    /// The default sits below 49152, where the Windows dynamic port range starts,
    /// so a transient outbound socket cannot claim it first, and it is not the
    /// default for any common development server. It is not reserved, so the host
    /// still has to cope with the port being unavailable.
    /// </remarks>
    public int ViewerPort { get; set; } = DefaultViewerPort;

    /// <summary>The value <see cref="ViewerPort"/> takes when telltale.json is silent.</summary>
    public const int DefaultViewerPort = 41821;
    public ThresholdConfig Thresholds { get; set; } = new();

    /// <summary>
    /// Why telltale.json could not be read, or null when there was nothing wrong.
    /// </summary>
    /// <remarks>
    /// Recorded rather than thrown. A malformed file used to leave an unhandled
    /// JsonException, which the console build turned into a stack trace and the
    /// windowed build turned into nothing at all: the application simply did not
    /// appear. Validation reports it like any other configuration mistake, and the
    /// values fall back to the defaults so nothing runs on a half-read file.
    /// </remarks>
    [JsonIgnore]
    public string? LoadError { get; private set; }

    public string ResolvedDatabasePath =>
        DatabasePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Telltale", "telltale.db");

    public static TelltaleConfig Load()
    {
        var configPath = Path.Combine(AppContext.BaseDirectory, "telltale.json");
        if (!File.Exists(configPath))
            configPath = Path.Combine(Environment.CurrentDirectory, "telltale.json");

        return LoadFrom(configPath);
    }

    /// <summary>Reads one named configuration file.</summary>
    /// <remarks>
    /// Separate from <see cref="Load"/> so that where the file is found and what
    /// happens when it cannot be read are two things rather than one. Only the
    /// second is worth testing, and testing it through <see cref="Load"/> would
    /// mean moving the process's working directory around.
    /// </remarks>
    public static TelltaleConfig LoadFrom(string configPath)
    {
        if (!File.Exists(configPath))
            return new TelltaleConfig();

        try
        {
            var json = File.ReadAllText(configPath);
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            return JsonSerializer.Deserialize<TelltaleConfig>(json, options) ?? new TelltaleConfig();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new TelltaleConfig
            {
                LoadError = $"{configPath} could not be read: {ex.Message}",
            };
        }
    }

    public List<string> Validate()
    {
        var errors = new List<string>();

        // Reported first and on its own: every value below is a default that was
        // used because the file could not be read, so listing them as well would
        // bury the one thing that is actually wrong.
        if (LoadError is not null)
        {
            errors.Add(LoadError);
            return errors;
        }

        // Checked here because nowhere else can report it. IsInSyncFolder calls
        // Path.GetFullPath, which throws on an empty or malformed path, and the
        // collector runs that check before the host exists. An unusable path
        // therefore reached the user as an unhandled exception and a process that
        // died again on every start, rather than as the configuration error it is.
        // A null value is not a problem: ResolvedDatabasePath supplies the default.
        if (DatabasePath is not null)
        {
            if (string.IsNullOrWhiteSpace(DatabasePath))
            {
                errors.Add("databasePath must not be empty.");
            }
            else
            {
                try
                {
                    Path.GetFullPath(DatabasePath);
                }
                catch (Exception ex)
                    when (ex is ArgumentException or NotSupportedException or PathTooLongException)
                {
                    errors.Add($"databasePath is not a usable path: {ex.Message}");
                }
            }
        }

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

        // Zero is allowed and means "let the operating system choose". Anything
        // below 1024 needs privileges Telltale does not have and should not want.
        if (ViewerPort != 0 && (ViewerPort < 1024 || ViewerPort > 65535))
            errors.Add("viewerPort must be 0, or between 1024 and 65535.");

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
