using System;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace BAR_Advanced_Launcher_2.Services.Logging;

/// <summary>Routes every <see cref="ILogger"/> write into the in-app log pane.</summary>
public sealed class AppLoggerProvider : ILoggerProvider
{
    private readonly IAppLogSink _sink;
    private readonly ConcurrentDictionary<string, AppLogger> _loggers = new();

    public AppLoggerProvider(IAppLogSink sink) => _sink = sink;

    public ILogger CreateLogger(string categoryName) =>
        _loggers.GetOrAdd(categoryName, name => new AppLogger(name, _sink));

    public void Dispose() => _loggers.Clear();

    private sealed class AppLogger : ILogger
    {
        private readonly string _category;
        private readonly IAppLogSink _sink;

        public AppLogger(string category, IAppLogSink sink)
        {
            _category = category;
            _sink = sink;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

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

            _sink.Add(new AppLogEntry(
                DateTimeOffset.Now,
                logLevel,
                _category,
                formatter(state, exception),
                exception?.ToString()));
        }
    }
}
