using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Telltale.Collector;

namespace Collector.Tests;

/// <summary>
/// A <see cref="Database"/> backed by a throwaway file, plus the small query
/// helpers the Database tests need to look at the result independently of the
/// class under test.
///
/// A file rather than <c>:memory:</c> on purpose. The behaviour under test
/// includes WAL mode, the auto_vacuum header setting and the page count the file
/// reports, none of which an in-memory database represents faithfully.
///
/// Each instance owns its own file, so tests using it stay independent and can
/// run in parallel.
/// </summary>
public sealed class TempDatabase : IDisposable
{
    public string Path { get; }
    public Database Db { get; }

    /// <summary>Log records the <see cref="Database"/> wrote, for tests that assert on them.</summary>
    public IReadOnlyList<(LogLevel Level, string Message)> LogEntries => _logger.Entries;

    private readonly RecordingLogger _logger = new();

    public TempDatabase(string prefix, bool vacuumOnStartup = false)
        : this(prefix, Guid.NewGuid().ToString(), vacuumOnStartup)
    {
    }

    private TempDatabase(string prefix, string id, bool vacuumOnStartup)
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"telltale_{prefix}_{id}.db");
        Db = new Database(Path, _logger, vacuumOnStartup);
    }

    /// <summary>
    /// Closes this database and reopens the same file, the way restarting the
    /// collector would. Used by the tests that care what happens on a second open.
    /// </summary>
    public Database Reopen(bool vacuumOnStartup = false)
    {
        Db.Dispose();
        SqliteConnection.ClearAllPools();
        _logger.Entries.Clear();
        return new Database(Path, _logger, vacuumOnStartup);
    }

    public SqliteConnection Connect()
    {
        var conn = new SqliteConnection($"Data Source={Path}");
        conn.Open();
        return conn;
    }

    public object? Scalar(string sql)
    {
        using var conn = Connect();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    public void Execute(string sql)
    {
        using var conn = Connect();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public int Count(string table, string? where = null) =>
        (int)(long)Scalar($"SELECT COUNT(*) FROM {table}" + (where is null ? "" : $" WHERE {where}"))!;

    public long[] Timestamps(string table) => Column(table, "ts");

    public long[] Column(string table, string column)
    {
        using var conn = Connect();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {column} FROM {table} ORDER BY ts";
        using var reader = cmd.ExecuteReader();

        var results = new List<long>();
        while (reader.Read())
            results.Add(reader.GetInt64(0));
        return [.. results];
    }

    /// <summary>Reads one column of one row as a double, for aggregate assertions.</summary>
    public double Real(string sql) => Convert.ToDouble(Scalar(sql));

    public void Dispose()
    {
        Db.Dispose();

        // The pooled connections keep a handle on the file, which stops the delete
        // below from succeeding on Windows.
        SqliteConnection.ClearAllPools();

        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { File.Delete(Path + suffix); } catch { /* best effort cleanup */ }
        }
    }

    /// <summary>
    /// Keeps what was logged instead of printing it. Tests that only need a
    /// <see cref="ILogger"/> ignore the recording; the auto_vacuum tests assert on it.
    /// </summary>
    private sealed class RecordingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}
