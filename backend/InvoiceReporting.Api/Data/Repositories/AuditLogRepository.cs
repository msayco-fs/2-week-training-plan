using InvoiceReporting.Api.Domain;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace InvoiceReporting.Api.Data.Repositories;

public sealed class AuditLogRepository(ReportingDbContext db, ILogger<AuditLogRepository> logger) : IAuditLogRepository
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
        catch (DbUpdateException ex)
        {
            // Detach first, whatever the cause, so this context can still be used.
            db.Entry(entry).State = EntityState.Detached;

            // Only the "one generated entry per quarter" index means another run
            // got there first. A timeout, deadlock or foreign key failure is rethrown as is.
            if (!IsQuarterlyReportGeneratedViolation(ex))
            {
                throw;
            }

            logger.LogDebug(ex,
                "Audit entry {Action} {Detail} for tenant {TenantId} hit the unique index; checking for the other run's entry",
                entry.Action, entry.Detail, entry.TenantId);

            // Second guard: the row the index protects must really be there.
            if (await ExistsAsync(entry.TenantId, entry.Action, entry.Detail ?? "", cancellationToken))
            {
                return false;
            }

            throw;
        }
    }

    // 2601: duplicate key row in a unique index. 2627: unique constraint. Both
    // messages quote the index name, so another unique index never matches.
    private static bool IsQuarterlyReportGeneratedViolation(DbUpdateException ex) =>
        ex.InnerException is SqlException { Number: 2601 or 2627 } sql
        && sql.Message.Contains($"'{ReportingDbContext.QuarterlyReportGeneratedIndexName}'", StringComparison.Ordinal);
}
