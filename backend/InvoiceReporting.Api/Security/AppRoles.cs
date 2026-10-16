namespace InvoiceReporting.Api.Security;

public static class AppRoles
{
    /// <summary>
    /// Platform administrator. In Phase 2 this role, read from the authenticated
    /// principal's role claims, is the only permitted bypass of the tenant-match
    /// requirement.
    /// </summary>
    public const string PlatformAdmin = "PlatformAdmin";
}
