namespace Telltale.Collector;

/// <summary>
/// What the collector knows about a process beyond the name and pid the sampler
/// already gives it. Both values are optional: a process can refuse to hand over
/// its executable path, and the command line is only ever read when the user has
/// turned that on.
/// </summary>
public readonly record struct ProcessIdentity(string? Path, string? CommandLine);

/// <summary>
/// Where a process's path and command line are read from.
/// </summary>
/// <remarks>
/// This exists as a seam so <see cref="ProcessIdentityResolver"/> can be tested
/// without Win32 or WMI. The command line lookup is deliberately batched: asking
/// for one pid at a time cost tens of milliseconds per process, which on a busy
/// machine took longer than the whole sampling interval.
/// </remarks>
public interface IProcessIdentitySource
{
    /// <summary>
    /// The full path of the executable behind <paramref name="pid"/>, or null when
    /// it cannot be read. Never throws.
    /// </summary>
    /// <remarks>
    /// A null here is an answer, not a failure. Protected processes and processes
    /// that have already exited both give one, and both are routine.
    /// </remarks>
    string? GetPath(int pid);

    /// <summary>
    /// The command lines of the requested pids, in one call. A pid present in the
    /// result with a null value has no readable command line, which is an answer. A
    /// pid missing from the result was not running by the time the lookup ran.
    /// </summary>
    /// <returns>
    /// Null when the lookup itself failed and answered nothing, which is different
    /// from answering that a process has no command line. The caller must not treat
    /// a failure as a set of empty answers, or one bad lookup would be remembered as
    /// the truth about every process running at that moment. Never throws.
    /// </returns>
    IReadOnlyDictionary<int, string?>? GetCommandLines(IReadOnlyCollection<int> pids);
}
