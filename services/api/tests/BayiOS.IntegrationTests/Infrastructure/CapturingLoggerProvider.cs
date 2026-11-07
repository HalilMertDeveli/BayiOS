using System.Collections.Concurrent;
using BayiOS.Api.Platform.Observability;
using Microsoft.Extensions.Logging;

namespace BayiOS.IntegrationTests.Infrastructure;

/// <summary>One captured log entry: the raw structured state (before redaction) and the production JSON line.</summary>
internal sealed record CapturedLog(
    string Category, LogLevel Level, int EventId, string Message, IReadOnlyDictionary<string, object?> State, string JsonLine);

/// <summary>
/// Test logger provider (#55): records every entry with its scopes and renders it through the production
/// <see cref="RedactingJsonConsoleFormatter"/>, so tests can assert both what the code logs and what the log stream
/// would contain.
/// </summary>
internal sealed class CapturingLoggerProvider : ILoggerProvider, ISupportExternalScope
{
    private IExternalScopeProvider _scopes = new LoggerExternalScopeProvider();

    public ConcurrentQueue<CapturedLog> Entries { get; } = new();

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(this, categoryName);

    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;

    public void Dispose()
    {
    }

    private sealed class CapturingLogger(CapturingLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => owner._scopes.Push(state);

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            var values = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (state is IEnumerable<KeyValuePair<string, object?>> properties)
            {
                foreach (var (key, value) in properties)
                {
                    values[key] = value;
                }
            }

            owner._scopes.ForEachScope(
                static (scope, target) =>
                {
                    if (scope is IEnumerable<KeyValuePair<string, object?>> scopeValues)
                    {
                        foreach (var (key, value) in scopeValues)
                        {
                            target.TryAdd(key, value);
                        }
                    }
                },
                values);

            var line = RedactingJsonConsoleFormatter.Format(logLevel, category, eventId, message, exception, state, owner._scopes, DateTimeOffset.UtcNow);
            owner.Entries.Enqueue(new CapturedLog(category, logLevel, eventId.Id, message, values, line));
        }
    }
}
