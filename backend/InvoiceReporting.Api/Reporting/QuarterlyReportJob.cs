using Hangfire;
using InvoiceReporting.Api.Data;
using InvoiceReporting.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace InvoiceReporting.Api.Reporting;

/// <summary>
/// Builds the quarterly invoice report for one tenant, saves it as a CSV file
/// (see <see cref="QuarterlyReportCsvWriter"/>) and records an audit entry.
/// Queued by <see cref="QuarterlyReportScheduler"/>.
/// </summary>
public sealed class QuarterlyReportJob(
    ReportingDbContext db,
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
        var alreadyGenerated = await db.AuditLogs.AnyAsync(
            a => a.TenantId == tenantId
                && a.Action == AuditActions.GenerateQuarterlyInvoiceReport
                && a.Detail == detail,
            cancellationToken);
        if (alreadyGenerated)
        {
            logger.LogInformation("Quarterly report {Quarter} for tenant {TenantId} already generated", detail, tenantId);
            return;
        }

        var invoices = await db.Invoices
            .AsNoTracking()
            .Where(i => i.TenantId == tenantId
                && i.IssuedDate >= quarter.FirstDay
                && i.IssuedDate < quarter.FirstDayOfNextQuarter)
            .OrderBy(i => i.IssuedDate)
            .ThenBy(i => i.Id)
            .Select(i => new InvoiceSummary(i.Id, i.CustomerName, i.Amount, i.IssuedDate))
            .ToListAsync(cancellationToken);

        // Summed in memory, like the monthly report: SQLite (used in tests)
        // cannot aggregate decimal columns.
        var count = invoices.Count;
        var total = invoices.Sum(i => i.Amount);

        // Written before the audit entry: if the audit save fails, the retry
        // writes the file again instead of skipping it.
        var path = await csvWriter.WriteAsync(tenantId, quarter, invoices, total, cancellationToken);

        db.AuditLogs.Add(new AuditLog
        {
            TenantId = tenantId,
            Action = AuditActions.GenerateQuarterlyInvoiceReport,
            Detail = detail,
            ActorSubject = null, // System job, no caller.
            OccurredAtUtc = timeProvider.GetUtcNow(),
        });
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Generated quarterly report {Quarter} for tenant {TenantId}: {Count} invoices, total {Total}, saved to {Path}",
            detail, tenantId, count, total, path);
    }
}
