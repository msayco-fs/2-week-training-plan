using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace InvoiceReporting.Api.Security;

/// <summary>
/// Counts tenant-access denials per subject in a fixed window that starts at the
/// subject's first denial. Reaching the threshold logs one
/// <c>TenantAccessProbeSuspected</c> error per window, which monitoring can
/// route to an alert.
/// </summary>
public sealed partial class TenantDenialMonitor(
    IMemoryCache cache,
    IOptions<TenantDenialAlertOptions> options,
    ILogger<TenantDenialMonitor> logger)
{
    public void RecordDenial(string? subject)
    {
        // Unauthenticated callers have no subject; the rate limiter covers them.
        if (string.IsNullOrEmpty(subject))
        {
            return;
        }

        var settings = options.Value;
        var counter = cache.GetOrCreate($"tenant-denials:{subject}", entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = settings.Window;
            return new DenialCounter();
        })!;

        // Equality, not >=, so the alert fires once per window, not on every
        // later denial.
        if (Interlocked.Increment(ref counter.Count) == settings.Threshold)
        {
            LogProbeSuspected(logger, subject, settings.Threshold, settings.Window);
        }
    }

    [LoggerMessage(EventId = 4031, EventName = "TenantAccessProbeSuspected", Level = LogLevel.Error,
        Message = "Subject {Subject} was denied tenant access {DenialCount} times within {Window}; possible cross-tenant probing")]
    private static partial void LogProbeSuspected(ILogger logger, string subject, int denialCount, TimeSpan window);

    private sealed class DenialCounter
    {
        public int Count;
    }
}
