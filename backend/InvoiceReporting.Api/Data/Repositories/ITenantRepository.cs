using InvoiceReporting.Api.Tenancy;

namespace InvoiceReporting.Api.Data.Repositories;

public interface ITenantRepository
{
    /// <summary>Finds a tenant by its normalized (lower-case) key.</summary>
    Task<ResolvedTenant?> FindByKeyAsync(string normalizedKey, CancellationToken cancellationToken);

    Task<IReadOnlyList<int>> GetAllIdsAsync(CancellationToken cancellationToken);
}
