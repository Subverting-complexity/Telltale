namespace Telltale.Collector;

/// <summary>
/// What the collector says when it cannot use the database it was pointed at.
/// There are two reasons for that and this holds both: the file will not open,
/// or it belongs to a build newer than this one.
///
/// They live here rather than in <c>Program.cs</c> because the startup path
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

    /// <summary>
    /// What to tell the user when the database could not be opened, or could not
    /// be brought up to date once it was.
    ///
    /// The collector runs in the background with no interface, so this failure
    /// is both silent and repeating: it happens again on every start until
    /// somebody works out why. Naming the file and the underlying error is the
    /// difference between a fixable problem and a recorder that quietly stopped.
    ///
    /// The heading says "use" rather than "open" because the caller catches
    /// everything the constructor can throw, and that includes a SQLite error
    /// raised by a migration after the file opened perfectly well. Claiming the
    /// file would not open would be wrong in that case, so the suggestions below
    /// are stated as conditional rather than as the diagnosis.
    /// </summary>
    public static string DescribeOpenFailure(string databasePath, Exception error) =>
        $"""
        Cannot use the Telltale database:
          {databasePath}

        {error.Message}

        If the file cannot be opened, check that no other program has it open, that
        the drive it is on is available, and that this account can write to that
        folder. To record somewhere else instead, set databasePath in telltale.json.
        """;
}
