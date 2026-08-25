namespace Telltale.Collector;

/// <summary>
/// The one decision the collector makes about a database before it starts
/// recording, and the wording it uses to explain itself when the answer is no.
///
/// Both live here rather than in <c>Program.cs</c> because the startup path
/// itself is not reachable from a test. Launching the executable to watch it
/// exit is slow and unreliable, so the judgement and the message are kept as
/// plain functions a test can call directly, and the startup path is left with
/// nothing to get wrong beyond printing the result and exiting.
/// </summary>
public static class StartupDatabaseCheck
{
    /// <summary>
    /// Whether this build has to refuse the database it has just opened, and
    /// what to tell the user when it does. Null means carry on.
    ///
    /// Older versions are not refused: by the time this is called they have
    /// already been migrated forward. A newer version is refused rather than
    /// used, because a build cannot know what a migration written after it
    /// shipped did, and so cannot judge whether writing to the result is safe.
    /// The recovery paths are what settle it. Refusing leaves the user an easy
    /// way out and destroys nothing. Carrying on and writing rows that a later
    /// migration gave a different meaning to leaves wrong data mixed into
    /// recorded history with nothing marking it, which cannot be found
    /// afterwards and so cannot be put right.
    /// </summary>
    public static string? RefusalForNewerDatabase(int databaseVersion, int buildVersion, string databasePath)
    {
        if (databaseVersion <= buildVersion) return null;

        // The numbers on their own leave the user stuck, so the message names
        // both ways out: run the build that owns this database, or start a
        // clean recording somewhere else and keep this file for going back.
        return $"""
            Database schema version {databaseVersion} is newer than this build understands (version {buildVersion}).
            A newer TelltaleCapture.exe has already upgraded this database:
              {databasePath}

            Either run that newer build, or set databasePath in telltale.json to a new
            file to start a fresh recording and keep {Path.GetFileName(databasePath)} for when you go back.
            """;
    }
}
