using InvoiceReporting.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace InvoiceReporting.Api.Data.Repositories;

public sealed class EmailDeliveryRepository(ReportingDbContext db) : IEmailDeliveryRepository
{
    public async Task<EmailDeliveryClaim> TryClaimAsync(
        int tenantId, int userId, string quarter, DateTime nowUtc, TimeSpan lease, CancellationToken cancellationToken)
    {
        var token = Guid.NewGuid();
        var delivery = new QuarterlyReportEmailDelivery
        {
            TenantId = tenantId,
            UserId = userId,
            Quarter = quarter,
            Status = EmailDeliveryStatus.Sending,
            ClaimToken = token,
            ClaimedAtUtc = nowUtc,
        };

        db.QuarterlyReportEmailDeliveries.Add(delivery);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return new EmailDeliveryClaim(EmailDeliveryClaimResult.Claimed, delivery.Id, token);
        }
        catch (DbUpdateException)
        {
            // Most likely the unique index: another job already has a row.
            // Checked below; anything else is rethrown.
            db.Entry(delivery).State = EntityState.Detached;
        }

        var existing = await db.QuarterlyReportEmailDeliveries
            .AsNoTracking()
            .SingleOrDefaultAsync(
                d => d.TenantId == tenantId && d.UserId == userId && d.Quarter == quarter,
                cancellationToken)
            ?? throw new InvalidOperationException("Could not record the report email delivery.");

        if (existing.Status == EmailDeliveryStatus.Sent)
        {
            return new EmailDeliveryClaim(EmailDeliveryClaimResult.AlreadySent);
        }

        if (existing.ClaimedAtUtc > nowUtc - lease)
        {
            return new EmailDeliveryClaim(EmailDeliveryClaimResult.InProgress);
        }

        // Stale claim. Take it over only if no other job did so first.
        var updated = await db.QuarterlyReportEmailDeliveries
            .Where(d => d.Id == existing.Id
                && d.ClaimToken == existing.ClaimToken
                && d.Status == EmailDeliveryStatus.Sending)
            .ExecuteUpdateAsync(s => s
                .SetProperty(d => d.ClaimToken, token)
                .SetProperty(d => d.ClaimedAtUtc, nowUtc), cancellationToken);

        return updated == 1
            ? new EmailDeliveryClaim(EmailDeliveryClaimResult.Claimed, existing.Id, token, TookOverExpiredClaim: true)
            : new EmailDeliveryClaim(EmailDeliveryClaimResult.InProgress);
    }

    public async Task MarkSentAsync(EmailDeliveryClaim claim, DateTime nowUtc, AuditLog audit, CancellationToken cancellationToken)
    {
        var delivery = await db.QuarterlyReportEmailDeliveries
            .SingleAsync(d => d.Id == claim.DeliveryId, cancellationToken);

        // Even if another job took the claim over meanwhile, this email was
        // sent, so recording it as sent is still correct.
        delivery.Status = EmailDeliveryStatus.Sent;
        delivery.SentAtUtc = nowUtc;
        db.AuditLogs.Add(audit);

        // One SaveChanges is one transaction: both rows are saved, or neither.
        await db.SaveChangesAsync(cancellationToken);
    }

    public Task ReleaseAsync(EmailDeliveryClaim claim, CancellationToken cancellationToken) =>
        db.QuarterlyReportEmailDeliveries
            .Where(d => d.Id == claim.DeliveryId
                && d.ClaimToken == claim.ClaimToken
                && d.Status == EmailDeliveryStatus.Sending)
            .ExecuteDeleteAsync(cancellationToken);
}
