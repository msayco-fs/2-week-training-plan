using Microsoft.Extensions.Logging;

namespace InvoiceReporting.Api.Tests.Infrastructure;

/// <summary>
/// Hands out one <see cref="CapturingLogger{T}"/> for every category, so tests
/// can check what a class that takes an <see cref="ILoggerFactory"/> logged.
/// </summary>
public sealed class CapturingLoggerFactory<T> : ILoggerFactory
{
    public CapturingLogger<T> Logger { get; } = new();

    /// <summary>The categories that were asked for, in order.</summary>
    public List<string> Categories { get; } = [];

    public ILogger CreateLogger(string categoryName)
    {
        Categories.Add(categoryName);
        return Logger;
    }

    public void AddProvider(ILoggerProvider provider)
    {
    }

    public void Dispose()
    {
    }
}
