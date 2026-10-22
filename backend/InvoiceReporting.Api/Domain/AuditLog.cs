namespace InvoiceReporting.Api.Domain;

public sealed class AuditLog
{
    public long Id { get; set; }

    /// <summary>The tenant whose data was accessed.</summary>
    public int TenantId { get; set; }

    public Tenant Tenant { get; set; } = null!;

    public required string Action { get; set; }

    /// <summary>Short, non-sensitive context for the action (for example the reporting month).</summary>
    public string? Detail { get; set; }

    /// <summary>The authenticated caller's subject identifier, when available.</summary>
    public string? ActorSubject { get; set; }

    public DateTimeOffset OccurredAtUtc { get; set; }
}
