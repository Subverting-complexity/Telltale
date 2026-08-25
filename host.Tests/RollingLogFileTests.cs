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
        Assert.Equal(["one", "two"], lines);
    }

    [Fact]
    public void It_rotates_once_the_file_is_large_enough()
    {
        var log = new RollingLogFile(_path, maxBytes: 64);

        log.Append(new string('a', 100));
        log.Append("after the rotation");

        Assert.Equal("after the rotation", File.ReadAllText(_path).TrimEnd());
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
    public void The_default_path_sits_beside_the_capture_database()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Telltale", "telltale.log");

        Assert.Equal(expected, RollingLogFile.DefaultPath);
    }
}
