using InvoiceReporting.Api.Domain;

namespace InvoiceReporting.Api.Data.Repositories;

public interface IAuditLogRepository
{
    Task<bool> ExistsAsync(int tenantId, string action, string detail, CancellationToken cancellationToken);

    /// <summary>Adds the entry and saves it immediately.</summary>
    Task AddAsync(AuditLog entry, CancellationToken cancellationToken);
}
