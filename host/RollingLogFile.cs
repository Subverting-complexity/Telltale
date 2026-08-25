using Microsoft.Extensions.Logging;

namespace Telltale.App;

/// <summary>
/// A small log file beside the capture database.
/// </summary>
/// <remarks>
/// The two executables Telltale used to ship wrote to a console. A WinExe has no
/// console, so without this the recorder would fail at runtime with nothing said
/// anywhere, which is worse than what it replaced.
///
/// It stays on this machine. Nothing here is sent anywhere, and it records only
/// what the collector already logged: never a process command line.
/// </remarks>
sealed class RollingLogFile
{
    readonly string _path;
    readonly long _maxBytes;
    readonly Lock _gate = new();

    public RollingLogFile(string path, long maxBytes = 1024 * 1024)
    {
        _path = path;
        _maxBytes = maxBytes;
    }

    /// <summary>The log Telltale writes to when nothing else is configured.</summary>
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Telltale", "telltale.log");

    public void Append(string line)
    {
        lock (_gate)
        {
            try
            {
                var folder = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(folder))
                    Directory.CreateDirectory(folder);

                if (File.Exists(_path) && new FileInfo(_path).Length >= _maxBytes)
                {
                    // One generation back is enough to see what led to a failure
                    // without the log ever being a reason to run out of disk.
                    var previous = _path + ".1";
                    File.Delete(previous);
                    File.Move(_path, previous);
                }

                File.AppendAllText(_path, line + Environment.NewLine);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Losing the log is not a reason to stop recording.
            }
        }
    }
}

/// <summary>Sends log output to a <see cref="RollingLogFile"/>.</summary>
sealed class FileLoggerProvider : ILoggerProvider
{
    readonly RollingLogFile _file;

    public FileLoggerProvider(RollingLogFile file) => _file = file;

    public ILogger CreateLogger(string categoryName) => new FileLogger(_file, categoryName);

    public void Dispose()
    {
    }

    sealed class FileLogger : ILogger
    {
        readonly RollingLogFile _file;
        readonly string _category;

        public FileLogger(RollingLogFile file, string category)
        {
            _file = file;
            _category = category;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;

            var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} {logLevel,-11} {_category} {formatter(state, exception)}";
            if (exception is not null)
                line += Environment.NewLine + exception;

            _file.Append(line);
        }
    }
}
