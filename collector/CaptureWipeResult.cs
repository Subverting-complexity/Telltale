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
/// How much smaller the database is than it was, measured the same way the rest
/// of the collector measures it: page count times page size, which is what the
/// size cap enforces against and what the window reports beside the clock.
///
/// Zero when nothing was deleted, and it can be zero after a real delete too.
/// SQLite frees whole pages, so a delete small enough to leave every page still
/// partly occupied returns none of them.
/// </param>
/// <param name="SpacePending">
/// Whether the space has been released without the folder shrinking yet.
///
/// A wipe finishes by folding the write ahead log back into the database and
/// shortening both files, and a reader can hold that off. The rows are already
/// committed by then, so the wipe carries on rather than failing, and
/// <see cref="BytesFreed"/> is a real count of pages the database gave up. What
/// has not happened is the files getting shorter, so someone who goes and looks
/// at the folder sees no change, and on a large delete sees it briefly grow.
///
/// True says the figure is early rather than wrong, which is the distinction the
/// window had no way to draw before #176. It is also true on the narrower path
/// where the housekeeping failed outright, where nothing is claimed at all and
/// the next rollup cycle reclaims the pages instead.
/// </param>
public sealed record CaptureWipeResult(long RowsDeleted, long BytesFreed, bool SpacePending = false);
