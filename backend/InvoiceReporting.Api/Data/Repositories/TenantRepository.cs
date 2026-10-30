using InvoiceReporting.Api.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace InvoiceReporting.Api.Data.Repositories;

public sealed class TenantRepository(ReportingDbContext db) : ITenantRepository
{
    public Task<ResolvedTenant?> FindByKeyAsync(string normalizedKey, CancellationToken cancellationToken) =>
        db.Tenants
            .AsNoTracking()
            .Where(t => t.Key == normalizedKey)
            .Select(t => new ResolvedTenant(t.Id, t.Key))
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<int>> GetAllIdsAsync(CancellationToken cancellationToken) =>
        await db.Tenants
            .AsNoTracking()
            .Select(t => t.Id)
            .ToListAsync(cancellationToken);
}
