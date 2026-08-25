using Microsoft.Extensions.Logging;

namespace Collector.Tests;

/// <summary>
/// An <see cref="ILogger"/> that keeps what was written instead of printing it.
///
/// Most tests only need something to pass to a constructor and ignore the
/// recording. The auto_vacuum tests assert on it, because whether the collector
/// tells the operator about an unconvertible database is part of the behaviour
/// under test rather than incidental output.
/// </summary>
internal sealed class RecordingLogger : ILogger
{
    public List<(LogLevel Level, string Message)> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
        Exception? exception, Func<TState, Exception?, string> formatter) =>
        Entries.Add((logLevel, formatter(state, exception)));
}
