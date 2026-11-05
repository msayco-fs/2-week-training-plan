using InvoiceReporting.Api.Data.Repositories;
using InvoiceReporting.Api.Domain;
using InvoiceReporting.Api.Tenancy;

namespace InvoiceReporting.Api.Reporting;

public sealed class InvoiceReportService(
    IInvoiceRepository invoiceRepository,
    IAuditLogRepository auditLogs,
    TimeProvider timeProvider) : IInvoiceReportService
{
    public async Task<MonthlyInvoiceReport> GetMonthlyReportAsync(
        ResolvedTenant tenant,
        ReportingMonth month,
        string? actorSubject,
        CancellationToken cancellationToken)
    {
        var invoices = await invoiceRepository.GetSummariesAsync(
            tenant.Id, month.FirstDay, month.FirstDayOfNextMonth, cancellationToken);

        // The rows are already in memory, so summing them here avoids a second
        // round trip and guarantees the total matches the returned invoices.
        var total = invoices.Sum(i => i.Amount);

        await auditLogs.AddAsync(new AuditLog
        {
            TenantId = tenant.Id,
            Action = AuditActions.ViewMonthlyInvoices,
            Detail = month.ToString(),
            ActorSubject = actorSubject,
            OccurredAtUtc = timeProvider.GetUtcNow(),
        }, cancellationToken);

        return new MonthlyInvoiceReport(month.ToString(), invoices, total);
    }
}
