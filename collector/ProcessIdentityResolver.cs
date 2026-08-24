namespace Telltale.Collector;

/// <summary>
/// Works out each process's path and command line once, and remembers the answer
/// for as long as that process keeps running.
/// </summary>
/// <remarks>
/// A process instance is identified by its pid together with its creation time, so
/// a pid the operating system hands out again is a different instance and is looked
/// up again. Those two values are only ever written when the
/// <c>process_instance</c> row is first inserted, so fetching them on every tick was
/// work whose result was thrown away. Caching them also means a row that retention
/// removed while its process is still running is rebuilt with the same values
/// rather than with nulls.
/// </remarks>
public sealed class ProcessIdentityResolver
{
    /// <summary>
    /// Pids below this are the Idle and System pseudo-processes. They have no
    /// readable path or command line, so asking for one only wastes a lookup.
    /// </summary>
    public const int LowestRealPid = 5;

    private static readonly IReadOnlyDictionary<int, string?> NoCommandLines =
        new Dictionary<int, string?>();

    private readonly IProcessIdentitySource _source;
    private readonly bool _recordCommandLines;
    private readonly Dictionary<(int Pid, long CreateTime), ProcessIdentity> _known = new();

    public ProcessIdentityResolver(IProcessIdentitySource source, TelltaleConfig config)
    {
        _source = source;
        _recordCommandLines = config.RecordCommandLines;
    }

    /// <summary>How many process instances are currently remembered.</summary>
    public int KnownCount => _known.Count;

    /// <summary>
    /// Makes sure every given process instance has an identity, looking up only the
    /// ones not already known. All the command lines needed come back in one call.
    /// </summary>
    public void Resolve(IReadOnlyCollection<(int Pid, long CreateTime)> keys)
    {
        List<(int Pid, long CreateTime)>? missing = null;
        foreach (var key in keys)
        {
            if (key.Pid < LowestRealPid || _known.ContainsKey(key))
                continue;
            (missing ??= []).Add(key);
        }

        if (missing is null)
            return;

        var commandLines = NoCommandLines;
        if (_recordCommandLines)
        {
            var pids = new HashSet<int>(missing.Count);
            foreach (var key in missing)
                pids.Add(key.Pid);
            commandLines = _source.GetCommandLines(pids);
        }

        foreach (var key in missing)
        {
            string? commandLine = null;
            if (commandLines.TryGetValue(key.Pid, out var raw))
                commandLine = TelltaleConfig.RedactCommandLine(raw);

            _known[key] = new ProcessIdentity(_source.GetPath(key.Pid), commandLine);
        }
    }

    /// <summary>
    /// The identity of a process instance. Empty for one that was never resolved,
    /// which covers the Idle and System pseudo-processes.
    /// </summary>
    public ProcessIdentity For((int Pid, long CreateTime) key) =>
        _known.TryGetValue(key, out var identity) ? identity : default;

    /// <summary>
    /// Drops everything remembered about process instances that are no longer
    /// running, so the cache stays the size of the live process list.
    /// </summary>
    public void Prune(IReadOnlyCollection<(int Pid, long CreateTime)> stillRunning)
    {
        var running = stillRunning as HashSet<(int Pid, long CreateTime)> ?? new HashSet<(int Pid, long CreateTime)>(stillRunning);

        List<(int Pid, long CreateTime)>? gone = null;
        foreach (var key in _known.Keys)
        {
            if (!running.Contains(key))
                (gone ??= []).Add(key);
        }

        if (gone is null)
            return;

        foreach (var key in gone)
            _known.Remove(key);
    }
}
