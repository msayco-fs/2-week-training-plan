namespace InvoiceReporting.Api.Tenancy;

/// <summary>
/// Answers "which tenant is this request asking about?".
/// Resolution is NOT authorization: a resolved tenant says nothing about
/// whether the caller is allowed to see that tenant's data.
/// </summary>
public interface ITenantResolver
{
    Task<TenantResolutionResult> ResolveAsync(string? requestedTenantKey, CancellationToken cancellationToken);
}
