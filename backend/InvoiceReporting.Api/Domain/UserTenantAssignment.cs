namespace InvoiceReporting.Api.Domain;

/// <summary>
/// Links a platform administrator to a tenant whose quarterly reports they
/// receive. Does not change API access: administrators can still call the API
/// for any tenant.
/// </summary>
public sealed class UserTenantAssignment
{
    public int UserId { get; set; }

    public User User { get; set; } = null!;

    public int TenantId { get; set; }

    public Tenant Tenant { get; set; } = null!;
}
