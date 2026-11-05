using System.Runtime.CompilerServices;
using InvoiceReporting.Api.Reporting;
using Microsoft.EntityFrameworkCore;

namespace InvoiceReporting.Api.Data.Repositories;

public sealed class InvoiceRepository(ReportingDbContext db) : IInvoiceRepository
{
    public async Task<IReadOnlyList<InvoiceSummary>> GetSummariesAsync(
        int tenantId,
        DateOnly fromInclusive,
        DateOnly toExclusive,
        CancellationToken cancellationToken) =>
        await Summaries(tenantId, fromInclusive, toExclusive).ToListAsync(cancellationToken);

    public async IAsyncEnumerable<InvoiceSummary> StreamSummariesAsync(
        int tenantId,
        DateOnly fromInclusive,
        DateOnly toExclusive,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var invoice in Summaries(tenantId, fromInclusive, toExclusive)
            .AsAsyncEnumerable()
            .WithCancellation(cancellationToken))
        {
            yield return invoice;
        }
    }

    // One parameterized query, filtered by tenant and date range in the
    // database and projected straight to the response shape.
    private IQueryable<InvoiceSummary> Summaries(int tenantId, DateOnly fromInclusive, DateOnly toExclusive) =>
        db.Invoices
            .AsNoTracking()
            .Where(i => i.TenantId == tenantId
                && i.IssuedDate >= fromInclusive
                && i.IssuedDate < toExclusive)
            .OrderBy(i => i.IssuedDate)
            .ThenBy(i => i.Id)
            .Select(i => new InvoiceSummary(i.Id, i.CustomerName, i.Amount, i.IssuedDate));
}
