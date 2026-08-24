using Telltale.Collector;

namespace Collector.Tests;

/// <summary>
/// The resolver is what stops the collector paying for a process's path and command
/// line on every tick. These tests pin the two properties that matter: each running
/// process instance is looked up once, and everything still needing a command line
/// is asked for in a single call.
/// </summary>
public class ProcessIdentityResolverTests
{
    private static TelltaleConfig Config(bool recordCommandLines) =>
        new() { RecordCommandLines = recordCommandLines };

    private static (int Pid, long CreateTime) Key(int pid, long createTime = 1000) =>
        (pid, createTime);

    [Fact]
    public void Resolve_LooksAnInstanceUpOnlyOnce()
    {
        var source = new RecordingIdentitySource();
        var resolver = new ProcessIdentityResolver(source, Config(recordCommandLines: true));
        var keys = new[] { Key(100), Key(200) };

        resolver.Resolve(keys);
        resolver.Resolve(keys);
        resolver.Resolve(keys);

        // Three ticks over the same two processes, one lookup each. Before this the
        // collector paid the full cost every five seconds and threw the answer away,
        // because the values are only written when the row is first inserted.
        Assert.Equal(new[] { 100, 200 }, source.PathRequests);
        Assert.Single(source.CommandLineBatches);
    }

    [Fact]
    public void Resolve_LooksUpOnlyTheInstancesItHasNotSeen()
    {
        var source = new RecordingIdentitySource();
        var resolver = new ProcessIdentityResolver(source, Config(recordCommandLines: true));

        resolver.Resolve([Key(100), Key(200)]);
        source.Clear();

        resolver.Resolve([Key(100), Key(200), Key(300)]);

        Assert.Equal(new[] { 300 }, source.PathRequests);
        Assert.Equal(new[] { 300 }, Assert.Single(source.CommandLineBatches));
    }

    [Fact]
    public void Resolve_AsksForEveryMissingCommandLineInOneCall()
    {
        var source = new RecordingIdentitySource();
        var resolver = new ProcessIdentityResolver(source, Config(recordCommandLines: true));

        resolver.Resolve([Key(100), Key(200), Key(300)]);

        // One batch, not one call per process. Asking per process cost tens of
        // milliseconds each, which is what made a busy machine miss its interval.
        var batch = Assert.Single(source.CommandLineBatches);
        Assert.Equal(new[] { 100, 200, 300 }, batch.Order().ToArray());
    }

    [Fact]
    public void Resolve_MakesNoCommandLineCallAtAllWhenRecordingIsOff()
    {
        var source = new RecordingIdentitySource();
        var resolver = new ProcessIdentityResolver(source, Config(recordCommandLines: false));

        resolver.Resolve([Key(100), Key(200)]);

        Assert.Empty(source.CommandLineBatches);
        Assert.Null(resolver.For(Key(100)).CommandLine);
        // The path is still wanted: it is what tells one node.exe from another.
        Assert.Equal(@"C:\p100.exe", resolver.For(Key(100)).Path);
    }

    [Fact]
    public void Resolve_RedactsCommandLinesBeforeKeepingThem()
    {
        var source = new RecordingIdentitySource();
        source.CommandLines[100] = @"C:\app.exe --password hunter2";
        var resolver = new ProcessIdentityResolver(source, Config(recordCommandLines: true));

        resolver.Resolve([Key(100)]);

        var stored = resolver.For(Key(100)).CommandLine;
        Assert.NotNull(stored);
        Assert.DoesNotContain("hunter2", stored);
        Assert.Contains("REDACTED", stored);
    }

    [Fact]
    public void Resolve_RemembersNothingFromALookupThatFailed()
    {
        var source = new RecordingIdentitySource { CommandLineLookupFails = true };
        source.CommandLines[100] = @"C:pp.exe --one";
        var resolver = new ProcessIdentityResolver(source, Config(recordCommandLines: true));

        resolver.Resolve([Key(100)]);
        Assert.Null(resolver.For(Key(100)).CommandLine);

        source.CommandLineLookupFails = false;
        source.Clear();
        resolver.Resolve([Key(100)]);

        // A failed lookup answers nothing about any process. Remembering it as "this
        // process has no command line" would take one bad moment as the truth about
        // everything running during it, for as long as those processes keep running.
        Assert.Equal(@"C:pp.exe --one", resolver.For(Key(100)).CommandLine);
    }

    [Fact]
    public void Resolve_DoesNotRepeatThePathLookupWhileTheCommandLineLookupKeepsFailing()
    {
        var source = new RecordingIdentitySource { CommandLineLookupFails = true };
        var resolver = new ProcessIdentityResolver(source, Config(recordCommandLines: true));

        resolver.Resolve([Key(100), Key(200)]);
        source.Clear();
        resolver.Resolve([Key(100), Key(200)]);

        // Retrying costs one command line lookup per tick, not one path lookup per
        // process, because the paths are a separate answer and they succeeded.
        Assert.Empty(source.PathRequests);
        Assert.Single(source.CommandLineBatches);
        Assert.Equal(@"C:\p100.exe", resolver.For(Key(100)).Path);
    }

    [Fact]
    public void Resolve_KeepsAnAnsweredAbsenceRatherThanAskingAgainForever()
    {
        var source = new RecordingIdentitySource();
        var resolver = new ProcessIdentityResolver(source, Config(recordCommandLines: true));

        // The lookup succeeded and said this process has no readable command line. That
        // is an answer, so asking again every tick would put back the per-tick cost for
        // exactly the processes that will never have one.
        resolver.Resolve([Key(100)]);
        source.Clear();
        resolver.Resolve([Key(100)]);

        Assert.Empty(source.CommandLineBatches);
        Assert.Null(resolver.For(Key(100)).CommandLine);
    }

    [Fact]
    public void Resolve_LeavesThePseudoProcessesAlone()
    {
        var source = new RecordingIdentitySource();
        var resolver = new ProcessIdentityResolver(source, Config(recordCommandLines: true));

        // Idle and System have neither a readable path nor a command line, so asking
        // for one only spends a lookup to be told no.
        resolver.Resolve([Key(0), Key(4), Key(100)]);

        Assert.Equal(new[] { 100 }, source.PathRequests);
        Assert.Equal(default, resolver.For(Key(0)));
        Assert.Equal(default, resolver.For(Key(4)));
    }

    [Fact]
    public void Resolve_TreatsAReusedPidAsADifferentProcess()
    {
        var source = new RecordingIdentitySource();
        var resolver = new ProcessIdentityResolver(source, Config(recordCommandLines: true));

        resolver.Resolve([Key(100, createTime: 1000)]);
        source.Clear();
        source.Paths[100] = @"C:\something-else.exe";

        resolver.Resolve([Key(100, createTime: 2000)]);

        // Windows hands a pid out again once the process behind it has gone. The
        // creation time is what tells the two apart, so the second one is looked up.
        Assert.Equal(new[] { 100 }, source.PathRequests);
        Assert.Equal(@"C:\p100.exe", resolver.For(Key(100, 1000)).Path);
        Assert.Equal(@"C:\something-else.exe", resolver.For(Key(100, 2000)).Path);
    }

    [Fact]
    public void Prune_ForgetsInstancesThatAreNoLongerRunning()
    {
        var source = new RecordingIdentitySource();
        var resolver = new ProcessIdentityResolver(source, Config(recordCommandLines: true));

        resolver.Resolve([Key(100), Key(200), Key(300)]);
        Assert.Equal(3, resolver.KnownCount);

        resolver.Prune([Key(100), Key(300)]);

        // The cache has to stay the size of the live process list, otherwise a machine
        // that starts and stops a lot of short-lived processes grows it without limit.
        Assert.Equal(2, resolver.KnownCount);
        Assert.Equal(default, resolver.For(Key(200)));
        Assert.Equal(@"C:\p100.exe", resolver.For(Key(100)).Path);
    }

    [Fact]
    public void Prune_MakesAReturningInstanceCostAFreshLookup()
    {
        var source = new RecordingIdentitySource();
        var resolver = new ProcessIdentityResolver(source, Config(recordCommandLines: true));

        resolver.Resolve([Key(100)]);
        resolver.Prune(Array.Empty<(int Pid, long CreateTime)>());
        source.Clear();

        resolver.Resolve([Key(100)]);

        Assert.Equal(new[] { 100 }, source.PathRequests);
    }

    [Fact]
    public void For_ReturnsNothingForAnInstanceItWasNeverGiven()
    {
        var source = new RecordingIdentitySource();
        var resolver = new ProcessIdentityResolver(source, Config(recordCommandLines: true));

        Assert.Equal(default, resolver.For(Key(999)));
    }

    /// <summary>
    /// Stands in for Win32 and WMI, and records what it was asked for so the tests can
    /// assert on the number and shape of the lookups rather than on their results.
    /// </summary>
    private sealed class RecordingIdentitySource : IProcessIdentitySource
    {
        public Dictionary<int, string?> Paths { get; } = new();
        public Dictionary<int, string?> CommandLines { get; } = new();
        public List<int> PathRequests { get; } = [];
        public List<int[]> CommandLineBatches { get; } = [];

        public void Clear()
        {
            PathRequests.Clear();
            CommandLineBatches.Clear();
        }

        public string? GetPath(int pid)
        {
            PathRequests.Add(pid);
            return Paths.TryGetValue(pid, out var path) ? path : $@"C:\p{pid}.exe";
        }

        /// <summary>When set, the next lookup fails outright instead of answering.</summary>
        public bool CommandLineLookupFails { get; set; }

        public IReadOnlyDictionary<int, string?>? GetCommandLines(IReadOnlyCollection<int> pids)
        {
            CommandLineBatches.Add([.. pids]);

            if (CommandLineLookupFails)
                return null;

            var found = new Dictionary<int, string?>();
            foreach (var pid in pids)
            {
                if (CommandLines.TryGetValue(pid, out var commandLine))
                    found[pid] = commandLine;
            }
            return found;
        }
    }
}
