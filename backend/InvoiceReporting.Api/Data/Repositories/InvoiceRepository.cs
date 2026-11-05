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
        // One parameterized query, filtered by tenant and date range in the
        // database and projected straight to the response shape.
        await db.Invoices
            .AsNoTracking()
            .Where(i => i.TenantId == tenantId
                && i.IssuedDate >= fromInclusive
                && i.IssuedDate < toExclusive)
            .OrderBy(i => i.IssuedDate)
            .ThenBy(i => i.Id)
            .Select(i => new InvoiceSummary(i.Id, i.CustomerName, i.Amount, i.IssuedDate))
            .ToListAsync(cancellationToken);
}
