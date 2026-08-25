using Microsoft.Extensions.Logging;

namespace Viewer.Tests;

/// <summary>
/// An <see cref="ILoggerProvider"/> that keeps what the viewer logged instead of
/// printing it, so a test can assert that a failing endpoint reported the failure
/// rather than discarding it.
///
/// collector.Tests has its own RecordingLogger serving the same purpose. This is a
/// deliberate second copy: the collector and viewer projects do not reference each
/// other, and neither do their test projects.
/// </summary>
public sealed class RecordingLoggerProvider : ILoggerProvider
{
    readonly List<LogEntry> _entries = [];
    readonly object _gate = new();

    /// <summary>
    /// A snapshot of what has been logged so far. Taken under the lock and copied,
    /// because the test host logs from whichever thread served the request.
    /// </summary>
    public IReadOnlyList<LogEntry> Entries
    {
        get { lock (_gate) { return _entries.ToArray(); } }
    }

    public ILogger CreateLogger(string categoryName) => new RecordingLogger(this, categoryName);

    public void Dispose()
    {
    }

    void Record(LogEntry entry)
    {
        lock (_gate) { _entries.Add(entry); }
    }

    public sealed record LogEntry(string Category, LogLevel Level, string Message, Exception? Exception);

    sealed class RecordingLogger : ILogger
    {
        readonly RecordingLoggerProvider _owner;
        readonly string _category;

        public RecordingLogger(RecordingLoggerProvider owner, string category)
        {
            _owner = owner;
            _category = category;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter) =>
            _owner.Record(new LogEntry(_category, logLevel, formatter(state, exception), exception));
    }
}
