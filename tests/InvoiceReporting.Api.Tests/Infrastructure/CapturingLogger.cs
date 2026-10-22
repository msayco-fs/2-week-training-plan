using Microsoft.Extensions.Logging;

namespace InvoiceReporting.Api.Tests.Infrastructure;

/// <summary>Records log calls so tests can assert on structured log fields.</summary>
public sealed class CapturingLogger<T> : ILogger<T>
{
    public List<LogEntry> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Entries.Add(new LogEntry(
            logLevel,
            eventId,
            (state as IEnumerable<KeyValuePair<string, object?>>)?.ToDictionary(p => p.Key, p => p.Value) ?? []));

    public sealed record LogEntry(LogLevel Level, EventId EventId, IReadOnlyDictionary<string, object?> State);
}
