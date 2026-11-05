using InvoiceReporting.Api.Reporting;

namespace InvoiceReporting.Api.Data.Repositories;

public interface IInvoiceRepository
{
    /// <summary>
    /// Invoices for one tenant issued in [fromInclusive, toExclusive),
    /// ordered by issue date then id.
    /// </summary>
    Task<IReadOnlyList<InvoiceSummary>> GetSummariesAsync(
        int tenantId,
        DateOnly fromInclusive,
        DateOnly toExclusive,
        CancellationToken cancellationToken);
}
