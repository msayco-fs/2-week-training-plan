using InvoiceReporting.Api.Tenancy;

namespace InvoiceReporting.Api.Reporting;

public interface IInvoiceReportService
{
    /// <summary>
    /// Builds the monthly invoice report for <paramref name="tenant"/> and records
    /// an audit entry. Callers must authorize access to the tenant first.
    /// </summary>
    Task<MonthlyInvoiceReport> GetMonthlyReportAsync(
        ResolvedTenant tenant,
        ReportingMonth month,
        string? actorSubject,
        CancellationToken cancellationToken);
}
