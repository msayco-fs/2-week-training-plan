namespace InvoiceReporting.Api.Domain;

/// <summary>
/// A person who can receive reports. Sign-in still uses JWT claims; this row
/// only stores what background jobs need and cannot read from a token, such
/// as the email address and tenant membership.
/// </summary>
public sealed class User
{
    public int Id { get; set; }

    /// <summary>The identity provider's subject identifier (the JWT <c>sub</c> claim).</summary>
    public required string Subject { get; set; }

    public required string Email { get; set; }

    public bool IsPlatformAdmin { get; set; }

    /// <summary>
    /// The one tenant a standard user belongs to. Null for platform
    /// administrators, whose tenants are listed in <see cref="TenantAssignments"/>.
    /// </summary>
    public int? TenantId { get; set; }

    public Tenant? Tenant { get; set; }

    /// <summary>Tenants assigned to a platform administrator. Ignored for standard users.</summary>
    public ICollection<UserTenantAssignment> TenantAssignments { get; } = new List<UserTenantAssignment>();

    public QuarterlyReportRecipient? QuarterlyReportRecipient { get; set; }
}
