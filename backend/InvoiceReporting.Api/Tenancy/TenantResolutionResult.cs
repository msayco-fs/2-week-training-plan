namespace InvoiceReporting.Api.Tenancy;

public enum TenantResolutionStatus
{
    Resolved,
    Missing,
    Malformed,
    NotFound,
}

public sealed record TenantResolutionResult(TenantResolutionStatus Status, ResolvedTenant? Tenant)
{
    public static TenantResolutionResult Missing { get; } = new(TenantResolutionStatus.Missing, null);

    public static TenantResolutionResult Malformed { get; } = new(TenantResolutionStatus.Malformed, null);

    public static TenantResolutionResult NotFound { get; } = new(TenantResolutionStatus.NotFound, null);

    public static TenantResolutionResult Success(ResolvedTenant tenant) => new(TenantResolutionStatus.Resolved, tenant);
}
