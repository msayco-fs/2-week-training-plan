using InvoiceReporting.Api.Tenancy;

namespace InvoiceReporting.Api.Data.Repositories;

public interface ITenantRepository
{
    /// <summary>Finds a tenant by its normalized (lower-case) key.</summary>
    Task<ResolvedTenant?> FindByKeyAsync(string normalizedKey, CancellationToken cancellationToken);

    Task<IReadOnlyList<int>> GetAllIdsAsync(CancellationToken cancellationToken);

    /// <summary>The tenant's display name, or null when no tenant has this id.</summary>
    Task<string?> GetDisplayNameAsync(int tenantId, CancellationToken cancellationToken);
}
