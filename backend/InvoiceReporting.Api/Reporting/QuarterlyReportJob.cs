using Hangfire;
using InvoiceReporting.Api.Data.Repositories;
using InvoiceReporting.Api.Domain;

namespace InvoiceReporting.Api.Reporting;

/// <summary>
/// Builds the quarterly invoice report for one tenant, saves it as a CSV file
/// (see <see cref="QuarterlyReportCsvWriter"/>) and records an audit entry.
/// Queued by <see cref="QuarterlyReportScheduler"/>.
/// </summary>
public sealed class QuarterlyReportJob(
    IInvoiceRepository invoiceRepository,
    IAuditLogRepository auditLogs,
    TimeProvider timeProvider,
    QuarterlyReportCsvWriter csvWriter,
    ILogger<QuarterlyReportJob> logger)
{
    [AutomaticRetry(Attempts = 3)]
    public async Task RunAsync(int tenantId, int year, int quarterNumber, CancellationToken cancellationToken)
    {
        var quarter = new ReportingQuarter(year, quarterNumber);
        var detail = quarter.ToString();

        // Retries and manual re-runs must not report the same quarter twice.
        var alreadyGenerated = await auditLogs.ExistsAsync(
            tenantId, AuditActions.GenerateQuarterlyInvoiceReport, detail, cancellationToken);
        if (alreadyGenerated)
        {
            logger.LogInformation("Quarterly report {Quarter} for tenant {TenantId} already generated", detail, tenantId);
            return;
        }

        var invoices = await invoiceRepository.GetSummariesAsync(
            tenantId, quarter.FirstDay, quarter.FirstDayOfNextQuarter, cancellationToken);

        // Summed in memory, like the monthly report: SQLite (used in tests)
        // cannot aggregate decimal columns.
        var count = invoices.Count;
        var total = invoices.Sum(i => i.Amount);

        // Written before the audit entry: if the audit save fails, the retry
        // writes the file again instead of skipping it.
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
    }
}
