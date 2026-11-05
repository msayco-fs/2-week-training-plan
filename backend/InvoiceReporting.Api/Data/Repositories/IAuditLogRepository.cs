using InvoiceReporting.Api.Domain;

namespace InvoiceReporting.Api.Data.Repositories;

public interface IAuditLogRepository
{
    Task<bool> ExistsAsync(int tenantId, string action, string detail, CancellationToken cancellationToken);

    /// <summary>Adds the entry and saves it immediately.</summary>
    Task AddAsync(AuditLog entry, CancellationToken cancellationToken);

    /// <summary>
    /// Adds an entry that a unique index allows only once (see
    /// <c>IX_AuditLogs_QuarterlyReportGenerated</c>). Returns false, without
    /// throwing, when an equal entry already exists, for example because
    /// another run saved it first. Other save errors are thrown.
    /// </summary>
    Task<bool> TryAddUniqueAsync(AuditLog entry, CancellationToken cancellationToken);
}
