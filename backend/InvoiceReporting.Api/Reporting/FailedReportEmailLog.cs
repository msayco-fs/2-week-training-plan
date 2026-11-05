namespace InvoiceReporting.Api.Reporting;

/// <summary>
/// Writes one line per report email that failed for good, for support to read.
/// Uses its own log category, <see cref="Category"/>, which is routed to a
/// daily log file in the logs folder. This is a second record next to the
/// audit log, and still works when the database is down.
/// </summary>
/// <remarks>
/// Each line holds only ids, the quarter and a reason code from
/// <see cref="ReportEmailFailureReasons"/>. Never add the email address, the
/// subject, the body, an API key or report content.
/// </remarks>
public sealed partial class FailedReportEmailLog(ILoggerFactory loggerFactory)
{
    /// <summary>The log category routed to the failed-email log file.</summary>
    public const string Category = "InvoiceReporting.FailedReportEmails";

    private readonly ILogger _logger = loggerFactory.CreateLogger(Category);

    /// <param name="jobId">The Hangfire job id, so support can find the job in the dashboard. Null when unknown.</param>
    public void Record(int tenantId, int userId, ReportingQuarter quarter, string reason, string? jobId) =>
        LogFailed(_logger, tenantId, userId, quarter.ToString(), reason, jobId ?? "-");

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Warning,
        Message = "FAILED tenant={TenantId} user={UserId} quarter={Quarter} reason={Reason} job={JobId}")]
    private static partial void LogFailed(ILogger logger, int tenantId, int userId, string quarter, string reason, string jobId);
}
