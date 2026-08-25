namespace Telltale.Collector;

/// <summary>
/// What a wipe actually did.
/// </summary>
/// <remarks>
/// Both numbers are reported back to the person who asked for the wipe, so both
/// are here rather than being logged and forgotten. A wipe that matched nothing
/// is a normal outcome, not a failure, and a zero row count is how it says so.
/// </remarks>
/// <param name="RowsDeleted">
/// How many rows went, counted across every table the wipe touched, including
/// the <c>process_instance</c> rows the cleanup afterwards removed.
/// </param>
/// <param name="BytesFreed">
/// How much smaller the database file is than it was. Zero when nothing was
/// deleted, and it can be zero after a real delete too: SQLite returns freed
/// pages to the file's own free list, and only the incremental vacuum that runs
/// afterwards hands whole pages back to the filesystem.
/// </param>
public sealed record CaptureWipeResult(long RowsDeleted, long BytesFreed);
