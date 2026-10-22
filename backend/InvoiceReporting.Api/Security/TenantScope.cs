namespace InvoiceReporting.Api.Security;

/// <summary>
/// The resource being authorized: the validated, normalized tenant key that a
/// request wants to operate on, as produced by tenant resolution.
/// </summary>
public sealed record TenantScope(string TenantKey);
