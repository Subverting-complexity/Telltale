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
    string? GetPath(int pid);

    /// <summary>
    /// The command lines of every requested pid that could be read, in one call.
    /// A pid missing from the result had no readable command line. Never throws.
    /// </summary>
    IReadOnlyDictionary<int, string?> GetCommandLines(IReadOnlyCollection<int> pids);
}
