using System.Globalization;
using Hangfire;
using InvoiceReporting.Api.Data.Repositories;
using InvoiceReporting.Api.Domain;
using Microsoft.Extensions.Options;

namespace InvoiceReporting.Api.Reporting;

/// <summary>
/// Emails a tenant's quarterly CSV report to each recipient. Queued by
/// <see cref="QuarterlyReportJob"/> once the CSV exists. Each recipient gets a
/// separate Hangfire job, so one bad address does not block the others.
/// </summary>
/// <remarks>
/// Idempotency: before sending, a job claims a
/// <see cref="QuarterlyReportEmailDelivery"/> row, which is unique per tenant,
/// user and quarter. Retries and duplicate jobs therefore cannot send twice.
/// The only remaining duplicate is a crash after the SMTP server accepted the
/// email but before the row was marked sent. That claim expires after
/// <see cref="ClaimLease"/>, and the email is sent again with the same
/// Message-ID.
/// </remarks>
public sealed class QuarterlyReportEmailJob(
    IReportRecipientRepository recipients,
    ITenantRepository tenants,
    IEmailDeliveryRepository deliveries,
    IReportEmailSender sender,
    QuarterlyReportCsvWriter csvWriter,
    IBackgroundJobClient jobs,
    IOptions<QuarterlyReportEmailOptions> options,
    TimeProvider timeProvider,
    ILogger<QuarterlyReportEmailJob> logger)
{
    /// <summary>
    /// How long a claim blocks other jobs. Much longer than one SMTP send, so
    /// a slow send is never taken over while it is still running.
    /// </summary>
    public static readonly TimeSpan ClaimLease = TimeSpan.FromMinutes(5);

    /// <summary>Queues one <see cref="SendAsync"/> job per recipient of this tenant's report.</summary>
    public async Task QueueRecipientsAsync(int tenantId, int year, int quarterNumber, CancellationToken cancellationToken)
    {
        if (!options.Value.Enabled)
        {
            logger.LogInformation("Report email is disabled; tenant {TenantId} report is not emailed", tenantId);
            return;
        }

        var userIds = await recipients.GetRecipientUserIdsAsync(tenantId, cancellationToken);

        // Only ids go into job arguments, so no email addresses are stored in Hangfire.
        foreach (var userId in userIds)
        {
            jobs.Enqueue<QuarterlyReportEmailJob>(job =>
                job.SendAsync(tenantId, userId, year, quarterNumber, CancellationToken.None));
        }

        logger.LogInformation(
            "Queued {Count} report emails for tenant {TenantId}, quarter {Year}-Q{Quarter}",
            userIds.Count, tenantId, year, quarterNumber);
    }

    // Five attempts: Hangfire's growing retry delays then outlast ClaimLease,
    // so a claim left by a crashed job is taken over before retries run out.
    [AutomaticRetry(Attempts = 5)]
    public async Task SendAsync(int tenantId, int userId, int year, int quarterNumber, CancellationToken cancellationToken)
    {
        var quarter = new ReportingQuarter(year, quarterNumber);
        var quarterText = quarter.ToString();

        // Checked before claiming: the user may have opted out or lost the
        // tenant assignment since the job was queued.
        var toAddress = await recipients.GetRecipientEmailAsync(tenantId, userId, cancellationToken);
        if (toAddress is null)
        {
            logger.LogInformation("User {UserId} no longer receives tenant {TenantId} reports; email skipped", userId, tenantId);
            return;
        }

        var path = csvWriter.GetPath(tenantId, quarter);
        if (!File.Exists(path))
        {
            // Throw so Hangfire retries; the CSV job may still be re-running.
            throw new FileNotFoundException($"Quarterly report {quarter} for tenant {tenantId} was not found.", Path.GetFileName(path));
        }

        var claim = await deliveries.TryClaimAsync(
            tenantId, userId, quarterText, timeProvider.GetUtcNow().UtcDateTime, ClaimLease, cancellationToken);
        switch (claim.Result)
        {
            case EmailDeliveryClaimResult.AlreadySent:
                logger.LogInformation("Report {Quarter} for tenant {TenantId} already emailed to user {UserId}", quarter, tenantId, userId);
                return;
            case EmailDeliveryClaimResult.InProgress:
                // Throw so Hangfire retries later: by then the other job has
                // finished, or its claim has expired and can be taken over.
                throw new InvalidOperationException(
                    $"Report {quarter} email for tenant {tenantId}, user {userId} is being sent by another job.");
        }

        try
        {
            var content = await File.ReadAllBytesAsync(path, cancellationToken);
            var tenantName = await tenants.GetDisplayNameAsync(tenantId, cancellationToken) ?? $"Tenant {tenantId}";

            await sender.SendAsync(new ReportEmail(
                IdempotencyKey: string.Create(CultureInfo.InvariantCulture, $"quarterly-report.{quarter}.t{tenantId}.u{userId}"),
                toAddress,
                Subject: $"Quarterly invoice report {quarter} – {tenantName}",
                Body: $"Attached is the quarterly invoice report for {tenantName}, {quarter}.",
                AttachmentFileName: Path.GetFileName(path),
                AttachmentContent: content), cancellationToken);
        }
        catch
        {
            // Not sent, so free the claim and let the retry try again.
            await deliveries.ReleaseAsync(claim, CancellationToken.None);
            throw;
        }

        // If this save fails, the claim stays in the Sending state. Retries see
        // InProgress until the lease expires, then send once more with the
        // same Message-ID.
        await deliveries.MarkSentAsync(claim, timeProvider.GetUtcNow().UtcDateTime, new AuditLog
        {
            TenantId = tenantId,
            Action = AuditActions.EmailQuarterlyInvoiceReport,
            Detail = string.Create(CultureInfo.InvariantCulture, $"{quarter}:user-{userId}"),
            ActorSubject = null, // System job, no caller.
            OccurredAtUtc = timeProvider.GetUtcNow(),
        }, cancellationToken);

        // The address is not logged; the user id is enough to trace it.
        logger.LogInformation("Emailed report {Quarter} for tenant {TenantId} to user {UserId}", quarter, tenantId, userId);
    }
}
