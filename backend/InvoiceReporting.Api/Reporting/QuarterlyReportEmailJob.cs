using System.Globalization;
using Hangfire;
using Hangfire.Server;
using InvoiceReporting.Api.Data.Repositories;
using InvoiceReporting.Api.Domain;
using Microsoft.Extensions.Options;

namespace InvoiceReporting.Api.Reporting;

/// <summary>
/// Emails a tenant's quarterly CSV report, zipped (see <see cref="QuarterlyReportZip"/>),
/// to each recipient. Queued by
/// <see cref="QuarterlyReportJob"/> once the CSV exists. Each recipient gets a
/// separate Hangfire job, so one bad address does not block the others.
/// </summary>
/// <remarks>
/// Idempotency: before sending, a job claims a
/// <see cref="QuarterlyReportEmailDelivery"/> row, which is unique per tenant,
/// user and quarter. Retries and duplicate jobs therefore cannot send twice.
///
/// The email may already be out while its claim is still in the Sending state:
/// the job crashed after the provider accepted it, or the provider did not
/// answer (<see cref="ReportEmailOutcomeUnknownException"/>, where the claim is
/// kept on purpose). Once that claim expires after <see cref="ClaimLease"/>,
/// the job that takes it over first asks the provider whether the email was
/// sent (<see cref="IReportEmailSender.FindPriorSendAsync"/>) and only sends if
/// not. SMTP cannot answer, so it sends again with the same Message-ID.
///
/// Failures that a retry cannot fix (a zipped report over the size limit, or an email the
/// provider rejects) are not retried. They are written to the failed-email log
/// file and to the audit log as <see cref="AuditActions.EmailQuarterlyInvoiceReportFailed"/>
/// for support to review.
/// </remarks>
public sealed class QuarterlyReportEmailJob(
    IReportRecipientRepository recipients,
    ITenantRepository tenants,
    IEmailDeliveryRepository deliveries,
    IAuditLogRepository audits,
    IReportEmailSender sender,
    QuarterlyReportCsvWriter csvWriter,
    FailedReportEmailLog failedLog,
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

    /// <summary>
    /// How many times Hangfire retries a failed send. The job runs at most
    /// <c>MaxRetries + 1</c> times; the last run is the one where Hangfire's
    /// <c>RetryCount</c> job parameter equals this value.
    /// </summary>
    public const int MaxRetries = 5;

    // Set by Hangfire's AutomaticRetryAttribute before each retry.
    private const string RetryCountParameter = "RetryCount";

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
                job.SendAsync(tenantId, userId, year, quarterNumber, null, CancellationToken.None));
        }

        logger.LogInformation(
            "Queued {Count} report emails for tenant {TenantId}, quarter {Year}-Q{Quarter}",
            userIds.Count, tenantId, year, quarterNumber);
    }

    /// <param name="context">
    /// Filled in by Hangfire (queue it with <c>null</c>). Gives the retry count
    /// and the job id. Null when called directly, which counts as the first attempt.
    /// </param>
    // Five retries: Hangfire's growing retry delays then outlast ClaimLease,
    // so a claim left by a crashed job is taken over before retries run out.
    [AutomaticRetry(Attempts = MaxRetries)]
    public async Task SendAsync(
        int tenantId, int userId, int year, int quarterNumber, PerformContext? context, CancellationToken cancellationToken)
    {
        var quarter = new ReportingQuarter(year, quarterNumber);
        var quarterText = quarter.ToString();
        var retryCount = context?.GetJobParameter<int>(RetryCountParameter, allowStale: true) ?? 0;
        var jobId = context?.BackgroundJob?.Id;

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
            var missing = new FileNotFoundException($"Quarterly report {quarter} for tenant {tenantId} was not found.", Path.GetFileName(path));
            await RecordAttemptFailedAsync(tenantId, userId, quarter, ReportEmailFailureReasons.CsvMissing, retryCount, jobId, missing);
            throw missing;
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
                // On the last retry this is recorded as failed, because nothing
                // will try again.
                var inProgress = new InvalidOperationException(
                    $"Report {quarter} email for tenant {tenantId}, user {userId} is being sent by another job.");
                await RecordAttemptFailedAsync(tenantId, userId, quarter, ReportEmailFailureReasons.InProgress, retryCount, jobId, inProgress);
                throw inProgress;
        }

        var idempotencyKey = string.Create(CultureInfo.InvariantCulture, $"quarterly-report.{quarter}.t{tenantId}.u{userId}");

        if (claim.TookOverExpiredClaim)
        {
            // An earlier job may have sent the email before it crashed or lost
            // the provider's answer. Ask before sending again.
            PriorSendStatus prior;
            try
            {
                prior = await sender.FindPriorSendAsync(idempotencyKey, cancellationToken);
            }
            catch (Exception ex)
            {
                // Keep the claim. Releasing it would drop the takeover, and the
                // next retry would send without asking. Once the lease ends,
                // the next retry takes over again and repeats the lookup.
                await RecordAttemptFailedAsync(tenantId, userId, quarter, ReportEmailFailureReasons.LookupFailed, retryCount, jobId, ex);
                throw;
            }

            if (prior == PriorSendStatus.Sent)
            {
                await MarkSentAsync(claim, tenantId, userId, quarter, cancellationToken);
                logger.LogInformation(
                    "Report {Quarter} email for tenant {TenantId}, user {UserId} was found at the provider; not sent again",
                    quarter, tenantId, userId);
                return;
            }

            logger.LogInformation(
                "Taking over report {Quarter} email for tenant {TenantId}, user {UserId}; provider lookup: {PriorSend}",
                quarter, tenantId, userId, prior);
        }

        // Zipped after the claim, so a re-run of an email that was already
        // sent returns above instead of being recorded as too large. Zipping
        // stops at the limit, so a huge CSV is never held in memory. A retry
        // cannot make the zip smaller, so this frees the claim and returns
        // instead of throwing.
        byte[]? zip;
        try
        {
            zip = await QuarterlyReportZip.CreateAsync(path, options.Value.MaxAttachmentBytes, cancellationToken);
        }
        catch (Exception ex)
        {
            // Not sent, so free the claim and let the retry try again.
            await deliveries.ReleaseAsync(claim, CancellationToken.None);
            await RecordAttemptFailedAsync(tenantId, userId, quarter, ReportEmailFailureReasons.Temporary, retryCount, jobId, ex);
            throw;
        }

        if (zip is null)
        {
            await deliveries.ReleaseAsync(claim, CancellationToken.None);
            // The CSV size is logged: the zip's size is unknown once it passes the limit.
            logger.LogWarning(
                "Report {Quarter} for tenant {TenantId} ({CsvSize}-byte CSV) zips to over the {Limit}-byte email limit; not emailed to user {UserId}",
                quarter, tenantId, new FileInfo(path).Length, options.Value.MaxAttachmentBytes, userId);
            await RecordFailedAsync(tenantId, userId, quarter, ReportEmailFailureReasons.TooLarge, jobId);
            return;
        }

        try
        {
            var tenantName = await tenants.GetDisplayNameAsync(tenantId, cancellationToken) ?? $"Tenant {tenantId}";

            await sender.SendAsync(new ReportEmail(
                idempotencyKey,
                toAddress,
                Subject: $"Quarterly invoice report {quarter} – {tenantName}",
                Body: $"Attached is the quarterly invoice report for {tenantName}, {quarter}, as a zipped CSV file.",
                AttachmentFileName: QuarterlyReportZip.GetFileName(path),
                AttachmentContentType: QuarterlyReportZip.ContentType,
                AttachmentContent: zip), cancellationToken);
        }
        catch (ReportEmailPermanentException ex)
        {
            // The provider refused the email, so a retry would fail the same
            // way. Free the claim so a manual re-run can send it, and return
            // without throwing so Hangfire does not retry.
            await deliveries.ReleaseAsync(claim, CancellationToken.None);
            logger.LogWarning(
                "Provider rejected report {Quarter} email for tenant {TenantId}, user {UserId}: {Reason}",
                quarter, tenantId, userId, ex.Reason);
            await RecordFailedAsync(tenantId, userId, quarter, ReportEmailFailureReasons.Rejected(ex.Reason), jobId);
            return;
        }
        catch (ReportEmailOutcomeUnknownException ex)
        {
            // The email may have been sent. Keep the claim: retries see
            // InProgress until the lease ends, then take over and ask the
            // provider before sending again.
            await RecordAttemptFailedAsync(tenantId, userId, quarter, ReportEmailFailureReasons.OutcomeUnknown, retryCount, jobId, ex);
            throw;
        }
        catch (Exception ex)
        {
            // Not sent, so free the claim and let the retry try again.
            await deliveries.ReleaseAsync(claim, CancellationToken.None);
            await RecordAttemptFailedAsync(tenantId, userId, quarter, ReportEmailFailureReasons.Temporary, retryCount, jobId, ex);
            throw;
        }

        // If this save fails, the claim stays in the Sending state. Retries see
        // InProgress until the lease expires, then take over and ask the
        // provider before sending again.
        await MarkSentAsync(claim, tenantId, userId, quarter, cancellationToken);

        // The address is not logged; the user id is enough to trace it.
        logger.LogInformation("Emailed report {Quarter} for tenant {TenantId} to user {UserId}", quarter, tenantId, userId);
    }

    /// <summary>Marks the claim sent and adds the success audit entry, in one save.</summary>
    private Task MarkSentAsync(
        EmailDeliveryClaim claim, int tenantId, int userId, ReportingQuarter quarter, CancellationToken cancellationToken) =>
        deliveries.MarkSentAsync(claim, timeProvider.GetUtcNow().UtcDateTime, new AuditLog
        {
            TenantId = tenantId,
            Action = AuditActions.EmailQuarterlyInvoiceReport,
            Detail = string.Create(CultureInfo.InvariantCulture, $"{quarter}:user-{userId}"),
            ActorSubject = null, // System job, no caller.
            OccurredAtUtc = timeProvider.GetUtcNow(),
        }, cancellationToken);

    /// <summary>
    /// Records an email that will not be sent without help: one line in the
    /// failed-email log file, then one <see cref="AuditActions.EmailQuarterlyInvoiceReportFailed"/>
    /// entry. Skipped when the same failure is already in the audit log, so a
    /// re-run does not add duplicates.
    /// </summary>
    /// <remarks>
    /// Never throws. The log file is written before the audit entry and does
    /// not depend on it, so the failure is still recorded when the database is down.
    /// </remarks>
    private async Task RecordFailedAsync(int tenantId, int userId, ReportingQuarter quarter, string reason, string? jobId)
    {
        var detail = ReportEmailFailureReasons.Detail(quarter, userId, reason);

        try
        {
            if (await audits.ExistsAsync(tenantId, AuditActions.EmailQuarterlyInvoiceReportFailed, detail, CancellationToken.None))
            {
                return;
            }
        }
        catch (Exception ex)
        {
            // Can't tell whether it was recorded before; record it again rather than lose it.
            logger.LogError(ex, "Could not check the audit log for failed report email {Detail} of tenant {TenantId}", detail, tenantId);
        }

        failedLog.Record(tenantId, userId, quarter, reason, jobId);

        try
        {
            await audits.AddAsync(new AuditLog
            {
                TenantId = tenantId,
                Action = AuditActions.EmailQuarterlyInvoiceReportFailed,
                Detail = detail,
                ActorSubject = null, // System job, no caller.
                OccurredAtUtc = timeProvider.GetUtcNow(),
            }, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // The log file already has this failure. Don't hide it behind an audit error.
            logger.LogError(ex, "Could not audit failed report email {Detail} of tenant {TenantId}", detail, tenantId);
        }
    }

    /// <summary>
    /// Records a send attempt that failed and will be thrown to Hangfire. On
    /// the last attempt Hangfire gives up, so the email is recorded as failed
    /// (<c>retries-exhausted</c>). On earlier attempts only an
    /// <see cref="AuditActions.EmailQuarterlyInvoiceReportAttemptFailed"/> entry
    /// is written. Never throws, so the original error reaches Hangfire.
    /// </summary>
    private async Task RecordAttemptFailedAsync(
        int tenantId, int userId, ReportingQuarter quarter, string reason, int retryCount, string? jobId, Exception error)
    {
        // Only the exception type: a provider message can echo the address.
        logger.LogWarning(
            "Report {Quarter} email for tenant {TenantId}, user {UserId} failed on attempt {Attempt} of {Attempts} ({Reason}, {ErrorType})",
            quarter, tenantId, userId, retryCount + 1, MaxRetries + 1, reason, error.GetType().Name);

        if (retryCount >= MaxRetries)
        {
            await RecordFailedAsync(tenantId, userId, quarter, ReportEmailFailureReasons.RetriesExhausted(reason), jobId);
            return;
        }

        try
        {
            // One entry per attempt, so no duplicate check.
            await audits.AddAsync(new AuditLog
            {
                TenantId = tenantId,
                Action = AuditActions.EmailQuarterlyInvoiceReportAttemptFailed,
                Detail = ReportEmailFailureReasons.Detail(quarter, userId, reason),
                ActorSubject = null, // System job, no caller.
                OccurredAtUtc = timeProvider.GetUtcNow(),
            }, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not audit failed report email attempt for tenant {TenantId}, user {UserId}", tenantId, userId);
        }
    }
}
