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

    /// <summary>
    /// The same invoices as <see cref="GetSummariesAsync"/>, in the same order,
    /// read one row at a time instead of loaded into a list. For large results,
    /// such as a whole quarter. The query stays open until the caller has read
    /// every row or stops early.
    /// </summary>
    IAsyncEnumerable<InvoiceSummary> StreamSummariesAsync(
        int tenantId,
        DateOnly fromInclusive,
        DateOnly toExclusive,
        CancellationToken cancellationToken);
}
