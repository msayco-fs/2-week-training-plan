namespace InvoiceReporting.Api.Security;

public static class AppRoles
{
    /// <summary>
    /// Platform administrator. This role, read from the authenticated principal's
    /// role claims, is the only permitted bypass of the tenant-match requirement
    /// in <see cref="TenantAccessHandler"/>.
    /// </summary>
    public const string PlatformAdmin = "PlatformAdmin";
}
