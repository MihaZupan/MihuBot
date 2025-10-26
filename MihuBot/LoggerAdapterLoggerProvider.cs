using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MihuBot.Configuration;

#nullable enable

namespace MihuBot;

[ProviderAlias("LoggerAdapter")]
public sealed class LoggerAdapterLoggerProvider(Logger logger, ServiceConfiguration serviceConfiguration) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new LoggerAdapterLogger(logger, serviceConfiguration, categoryName);

    public void Dispose() { }

    private sealed class LoggerAdapterLogger(Logger logger, ServiceConfiguration serviceConfiguration, string categoryName) : ILogger
    {
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullLogger.Instance.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel)
        {
            if (logLevel == LogLevel.None)
            {
                return false;
            }

            return logLevel >= LogLevel.Information || serviceConfiguration.LoggerTrace;
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            string prefix = $"[{logLevel} {eventId.Name ?? eventId.Id.ToString()} {categoryName}]";
            string formatted = formatter(state, exception);
            string message = exception is null ? $"{prefix} {formatted}" : $"{prefix} {formatted} Exception: {exception}";

            logger.DebugLog(message);
        }
    }
}
