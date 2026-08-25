using Telltale.App;

namespace Host.Tests;

/// <summary>
/// Telltale has no console any more, so this file is the only place a runtime
/// failure can be seen. It has to keep working when the disk does not, and it must
/// never be the reason the disk fills up.
/// </summary>
public class RollingLogFileTests : IDisposable
{
    readonly string _folder = Path.Combine(Path.GetTempPath(), $"telltale-log-{Guid.NewGuid():N}");
    readonly string _path;

    public RollingLogFileTests()
    {
        _path = Path.Combine(_folder, "telltale.log");
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder))
            Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public void It_creates_the_folder_it_writes_into()
    {
        new RollingLogFile(_path).Append("first line");

        Assert.True(File.Exists(_path));
        Assert.Contains("first line", File.ReadAllText(_path));
    }

    [Fact]
    public void It_appends_rather_than_replacing()
    {
        var log = new RollingLogFile(_path);

        log.Append("one");
        log.Append("two");

        var lines = File.ReadAllLines(_path);
        Assert.Equal(2, lines.Length);
        Assert.EndsWith("one", lines[0]);
        Assert.EndsWith("two", lines[1]);
    }

    [Fact]
    public void Every_line_carries_the_time_it_was_written()
    {
        // Lines arrive from two places: the logging pipeline and direct writes
        // from the application itself. Stamping them here is what keeps the two
        // readable in the same order.
        new RollingLogFile(_path).Append("something happened");

        var line = File.ReadAllLines(_path).Single();

        Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} something happened$", line);
    }

    [Fact]
    public void It_rotates_once_the_file_is_large_enough()
    {
        var log = new RollingLogFile(_path, maxBytes: 64);

        log.Append(new string('a', 100));
        log.Append("after the rotation");

        Assert.EndsWith("after the rotation", File.ReadAllText(_path).TrimEnd());
        Assert.Contains("aaa", File.ReadAllText(_path + ".1"));
    }

    [Fact]
    public void It_keeps_one_generation_and_no_more()
    {
        var log = new RollingLogFile(_path, maxBytes: 32);

        for (var i = 0; i < 5; i++)
            log.Append(new string('x', 50));

        Assert.True(File.Exists(_path));
        Assert.True(File.Exists(_path + ".1"));
        Assert.False(File.Exists(_path + ".2"));
    }

    [Fact]
    public void A_log_it_cannot_write_is_not_a_crash()
    {
        // Recording matters more than the record of it. A path that cannot be
        // written has to be swallowed, not thrown out of a background worker.
        Directory.CreateDirectory(_folder);
        File.WriteAllText(_path, "this is a file, not a folder");

        var log = new RollingLogFile(Path.Combine(_path, "nested.log"));

        log.Append("this cannot land anywhere");
    }

    [Fact]
    public void Concurrent_writers_do_not_lose_or_interleave_lines()
    {
        var log = new RollingLogFile(_path);

        Parallel.For(0, 200, i => log.Append($"line {i}"));

        var lines = File.ReadAllLines(_path);
        Assert.Equal(200, lines.Length);
        Assert.Equal(200, lines.Distinct().Count());
    }

    [Fact]
    public void The_log_sits_beside_the_capture_database()
    {
        // Someone who moves the database should not have to learn about a second
        // place to look for what went wrong with it.
        var database = Path.Combine("D:", "captures", "telltale.db");

        Assert.Equal(
            Path.Combine("D:", "captures", "telltale.log"),
            RollingLogFile.PathBeside(database));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("telltale.db")]
    public void A_database_path_with_no_folder_still_gives_a_usable_log_path(string database)
    {
        var path = RollingLogFile.PathBeside(database);

        Assert.EndsWith("telltale.log", path);
        Assert.True(Path.IsPathRooted(path));
    }
}
