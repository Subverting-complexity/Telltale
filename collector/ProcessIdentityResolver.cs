namespace Telltale.Collector;

/// <summary>
/// Works out each process's path and command line once, and remembers the answer
/// for as long as that process keeps running.
/// </summary>
/// <remarks>
/// <para>
/// A process instance is identified by its pid together with its creation time, so
/// a pid the operating system hands out again is a different instance and is looked
/// up again. Those two values are only ever written when the
/// <c>process_instance</c> row is first inserted, so fetching them on every tick was
/// work whose result was thrown away. Caching them also means a row that retention
/// removed while its process is still running is rebuilt with the same values
/// rather than with nulls.
/// </para>
/// <para>
/// Paths and command lines are remembered separately because they can fail
/// separately. A path that comes back null is a real answer about one process. A
/// command line lookup that fails answers nothing about any process, so nothing is
/// remembered from it and the next tick asks again. That retry costs one WMI query,
/// not one per process, because the paths are already cached by then.
/// </para>
/// <para>
/// This type is not thread safe. It holds plain dictionaries and expects to be
/// driven only from the sampling loop in <see cref="CollectorWorker"/>, which is
/// serial.
/// </para>
/// </remarks>
public sealed class ProcessIdentityResolver
{
    /// <summary>
    /// Pids below this are the Idle and System pseudo-processes. They have no
    /// readable path or command line, so asking for one only wastes a lookup.
    /// </summary>
    public const int LowestRealPid = 5;

    private readonly IProcessIdentitySource _source;
    private readonly bool _recordCommandLines;
    private readonly Dictionary<(int Pid, long CreateTime), string?> _paths = new();
    private readonly Dictionary<(int Pid, long CreateTime), string?> _commandLines = new();

    public ProcessIdentityResolver(IProcessIdentitySource source, TelltaleConfig config)
    {
        _source = source;
        _recordCommandLines = config.RecordCommandLines;
    }

    /// <summary>How many process instances currently have a remembered path.</summary>
    public int KnownCount => _paths.Count;

    /// <summary>
    /// Makes sure every given process instance has an identity, looking up only what
    /// is not already known. All the command lines needed come back in one call.
    /// </summary>
    public void Resolve(IReadOnlyCollection<(int Pid, long CreateTime)> keys)
    {
        ResolveCommandLines(keys);
        ResolvePaths(keys);
    }

    private void ResolveCommandLines(IReadOnlyCollection<(int Pid, long CreateTime)> keys)
    {
        if (!_recordCommandLines)
            return;

        List<(int Pid, long CreateTime)>? missing = null;
        HashSet<int>? pids = null;

        foreach (var key in keys)
        {
            if (key.Pid < LowestRealPid || _commandLines.ContainsKey(key))
                continue;
            (missing ??= []).Add(key);
            (pids ??= []).Add(key.Pid);
        }

        if (missing is null || pids is null)
            return;

        var answered = _source.GetCommandLines(pids);
        if (answered is null)
        {
            // The lookup failed rather than answering. Remembering nothing here is
            // what keeps one bad tick from being taken as the truth about every
            // process that happened to be running during it.
            return;
        }

        foreach (var key in missing)
        {
            answered.TryGetValue(key.Pid, out var raw);
            _commandLines[key] = TelltaleConfig.RedactCommandLine(raw);
        }
    }

    private void ResolvePaths(IReadOnlyCollection<(int Pid, long CreateTime)> keys)
    {
        foreach (var key in keys)
        {
            if (key.Pid < LowestRealPid || _paths.ContainsKey(key))
                continue;
            _paths[key] = _source.GetPath(key.Pid);
        }
    }

    /// <summary>
    /// The identity of a process instance. Empty for one that was never resolved,
    /// which covers the Idle and System pseudo-processes.
    /// </summary>
    public ProcessIdentity For((int Pid, long CreateTime) key)
    {
        _paths.TryGetValue(key, out var path);
        _commandLines.TryGetValue(key, out var commandLine);
        return new ProcessIdentity(path, commandLine);
    }

    /// <summary>
    /// Drops everything remembered about process instances that are no longer
    /// running, so the cache stays the size of the live process list.
    /// </summary>
    public void Prune(IReadOnlyCollection<(int Pid, long CreateTime)> stillRunning)
    {
        var running = stillRunning as HashSet<(int Pid, long CreateTime)>
            ?? new HashSet<(int Pid, long CreateTime)>(stillRunning);

        PruneOne(_paths, running);
        PruneOne(_commandLines, running);
    }

    private static void PruneOne(
        Dictionary<(int Pid, long CreateTime), string?> cache,
        HashSet<(int Pid, long CreateTime)> running)
    {
        List<(int Pid, long CreateTime)>? gone = null;
        foreach (var key in cache.Keys)
        {
            if (!running.Contains(key))
                (gone ??= []).Add(key);
        }

        if (gone is null)
            return;

        foreach (var key in gone)
            cache.Remove(key);
    }
}
