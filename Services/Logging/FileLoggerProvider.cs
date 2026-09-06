using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using BAR_Advanced_Launcher_2.Infrastructure;
using Microsoft.Extensions.Logging;

namespace BAR_Advanced_Launcher_2.Services.Logging;

/// <summary>
/// Appends to a per-day file under <see cref="AppPaths.LogsFolder"/>. Deliberately
/// simple: a crash on startup has to leave a trace even if nothing else came up.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentDictionary<string, FileLogger> _loggers = new();
    private readonly object _writeGate = new();
    private readonly LogLevel _minimumLevel;
    private readonly string _folder;

    public FileLoggerProvider(string folder, LogLevel minimumLevel = LogLevel.Debug)
    {
        _folder = folder;
        _minimumLevel = minimumLevel;
        Directory.CreateDirectory(_folder);
    }

    public ILogger CreateLogger(string categoryName) =>
        _loggers.GetOrAdd(categoryName, name => new FileLogger(name, this));

    public void Dispose() => _loggers.Clear();

    private string CurrentFile => Path.Combine(_folder, $"app-{DateTime.Now:yyyyMMdd}.log");

    private void Write(string line)
    {
        lock (_writeGate)
        {
            try
            {
                File.AppendAllText(CurrentFile, line + Environment.NewLine, Encoding.UTF8);
            }
            catch (IOException)
            {
                // The log file is the last thing that should take the app down.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private sealed class FileLogger : ILogger
    {
        private readonly string _category;
        private readonly FileLoggerProvider _owner;

        public FileLogger(string category, FileLoggerProvider owner)
        {
            _category = category;
            _owner = owner;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) =>
            logLevel != LogLevel.None && logLevel >= _owner._minimumLevel;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var text = new StringBuilder()
                .Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz"))
                .Append(" [").Append(logLevel).Append("] ")
                .Append(_category).Append(": ")
                .Append(formatter(state, exception));

            if (exception is not null)
            {
                text.AppendLine().Append(exception);
            }

            _owner.Write(text.ToString());
        }
    }
}
