namespace InvoiceReporting.Api.Security;

public static class TenantClaimTypes
{
    /// <summary>
    /// Claim that identifies the authenticated caller's tenant. Its value is a
    /// tenant key (for example <c>dealer-alpha</c>) and must be issued by the
    /// trusted identity provider, never taken from request input.
    /// </summary>
    public const string TenantId = "tenant_id";
}
