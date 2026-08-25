using Telltale.Collector;

namespace Telltale.App;

/// <summary>
/// How the window asks for recorded history to be thrown away.
/// </summary>
/// <remarks>
/// The listener holds one of these rather than the recorder's database, for two
/// reasons. It keeps the listener testable without a real capture file, and it
/// keeps the destructive surface down to the two calls the window actually makes,
/// rather than handing an HTTP endpoint everything <see cref="Database"/> can do.
/// </remarks>
interface ICaptureWipe
{
    /// <summary>Throws away everything recorded so far.</summary>
    CaptureWipeResult All();

    /// <summary>
    /// Throws away everything recorded between the two moments, both ends
    /// included, in epoch milliseconds.
    /// </summary>
    CaptureWipeResult Range(long fromMs, long toMs);
}

/// <summary>
/// Wipes through the running recorder's own database connection.
/// </summary>
/// <remarks>
/// Going through the recorder rather than opening a second writable connection is
/// the point of this class. The two would be separate writers to one file, and the
/// large delete a wipe performs would hold the write lock long enough for the
/// sampler's next tick to fail on a busy database. One connection, one lock, and
/// the wipe simply takes its turn.
/// </remarks>
sealed class RecorderCaptureWipe : ICaptureWipe
{
    readonly Database _database;

    public RecorderCaptureWipe(Database database) => _database = database;

    public CaptureWipeResult All() => _database.WipeAll();

    public CaptureWipeResult Range(long fromMs, long toMs) => _database.WipeRange(fromMs, toMs);
}
