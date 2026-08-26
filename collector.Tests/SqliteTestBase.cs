using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Telltale.Collector;

namespace Collector.Tests;

/// <summary>
/// Shared scaffolding for tests that drive a real <see cref="Database"/> against
/// a temporary SQLite file: the database itself, direct query helpers for reading
/// back what the collector wrote, and cleanup that also removes the write-ahead
/// log files SQLite leaves alongside the database.
///
/// Tests read their assertions back through a separate connection rather than
/// through <see cref="Database"/>, because the point is usually to check what
/// actually landed on disk rather than what the collector believes it wrote.
/// </summary>
public abstract class SqliteTestBase : IDisposable
{
    protected string DbPath { get; }

    protected Database Db { get; }

    /// <param name="prefix">
    /// Distinguishes one suite's temporary files from another's when a test run
    /// leaves something behind.
    /// </param>
    protected SqliteTestBase(string prefix)
    {
        DbPath = Path.Combine(Path.GetTempPath(), $"telltale_{prefix}_{Guid.NewGuid()}.db");
        Db = new Database(DbPath, new SilentLogger());
    }

    protected SqliteConnection Connect() => TestConnection.Open(DbPath);

    protected object? Scalar(string sql)
    {
        using var conn = Connect();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    protected void Execute(string sql)
    {
        using var conn = Connect();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    protected int Count(string table, string? where = null) =>
        (int)(long)Scalar($"SELECT COUNT(*) FROM {table}" + (where is null ? "" : $" WHERE {where}"))!;

    /// <summary>Reads one numeric column of one row, for aggregate assertions.</summary>
    protected double Real(string sql) => Convert.ToDouble(Scalar(sql));

    protected long[] Timestamps(string table) => Column(table, "ts");

    protected long[] Column(string table, string column)
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

    public void Dispose()
    {
        // Disposing the database closes its connection for real, because neither
        // it nor TestConnection pools, so the sidecar files are already gone by
        // the time the loop runs.
        Db.Dispose();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { File.Delete(DbPath + suffix); } catch { /* best effort cleanup */ }
        }
        GC.SuppressFinalize(this);
    }

    private sealed class SilentLogger : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => false;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter) { }
    }
}
