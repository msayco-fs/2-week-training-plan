using InvoiceReporting.Api.Domain;

namespace InvoiceReporting.Api.Data.Repositories;

public interface IEmailDeliveryRepository
{
    /// <summary>
    /// Claims the right to send one report email. Succeeds when no job has
    /// claimed it yet, or when an earlier claim is older than
    /// <paramref name="lease"/> (its job most likely crashed).
    /// </summary>
    Task<EmailDeliveryClaim> TryClaimAsync(
        int tenantId, int userId, string quarter, DateTime nowUtc, TimeSpan lease, CancellationToken cancellationToken);

    /// <summary>Marks the email sent and saves <paramref name="audit"/> in the same transaction.</summary>
    Task MarkSentAsync(EmailDeliveryClaim claim, DateTime nowUtc, AuditLog audit, CancellationToken cancellationToken);

    /// <summary>Removes a claim after a failed send, so a retry can claim it again.</summary>
    Task ReleaseAsync(EmailDeliveryClaim claim, CancellationToken cancellationToken);
}

public enum EmailDeliveryClaimResult
{
    /// <summary>This job owns the claim and must send the email.</summary>
    Claimed,

    /// <summary>The email was already sent. Do nothing.</summary>
    AlreadySent,

    /// <summary>Another job claimed it recently and may still be sending.</summary>
    InProgress,
}

/// <param name="TookOverExpiredClaim">
/// True when this claim replaced an earlier job's expired claim. That job may
/// have sent the email before it stopped, so the caller should ask the
/// provider before sending again.
/// </param>
public sealed record EmailDeliveryClaim(
    EmailDeliveryClaimResult Result,
    long DeliveryId = 0,
    Guid ClaimToken = default,
    bool TookOverExpiredClaim = false);
