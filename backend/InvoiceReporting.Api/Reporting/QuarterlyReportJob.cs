using System.Text.Json.Serialization;
using Hangfire;
using InvoiceReporting.Api.Data.Repositories;
using InvoiceReporting.Api.Domain;

namespace InvoiceReporting.Api.Reporting;

/// <summary>
/// Builds the quarterly invoice report for one tenant, saves it as a CSV file
/// (see <see cref="QuarterlyReportCsvWriter"/>), records an audit entry and
/// queues the report emails (see <see cref="QuarterlyReportEmailJob"/>).
/// Queued by <see cref="QuarterlyReportScheduler"/>, or run directly by
/// <see cref="Controllers.QuarterlyReportAdminController"/>.
/// </summary>
/// <remarks>
/// Cancellation: the token is passed to every database query, the file write
/// and checked between stages. A cancelled run throws
/// <see cref="OperationCanceledException"/> and never returns a result, so it
/// is never reported as done. Under Hangfire the token is the server shutdown
/// token; over HTTP it is the request-aborted token.
/// </remarks>
public sealed class QuarterlyReportJob(
    IInvoiceRepository invoiceRepository,
    IAuditLogRepository auditLogs,
    TimeProvider timeProvider,
    QuarterlyReportCsvWriter csvWriter,
    IBackgroundJobClient jobs,
    ILogger<QuarterlyReportJob> logger)
{
    [AutomaticRetry(Attempts = 3)]
    public async Task<QuarterlyReportRunResult> RunAsync(int tenantId, int year, int quarterNumber, CancellationToken cancellationToken)
    {
        var quarter = new ReportingQuarter(year, quarterNumber);
        var detail = quarter.ToString();

        // Retries and manual re-runs must not report the same quarter twice.
        var alreadyGenerated = await auditLogs.ExistsAsync(
            tenantId, AuditActions.GenerateQuarterlyInvoiceReport, detail, cancellationToken);
        if (alreadyGenerated)
        {
            logger.LogInformation("Quarterly report {Quarter} for tenant {TenantId} already generated", detail, tenantId);

            // Queued again in case the earlier run saved the audit entry but
            // failed before queuing the emails. Safe: each recipient's email
            // job skips people who already received the report.
            cancellationToken.ThrowIfCancellationRequested();
            QueueEmails(tenantId, year, quarterNumber);
            return QuarterlyReportRunResult.AlreadyGenerated;
        }

        var invoices = await invoiceRepository.GetSummariesAsync(
            tenantId, quarter.FirstDay, quarter.FirstDayOfNextQuarter, cancellationToken);

        // Summed in memory, like the monthly report: SQLite (used in tests)
        // cannot aggregate decimal columns.
        var count = invoices.Count;
        var total = invoices.Sum(i => i.Amount);

        // Written before the audit entry: if the audit save fails, the retry
        // writes the file again instead of skipping it.
        cancellationToken.ThrowIfCancellationRequested();
        var path = await csvWriter.WriteAsync(tenantId, quarter, invoices, total, cancellationToken);

        await auditLogs.AddAsync(new AuditLog
        {
            TenantId = tenantId,
            Action = AuditActions.GenerateQuarterlyInvoiceReport,
            Detail = detail,
            ActorSubject = null, // System job, no caller.
            OccurredAtUtc = timeProvider.GetUtcNow(),
        }, cancellationToken);

        logger.LogInformation(
            "Generated quarterly report {Quarter} for tenant {TenantId}: {Count} invoices, total {Total}, saved to {Path}",
            detail, tenantId, count, total, path);

        // Cancelled after the audit save: the next run takes the
        // already-generated branch above and queues the emails then.
        cancellationToken.ThrowIfCancellationRequested();
        QueueEmails(tenantId, year, quarterNumber);
        return QuarterlyReportRunResult.Generated;
    }

    // CancellationToken.None is a placeholder: Hangfire replaces it with the
    // server's shutdown token when the email job runs.
    private void QueueEmails(int tenantId, int year, int quarterNumber) =>
        jobs.Enqueue<QuarterlyReportEmailJob>(job =>
            job.QueueRecipientsAsync(tenantId, year, quarterNumber, CancellationToken.None));
}

/// <summary>What <see cref="QuarterlyReportJob.RunAsync"/> did.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<QuarterlyReportRunResult>))]
public enum QuarterlyReportRunResult
{
    /// <summary>The CSV was written, audited and the emails queued.</summary>
    Generated,

    /// <summary>The quarter was already reported. Only the emails were queued again.</summary>
    AlreadyGenerated,
}
