namespace InvoiceReporting.Api.Security;

public static class AuthorizationPolicies
{
    /// <summary>
    /// Resource-based policy evaluated against a <see cref="TenantScope"/>.
    /// See <see cref="TenantAccessHandler"/> for the rules.
    /// </summary>
    public const string TenantAccess = "TenantAccess";
}
