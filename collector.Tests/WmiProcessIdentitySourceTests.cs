using Telltale.Collector;

namespace Collector.Tests;

/// <summary>
/// The WMI call itself cannot be tested, but the shaping of what it returns can, and
/// that is where a wrong answer would hide. A row whose ProcessId does not read as a
/// number is dropped, and a machine where every row is dropped looks exactly like a
/// machine with no command lines at all, so the count of dropped rows has to come
/// back out rather than being swallowed.
/// </summary>
public class WmiProcessIdentitySourceTests
{
    private static (object? ProcessId, object? CommandLine) Row(object? pid, object? commandLine) =>
        (pid, commandLine);

    [Fact]
    public void ShapeRows_KeepsOnlyThePidsThatWereAskedFor()
    {
        var rows = new[]
        {
            Row(100u, @"C:\a.exe --one"),
            Row(200u, @"C:\b.exe --two"),
            Row(300u, @"C:\c.exe --three"),
        };

        var found = WmiProcessIdentitySource.ShapeRows(rows, new[] { 100, 300 }, out int unreadable);

        // The query has no WHERE clause, because WQL has no IN and one query per pid is
        // what caused the stall. Filtering happens here instead.
        Assert.Equal(0, unreadable);
        Assert.Equal(new[] { 100, 300 }, found.Keys.Order().ToArray());
        Assert.Equal(@"C:\a.exe --one", found[100]);
        Assert.Equal(@"C:\c.exe --three", found[300]);
    }

    [Fact]
    public void ShapeRows_KeepsAProcessWithNoCommandLineAsAnAnswer()
    {
        var rows = new[] { Row(100u, null) };

        var found = WmiProcessIdentitySource.ShapeRows(rows, new[] { 100 }, out int unreadable);

        // Present with a null value means "this process has no readable command line",
        // which is a real answer and must not be confused with the lookup failing.
        Assert.Equal(0, unreadable);
        Assert.True(found.ContainsKey(100));
        Assert.Null(found[100]);
    }

    [Fact]
    public void ShapeRows_LeavesOutAPidThatWasAskedForButDidNotComeBack()
    {
        var rows = new[] { Row(100u, @"C:\a.exe") };

        var found = WmiProcessIdentitySource.ShapeRows(rows, new[] { 100, 200 }, out _);

        // 200 exited between the sample and the query. Absent, rather than present and
        // null, so the caller can tell the two apart.
        Assert.False(found.ContainsKey(200));
    }

    [Fact]
    public void ShapeRows_CountsRowsWhoseProcessIdIsNotTheDocumentedType()
    {
        var rows = new[]
        {
            Row(100u, @"C:\a.exe"),
            Row("200", @"C:\b.exe"),
            Row(null, @"C:\c.exe"),
            Row(300, @"C:\d.exe"),
        };

        var found = WmiProcessIdentitySource.ShapeRows(rows, new[] { 100, 200, 300 }, out int unreadable);

        // Win32_Process.ProcessId is documented as a uint32. If it ever were not, every
        // row would be dropped in silence, so the count is reported and logged.
        Assert.Equal(3, unreadable);
        Assert.Equal(new[] { 100 }, found.Keys.ToArray());
    }

    [Fact]
    public void ShapeRows_ReturnsNothingWhenNoRowsCameBack()
    {
        var found = WmiProcessIdentitySource.ShapeRows([], new[] { 100 }, out int unreadable);

        Assert.Empty(found);
        Assert.Equal(0, unreadable);
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    [InlineData(59, false)]
    [InlineData(60, true)]
    [InlineData(120, true)]
    public void FailureReporting_GoesQuietOnceTheProblemIsStanding(
        int consecutiveFailures, bool expected)
    {
        // WMI being unavailable is a condition rather than an event. The first couple
        // of failures each get a line, then it repeats rarely, so a user who turned
        // command lines on still learns they are not getting any.
        Assert.Equal(expected, WmiProcessIdentitySource.ShouldReportFailure(consecutiveFailures));
    }
}
