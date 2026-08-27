using Microsoft.Data.Sqlite;

namespace Telltale.Viewer;

/// <summary>
/// Reads what the recorder wrote down about giving up detail under size pressure,
/// so the window can explain why a day is being shown at a coarser width than the
/// configuration asks for.
/// </summary>
/// <remarks>
/// Without this the two look identical from the outside. A person who set the one
/// minute detail to be kept for a week, and finds last Tuesday served at ten
/// minutes, has no way to tell a setting being ignored from the size limit doing
/// exactly what it was asked to.
///
/// The viewer cannot say what was configured. That lives in <c>telltale.json</c>,
/// which belongs to the collector, and the two projects deliberately do not
/// reference each other. It does not need to: a row exists here only because a
/// tier gave something up, so the presence of one is the whole signal.
/// </remarks>
public static class TierPressureReader
{
    /// <returns>
    /// True when at least one tier has had its hold on data shortened.
    /// </returns>
    /// <remarks>
    /// A plain yes or no, rather than how far each tier was pulled in. The obvious
    /// single number, the shortest hold across every tier that gave something up,
    /// cannot be turned into an accurate sentence: if only the daily tier gave
    /// anything up then that number is measured in hundreds of days and says
    /// nothing about how far back fine detail reaches. Saying which tier now holds
    /// what would be the useful version, and it needs the window to name the tiers,
    /// which is a bigger change than the message is currently worth.
    /// </remarks>
    public static bool Read(SqliteConnection conn)
    {
        // A database written by a build that predates the size pressure work has no
        // such table. That is not an error and not a failure to read: it is a
        // capture that has never had detail taken off it, which is exactly what an
        // empty answer means.
        if (!HasTable(conn, "tier_pressure")) return false;

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM tier_pressure LIMIT 1";

        object? value = cmd.ExecuteScalar();

        return value is not (null or DBNull);
    }

    static bool HasTable(SqliteConnection conn, string table)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = @name";
        cmd.Parameters.AddWithValue("@name", table);

        return cmd.ExecuteScalar() is not null;
    }
}
