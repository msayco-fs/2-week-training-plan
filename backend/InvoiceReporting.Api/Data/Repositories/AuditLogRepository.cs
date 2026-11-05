using InvoiceReporting.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace InvoiceReporting.Api.Data.Repositories;

public sealed class AuditLogRepository(ReportingDbContext db) : IAuditLogRepository
{
    public Task<bool> ExistsAsync(int tenantId, string action, string detail, CancellationToken cancellationToken) =>
        db.AuditLogs.AnyAsync(
            a => a.TenantId == tenantId && a.Action == action && a.Detail == detail,
            cancellationToken);

    public async Task AddAsync(AuditLog entry, CancellationToken cancellationToken)
    {
        db.AuditLogs.Add(entry);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<bool> TryAddUniqueAsync(AuditLog entry, CancellationToken cancellationToken)
    {
        db.AuditLogs.Add(entry);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException)
        {
            // Most likely the unique index. Detach so this context can still
            // save, then check; anything else is rethrown.
            db.Entry(entry).State = EntityState.Detached;
            if (await ExistsAsync(entry.TenantId, entry.Action, entry.Detail ?? "", cancellationToken))
            {
                return false;
            }

            throw;
        }
    }
}
