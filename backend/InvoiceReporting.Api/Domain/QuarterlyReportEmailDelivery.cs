namespace InvoiceReporting.Api.Domain;

/// <summary>
/// Idempotency record for one report email: one row per tenant, user and
/// quarter, enforced by a unique index. A job claims the row before sending
/// and marks it sent afterwards, so retries and duplicate jobs do not send twice.
/// </summary>
public sealed class QuarterlyReportEmailDelivery
{
    public long Id { get; set; }

    public int TenantId { get; set; }

    public Tenant Tenant { get; set; } = null!;

    public int UserId { get; set; }

    public User User { get; set; } = null!;

    /// <summary>The reporting quarter, for example <c>2026-Q3</c>.</summary>
    public required string Quarter { get; set; }

    public EmailDeliveryStatus Status { get; set; }

    /// <summary>
    /// Changes on every claim. Updates check it, so a slow job cannot change a
    /// row that another job has since taken over.
    /// </summary>
    public Guid ClaimToken { get; set; }

    public DateTime ClaimedAtUtc { get; set; }

    public DateTime? SentAtUtc { get; set; }
}

public enum EmailDeliveryStatus
{
    /// <summary>A job has claimed the email and may be sending it now.</summary>
    Sending,

    Sent,
}
