namespace InvoiceReporting.Api.Tenancy;

public enum TenantResolutionStatus
{
    Resolved,
    Missing,
    Malformed,
    NotFound,
}

/// <param name="Status">Outcome of resolution.</param>
/// <param name="TenantKey">
/// The validated, normalized tenant key the request asked for. Set when the
/// status is <see cref="TenantResolutionStatus.Resolved"/> or
/// <see cref="TenantResolutionStatus.NotFound"/>, so authorization can run
/// before the caller learns whether the tenant exists.
/// </param>
/// <param name="Tenant">The tenant record. Set only when resolved.</param>
public sealed record TenantResolutionResult(TenantResolutionStatus Status, string? TenantKey, ResolvedTenant? Tenant)
{
    public static TenantResolutionResult Missing { get; } = new(TenantResolutionStatus.Missing, null, null);

    public static TenantResolutionResult Malformed { get; } = new(TenantResolutionStatus.Malformed, null, null);

    public static TenantResolutionResult NotFound(string tenantKey) => new(TenantResolutionStatus.NotFound, tenantKey, null);

    public static TenantResolutionResult Success(ResolvedTenant tenant) => new(TenantResolutionStatus.Resolved, tenant.Key, tenant);
}
