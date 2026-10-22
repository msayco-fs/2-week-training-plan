using InvoiceReporting.Api.Data;
using InvoiceReporting.Api.Domain;
using InvoiceReporting.Api.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace InvoiceReporting.Api.Reporting;

public sealed class InvoiceReportService(ReportingDbContext db, TimeProvider timeProvider) : IInvoiceReportService
{
    public async Task<MonthlyInvoiceReport> GetMonthlyReportAsync(
        ResolvedTenant tenant,
        ReportingMonth month,
        string? actorSubject,
        CancellationToken cancellationToken)
    {
        // One parameterized query, filtered by tenant and date range in the
        // database and projected straight to the response shape.
        var invoices = await db.Invoices
            .AsNoTracking()
            .Where(i => i.TenantId == tenant.Id
                && i.IssuedDate >= month.FirstDay
                && i.IssuedDate < month.FirstDayOfNextMonth)
            .OrderBy(i => i.IssuedDate)
            .ThenBy(i => i.Id)
            .Select(i => new InvoiceSummary(i.Id, i.CustomerName, i.Amount, i.IssuedDate))
            .ToListAsync(cancellationToken);

        // The rows are already in memory, so summing them here avoids a second
        // round trip and guarantees the total matches the returned invoices.
        var total = invoices.Sum(i => i.Amount);

        db.AuditLogs.Add(new AuditLog
        {
            TenantId = tenant.Id,
            Action = AuditActions.ViewMonthlyInvoices,
            Detail = month.ToString(),
            ActorSubject = actorSubject,
            OccurredAtUtc = timeProvider.GetUtcNow(),
        });
        await db.SaveChangesAsync(cancellationToken);

        return new MonthlyInvoiceReport(month.ToString(), invoices, total);
    }
}
