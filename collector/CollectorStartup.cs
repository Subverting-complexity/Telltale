using Microsoft.Data.Sqlite;

namespace Telltale.Collector;

/// <summary>
/// The checks that run before the recorder is allowed to start, expressed as
/// messages rather than as writes to the console.
/// </summary>
/// <remarks>
/// The collector executable prints what these return and exits. The Telltale host
/// has no console to print to, so it shows the same text in a dialog. Neither
/// decides here what a failure looks like, which is the point of returning the
/// message instead of reporting it.
/// </remarks>
public static class CollectorStartup
{
    /// <summary>
    /// Checks the configuration is usable and that the database is not somewhere
    /// that will corrupt it.
    /// </summary>
    /// <returns>The problem to report, or null when there is nothing wrong.</returns>
    public static string? DescribeConfigurationProblem(TelltaleConfig config)
    {
        var errors = config.Validate();
        if (errors.Count > 0)
        {
            var lines = new List<string> { "Configuration errors:" };
            lines.AddRange(errors.Select(e => $"  - {e}"));
            return string.Join(Environment.NewLine, lines);
        }

        if (TelltaleConfig.IsInSyncFolder(config.ResolvedDatabasePath))
        {
            return string.Join(Environment.NewLine,
                $"Database path is inside a cloud sync folder: {config.ResolvedDatabasePath}",
                "This can cause database corruption. Set databasePath in telltale.json to a local folder.");
        }

        return null;
    }

    /// <summary>
    /// Opens the database and confirms this build understands its schema version.
    /// </summary>
    /// <remarks>
    /// Opening here rather than leaving it to whichever hosted service resolves it
    /// first means migrations run at a known point, and the version check gets its
    /// answer before anything has started recording.
    ///
    /// On failure the database files are released and <paramref name="host"/> is
    /// disposed, so the caller must report the message and exit rather than
    /// continue with it.
    /// </remarks>
    /// <returns>The problem to report, or null when the database is ready.</returns>
    public static string? OpenAndCheckDatabase(IHost host, TelltaleConfig config)
    {
        Database database;
        try
        {
            database = host.Services.GetRequiredService<Database>();
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            // A locked, corrupt, unreachable or unwritable file. Without this the
            // exception leaves host startup before logging is running, so the
            // process dies with nothing said about which file failed or why, and
            // does it again on every start. Anything outside these three is a bug
            // rather than a broken database, and still gets to surface as one.
            var message = StartupDatabaseCheck.DescribeOpenFailure(config.ResolvedDatabasePath, ex);
            ReleaseDatabaseFiles(host);
            return message;
        }

        string? refusal = StartupDatabaseCheck.RefusalForNewerDatabase(
            database.SchemaVersion, SchemaMigrations.LatestVersion, config.ResolvedDatabasePath);
        if (refusal is not null)
        {
            ReleaseDatabaseFiles(host);
            return refusal;
        }

        return null;
    }

    /// <summary>
    /// Closes the database before the process exits on a startup failure.
    /// </summary>
    /// <remarks>
    /// Disposing the host disposes the connection, and the collector opens that
    /// connection with pooling off, so the file and its -wal and -shm sidecars are
    /// released here rather than held until the process exits. On the refusal path
    /// that is the point: anything left open would leave traces beside a database
    /// this build has just declined to touch.
    /// </remarks>
    static void ReleaseDatabaseFiles(IHost host) => host.Dispose();
}
