using System.IO.Compression;
using InvoiceReporting.Api.Data;
using InvoiceReporting.Api.Data.Repositories;
using InvoiceReporting.Api.Domain;
using InvoiceReporting.Api.Reporting;
using InvoiceReporting.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace InvoiceReporting.Api.Tests.Integration;

/// <summary>
/// Tests share one database, and sent emails are recorded in the audit log,
/// so each test uses its own quarter.
/// </summary>
public sealed class QuarterlyReportEmailJobTests(InvoiceReportApiFactory factory)
    : IClassFixture<InvoiceReportApiFactory>, IDisposable
{
    private readonly string _outputDirectory =
        Path.Combine(Path.GetTempPath(), "invoice-reporting-tests", Guid.NewGuid().ToString("N"));

    private readonly FakeReportEmailSender _sender = new();
    private readonly FakeBackgroundJobClient _jobs = new();
    private readonly CapturingLoggerFactory<FailedReportEmailLog> _failedLog = new();

    [Fact]
    public async Task QueueRecipientsAsync_QueuesOneSendPerRecipient_WithUserIdsOnly()
    {
        var (tenantId, expectedUserIds) = await factory.WithDbAsync(async db =>
        {
            var tenant = await TenantIdAsync(db);
            var ids = await db.Users
                .Where(u => u.Subject == TestData.AlphaUserSubject || u.Subject == TestData.AdminAssignedToASubject)
                .OrderBy(u => u.Id)
                .Select(u => u.Id)
                .ToListAsync();
            return (tenant, ids);
        });

        await RunAsync(job => job.QueueRecipientsAsync(tenantId, 2026, 1, CancellationToken.None));

        Assert.All(_jobs.Created, job => Assert.Equal(nameof(QuarterlyReportEmailJob.SendAsync), job.Method.Name));
        Assert.Equal(expectedUserIds, _jobs.Created.Select(job => (int)job.Args[1]!));
        Assert.DoesNotContain(_jobs.Created.SelectMany(job => job.Args), arg => arg is string);
    }

    [Fact]
    public async Task QueueRecipientsAsync_WhenEmailIsDisabled_QueuesNothing()
    {
        var tenantId = await factory.WithDbAsync(TenantIdAsync);

        await RunAsync(job => job.QueueRecipientsAsync(tenantId, 2026, 1, CancellationToken.None), enabled: false);

        Assert.Empty(_jobs.Created);
    }

    [Fact]
    public async Task SendAsync_EmailsTheCsvZipped_AndASecondRunSendsNothing()
    {
        var (tenantId, userId) = await TenantAAndUserAsync(TestData.AlphaUserSubject);
        var csvPath = await WriteCsvAsync(tenantId, new ReportingQuarter(2026, 2));

        await RunAsync(job => job.SendAsync(tenantId, userId, 2026, 2, null, CancellationToken.None));
        await RunAsync(job => job.SendAsync(tenantId, userId, 2026, 2, null, CancellationToken.None));

        var email = Assert.Single(_sender.Sent);
        Assert.Equal($"quarterly-report.2026-Q2.t{tenantId}.u{userId}", email.IdempotencyKey);
        Assert.Equal("alpha.user@example.com", email.ToAddress);
        Assert.Equal("Quarterly invoice report 2026-Q2 – Tenant A (test)", email.Subject);
        Assert.Equal("2026-Q2-tenant-" + tenantId + ".zip", email.AttachmentFileName);
        Assert.Equal("application/zip", email.AttachmentContentType);
        Assert.Contains("zipped CSV", email.Body);
        Assert.Equal(await File.ReadAllBytesAsync(csvPath), Unzip(email.AttachmentContent, Path.GetFileName(csvPath)));
        // A fresh claim needs no provider lookup.
        Assert.Empty(_sender.Lookups);
    }

    [Theory]
    [InlineData(TestData.AlphaOptOutSubject)]
    [InlineData(TestData.BravoUserWithStrayAssignmentSubject)]
    [InlineData(TestData.AdminWithoutAssignmentsSubject)]
    public async Task SendAsync_ToAUserWhoDoesNotQualify_SendsNothing(string subject)
    {
        var (tenantId, userId) = await TenantAAndUserAsync(subject);
        await WriteCsvAsync(tenantId, new ReportingQuarter(2026, 3));

        await RunAsync(job => job.SendAsync(tenantId, userId, 2026, 3, null, CancellationToken.None));

        Assert.Empty(_sender.Sent);
    }

    [Fact]
    public async Task SendAsync_WhenTheCsvIsMissing_Throws_SoHangfireRetries()
    {
        var (tenantId, userId) = await TenantAAndUserAsync(TestData.AlphaUserSubject);

        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            RunAsync(job => job.SendAsync(tenantId, userId, 2026, 4, null, CancellationToken.None)));
        Assert.Empty(_sender.Sent);
        Assert.Equal(
            [$"2026-Q4:user-{userId}:csv-missing"],
            await AuditDetailsAsync(AuditActions.EmailQuarterlyInvoiceReportAttemptFailed, tenantId, "2026-Q4"));
        Assert.Empty(_failedLog.Logger.Entries);
    }

    [Fact]
    public async Task SendAsync_WhenTheCsvIsStillMissingOnTheLastRetry_RecordsFailed()
    {
        var (tenantId, userId) = await TenantAAndUserAsync(TestData.AlphaUserSubject);
        var context = HangfireTestContext.Create(retryCount: QuarterlyReportEmailJob.MaxRetries);

        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            RunAsync(job => job.SendAsync(tenantId, userId, 2020, 1, context, CancellationToken.None)));

        Assert.Equal(
            [$"2020-Q1:user-{userId}:retries-exhausted:csv-missing"],
            await FailedAuditDetailsAsync(tenantId, "2020-Q1"));
        AssertSingleFailedLogLine(tenantId, userId, "2020-Q1", "retries-exhausted:csv-missing", context.BackgroundJob.Id);
    }

    [Fact]
    public async Task SendAsync_WhenAnotherJobStillHoldsTheClaimOnTheLastRetry_RecordsFailed()
    {
        var (tenantId, userId) = await TenantAAndUserAsync(TestData.AlphaUserSubject);
        await WriteCsvAsync(tenantId, new ReportingQuarter(2020, 2));
        await InsertClaimAsync(tenantId, userId, "2020-Q2", claimedAgo: TimeSpan.FromSeconds(10));
        var context = HangfireTestContext.Create(retryCount: QuarterlyReportEmailJob.MaxRetries);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RunAsync(job => job.SendAsync(tenantId, userId, 2020, 2, context, CancellationToken.None)));

        Assert.Empty(_sender.Sent);
        Assert.Equal(
            [$"2020-Q2:user-{userId}:retries-exhausted:in-progress"],
            await FailedAuditDetailsAsync(tenantId, "2020-Q2"));
        AssertSingleFailedLogLine(tenantId, userId, "2020-Q2", "retries-exhausted:in-progress", context.BackgroundJob.Id);
    }

    [Fact]
    public async Task SendAsync_WhenTheSendFails_ReleasesTheClaim_SoTheRetrySends()
    {
        var (tenantId, userId) = await TenantAAndUserAsync(TestData.AlphaUserSubject);
        await WriteCsvAsync(tenantId, new ReportingQuarter(2025, 1));
        _sender.FailNextSends = 1;

        await Assert.ThrowsAsync<IOException>(() =>
            RunAsync(job => job.SendAsync(tenantId, userId, 2025, 1, null, CancellationToken.None)));
        await RunAsync(job => job.SendAsync(tenantId, userId, 2025, 1, null, CancellationToken.None));

        Assert.Single(_sender.Sent);
        Assert.Equal(EmailDeliveryStatus.Sent, (await DeliveryAsync(tenantId, userId, "2025-Q1")).Status);
        // A temporary failure is retried, so it is not a failure for support.
        Assert.Empty(_failedLog.Logger.Entries);
        Assert.Equal(0, await FailedAuditCountAsync(tenantId, "2025-Q1"));
        Assert.Equal(
            [$"2025-Q1:user-{userId}:temporary"],
            await AuditDetailsAsync(AuditActions.EmailQuarterlyInvoiceReportAttemptFailed, tenantId, "2025-Q1"));
    }

    [Fact]
    public async Task SendAsync_BeforeTheLastRetry_RecordsOnlyAnAttemptFailed()
    {
        var (tenantId, userId) = await TenantAAndUserAsync(TestData.AlphaUserSubject);
        await WriteCsvAsync(tenantId, new ReportingQuarter(2022, 1));
        _sender.FailNextSends = 1;
        var context = HangfireTestContext.Create(retryCount: QuarterlyReportEmailJob.MaxRetries - 1);

        await Assert.ThrowsAsync<IOException>(() =>
            RunAsync(job => job.SendAsync(tenantId, userId, 2022, 1, context, CancellationToken.None)));

        Assert.Equal(
            [$"2022-Q1:user-{userId}:temporary"],
            await AuditDetailsAsync(AuditActions.EmailQuarterlyInvoiceReportAttemptFailed, tenantId, "2022-Q1"));
        Assert.Equal(0, await FailedAuditCountAsync(tenantId, "2022-Q1"));
        Assert.Empty(_failedLog.Logger.Entries);
    }

    [Fact]
    public async Task SendAsync_OnTheLastRetry_RecordsFailed_RetriesExhausted_AndStillThrows()
    {
        var (tenantId, userId) = await TenantAAndUserAsync(TestData.AlphaUserSubject);
        await WriteCsvAsync(tenantId, new ReportingQuarter(2025, 4));
        _sender.FailNextSends = 1;
        var context = HangfireTestContext.Create(retryCount: QuarterlyReportEmailJob.MaxRetries);

        // Still thrown, so Hangfire marks the job Failed in its dashboard.
        await Assert.ThrowsAsync<IOException>(() =>
            RunAsync(job => job.SendAsync(tenantId, userId, 2025, 4, context, CancellationToken.None)));

        Assert.Equal(
            [$"2025-Q4:user-{userId}:retries-exhausted:temporary"],
            await FailedAuditDetailsAsync(tenantId, "2025-Q4"));
        Assert.Empty(await AuditDetailsAsync(AuditActions.EmailQuarterlyInvoiceReportAttemptFailed, tenantId, "2025-Q4"));
        AssertSingleFailedLogLine(tenantId, userId, "2025-Q4", "retries-exhausted:temporary", context.BackgroundJob.Id);
    }

    [Fact]
    public async Task SendAsync_WhenTheOutcomeIsUnknownOnTheLastRetry_RecordsRetriesExhaustedOutcomeUnknown()
    {
        var (tenantId, userId) = await TenantAAndUserAsync(TestData.AlphaUserSubject);
        await WriteCsvAsync(tenantId, new ReportingQuarter(2022, 2));
        _sender.FailureKind = FakeSendFailure.OutcomeUnknown;
        _sender.FailNextSends = 1;
        var context = HangfireTestContext.Create(retryCount: QuarterlyReportEmailJob.MaxRetries);

        await Assert.ThrowsAsync<ReportEmailOutcomeUnknownException>(() =>
            RunAsync(job => job.SendAsync(tenantId, userId, 2022, 2, context, CancellationToken.None)));

        Assert.Equal(
            [$"2022-Q2:user-{userId}:retries-exhausted:outcome-unknown"],
            await FailedAuditDetailsAsync(tenantId, "2022-Q2"));
    }

    [Fact]
    public async Task SendAsync_WhenTheZipIsOverTheLimit_RecordsTooLargeOnce_EvenWhenRunTwice()
    {
        var (tenantId, userId) = await TenantAAndUserAsync(TestData.AlphaUserSubject);
        await WriteCsvAsync(tenantId, new ReportingQuarter(2024, 1));

        // Completes without throwing, so Hangfire does not retry.
        await RunAsync(job => job.SendAsync(tenantId, userId, 2024, 1, null, CancellationToken.None), maxAttachmentBytes: 1);
        await RunAsync(job => job.SendAsync(tenantId, userId, 2024, 1, null, CancellationToken.None), maxAttachmentBytes: 1);

        Assert.Empty(_sender.Sent);
        Assert.Null(await FindDeliveryAsync(tenantId, userId, "2024-Q1"));
        Assert.Equal(
            [$"2024-Q1:user-{userId}:too-large"],
            await FailedAuditDetailsAsync(tenantId, "2024-Q1"));
        AssertSingleFailedLogLine(tenantId, userId, "2024-Q1", ReportEmailFailureReasons.TooLarge);
    }

    /// <summary>Found by a compose run: a re-run with a lower limit flagged emails that had already gone out.</summary>
    [Fact]
    public async Task SendAsync_WhenAlreadySent_AndTheZipIsNowOverTheLimit_RecordsNoFailure()
    {
        var (tenantId, userId) = await TenantAAndUserAsync(TestData.AlphaUserSubject);
        await WriteCsvAsync(tenantId, new ReportingQuarter(2021, 4));
        await RunAsync(job => job.SendAsync(tenantId, userId, 2021, 4, null, CancellationToken.None));

        await RunAsync(job => job.SendAsync(tenantId, userId, 2021, 4, null, CancellationToken.None), maxAttachmentBytes: 1);

        Assert.Single(_sender.Sent);
        Assert.Equal(0, await FailedAuditCountAsync(tenantId, "2021-Q4"));
        Assert.Empty(_failedLog.Logger.Entries);
    }

    [Fact]
    public async Task SendAsync_WhenTheZipIsExactlyAtTheLimit_Sends()
    {
        var (tenantId, userId) = await TenantAAndUserAsync(TestData.AlphaUserSubject);
        var csvPath = await WriteCsvAsync(tenantId, new ReportingQuarter(2024, 2));
        var size = (await QuarterlyReportZip.CreateAsync(csvPath, long.MaxValue, CancellationToken.None))!.Length;

        await RunAsync(job => job.SendAsync(tenantId, userId, 2024, 2, null, CancellationToken.None), maxAttachmentBytes: size);

        Assert.Single(_sender.Sent);
        Assert.Empty(_failedLog.Logger.Entries);
    }

    [Fact]
    public async Task SendAsync_WhenTheProviderRejectsPermanently_ReleasesTheClaim_RecordsFailed_AndDoesNotThrow()
    {
        var (tenantId, userId) = await TenantAAndUserAsync(TestData.AlphaUserSubject);
        await WriteCsvAsync(tenantId, new ReportingQuarter(2024, 3));
        _sender.FailureKind = FakeSendFailure.Permanent;
        _sender.FailNextSends = 1;

        await RunAsync(job => job.SendAsync(tenantId, userId, 2024, 3, null, CancellationToken.None));

        Assert.Empty(_sender.Sent);
        // Released, so a manual re-run of the report can send it.
        Assert.Null(await FindDeliveryAsync(tenantId, userId, "2024-Q3"));
        Assert.Equal(
            [$"2024-Q3:user-{userId}:rejected:http-400:mj-0013"],
            await FailedAuditDetailsAsync(tenantId, "2024-Q3"));
        var entry = AssertSingleFailedLogLine(tenantId, userId, "2024-Q3", "rejected:http-400:mj-0013");
        Assert.DoesNotContain(entry.State.Values, value => value?.ToString()?.Contains('@') == true);
    }

    [Fact]
    public async Task SendAsync_WhenTheAuditWriteFails_StillWritesTheFailedLog()
    {
        var (tenantId, userId) = await TenantAAndUserAsync(TestData.AlphaUserSubject);
        await WriteCsvAsync(tenantId, new ReportingQuarter(2024, 4));

        await factory.WithDbAsync(async db =>
        {
            await CreateJob(db, enabled: true, maxAttachmentBytes: 1, audits: new FailingAuditLogRepository())
                .SendAsync(tenantId, userId, 2024, 4, null, CancellationToken.None);
            return 0;
        });

        AssertSingleFailedLogLine(tenantId, userId, "2024-Q4", ReportEmailFailureReasons.TooLarge);
    }

    private CapturingLogger<FailedReportEmailLog>.LogEntry AssertSingleFailedLogLine(
        int tenantId, int userId, string quarter, string reason, string jobId = "-")
    {
        Assert.All(_failedLog.Categories, category => Assert.Equal(FailedReportEmailLog.Category, category));
        var entry = Assert.Single(_failedLog.Logger.Entries);
        Assert.Equal(tenantId, entry.State["TenantId"]);
        Assert.Equal(userId, entry.State["UserId"]);
        Assert.Equal(quarter, entry.State["Quarter"]);
        Assert.Equal(reason, entry.State["Reason"]);
        Assert.Equal(jobId, entry.State["JobId"]);
        return entry;
    }

    private async Task<int> FailedAuditCountAsync(int tenantId, string quarter) =>
        (await FailedAuditDetailsAsync(tenantId, quarter)).Count;

    private Task<List<string?>> FailedAuditDetailsAsync(int tenantId, string quarter) =>
        AuditDetailsAsync(AuditActions.EmailQuarterlyInvoiceReportFailed, tenantId, quarter);

    private Task<List<string?>> AuditDetailsAsync(string action, int tenantId, string quarter) =>
        factory.WithDbAsync(db => db.AuditLogs.AsNoTracking()
            .Where(a => a.TenantId == tenantId
                && a.Action == action
                && a.Detail!.StartsWith(quarter + ":"))
            .Select(a => a.Detail)
            .ToListAsync());

    private Task<QuarterlyReportEmailDelivery?> FindDeliveryAsync(int tenantId, int userId, string quarter) =>
        factory.WithDbAsync(db => db.QuarterlyReportEmailDeliveries.AsNoTracking()
            .SingleOrDefaultAsync(d => d.TenantId == tenantId && d.UserId == userId && d.Quarter == quarter));

    /// <summary>Stands in for an audit log whose database is down.</summary>
    private sealed class FailingAuditLogRepository : IAuditLogRepository
    {
        public Task<bool> ExistsAsync(int tenantId, string action, string detail, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Database unavailable.");

        public Task AddAsync(AuditLog entry, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Database unavailable.");

        public Task<bool> TryAddUniqueAsync(AuditLog entry, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Database unavailable.");
    }

    /// <summary>
    /// Covers two jobs for the same email at once, which happens when the CSV
    /// job re-queues recipients on a retry: the second job finds the first one's claim.
    /// </summary>
    [Fact]
    public async Task SendAsync_WhenAnotherJobHoldsAFreshClaim_SendsNothing_AndThrowsToRetryLater()
    {
        var (tenantId, userId) = await TenantAAndUserAsync(TestData.AlphaUserSubject);
        await WriteCsvAsync(tenantId, new ReportingQuarter(2025, 2));
        await InsertClaimAsync(tenantId, userId, "2025-Q2", claimedAgo: TimeSpan.FromSeconds(10));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RunAsync(job => job.SendAsync(tenantId, userId, 2025, 2, null, CancellationToken.None)));

        Assert.Empty(_sender.Sent);
        Assert.Equal(
            [$"2025-Q2:user-{userId}:in-progress"],
            await AuditDetailsAsync(AuditActions.EmailQuarterlyInvoiceReportAttemptFailed, tenantId, "2025-Q2"));
    }

    [Fact]
    public async Task SendAsync_TakesOverAnExpiredClaim_AndSendsOnce()
    {
        // An expired claim means a job crashed mid-send.
        var (tenantId, userId) = await TenantAAndUserAsync(TestData.AlphaUserSubject);
        await WriteCsvAsync(tenantId, new ReportingQuarter(2025, 3));
        await InsertClaimAsync(tenantId, userId, "2025-Q3", claimedAgo: QuarterlyReportEmailJob.ClaimLease + TimeSpan.FromMinutes(1));

        await RunAsync(job => job.SendAsync(tenantId, userId, 2025, 3, null, CancellationToken.None));
        await RunAsync(job => job.SendAsync(tenantId, userId, 2025, 3, null, CancellationToken.None));

        var email = Assert.Single(_sender.Sent);
        Assert.Equal($"quarterly-report.2025-Q3.t{tenantId}.u{userId}", email.IdempotencyKey);
        // Asked the provider once, on the takeover only.
        Assert.Equal([email.IdempotencyKey], _sender.Lookups);
    }

    [Fact]
    public async Task SendAsync_WhenTheOutcomeIsUnknown_KeepsTheClaim_AndThrows()
    {
        var (tenantId, userId) = await TenantAAndUserAsync(TestData.AlphaUserSubject);
        await WriteCsvAsync(tenantId, new ReportingQuarter(2022, 3));
        _sender.FailureKind = FakeSendFailure.OutcomeUnknown;
        _sender.FailNextSends = 1;

        await Assert.ThrowsAsync<ReportEmailOutcomeUnknownException>(() =>
            RunAsync(job => job.SendAsync(tenantId, userId, 2022, 3, null, CancellationToken.None)));

        Assert.Equal(EmailDeliveryStatus.Sending, (await DeliveryAsync(tenantId, userId, "2022-Q3")).Status);
        Assert.Equal(
            [$"2022-Q3:user-{userId}:outcome-unknown"],
            await AuditDetailsAsync(AuditActions.EmailQuarterlyInvoiceReportAttemptFailed, tenantId, "2022-Q3"));

        // A retry before the lease ends must not send: the email may be out already.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RunAsync(job => job.SendAsync(tenantId, userId, 2022, 3, null, CancellationToken.None)));
        Assert.Empty(_sender.Sent);
    }

    [Fact]
    public async Task SendAsync_OnTakeover_WhenTheProviderAlreadySentIt_MarksSentWithoutResending()
    {
        var (tenantId, userId) = await TenantAAndUserAsync(TestData.AlphaUserSubject);
        await WriteCsvAsync(tenantId, new ReportingQuarter(2022, 4));
        await InsertClaimAsync(tenantId, userId, "2022-Q4", claimedAgo: QuarterlyReportEmailJob.ClaimLease + TimeSpan.FromMinutes(1));
        _sender.LookupResult = PriorSendStatus.Sent;

        await RunAsync(job => job.SendAsync(tenantId, userId, 2022, 4, null, CancellationToken.None));

        Assert.Empty(_sender.Sent);
        Assert.Equal([$"quarterly-report.2022-Q4.t{tenantId}.u{userId}"], _sender.Lookups);
        Assert.Equal(EmailDeliveryStatus.Sent, (await DeliveryAsync(tenantId, userId, "2022-Q4")).Status);
        Assert.Equal(
            [$"2022-Q4:user-{userId}"],
            await AuditDetailsAsync(AuditActions.EmailQuarterlyInvoiceReport, tenantId, "2022-Q4"));
    }

    [Theory]
    [InlineData(PriorSendStatus.NotFound, 1)]
    [InlineData(PriorSendStatus.Unsupported, 2)]
    public async Task SendAsync_OnTakeover_WhenTheProviderHasNoRecord_Sends(PriorSendStatus lookupResult, int quarterNumber)
    {
        var (tenantId, userId) = await TenantAAndUserAsync(TestData.AlphaUserSubject);
        await WriteCsvAsync(tenantId, new ReportingQuarter(2021, quarterNumber));
        await InsertClaimAsync(tenantId, userId, $"2021-Q{quarterNumber}", claimedAgo: QuarterlyReportEmailJob.ClaimLease + TimeSpan.FromMinutes(1));
        _sender.LookupResult = lookupResult;

        await RunAsync(job => job.SendAsync(tenantId, userId, 2021, quarterNumber, null, CancellationToken.None));

        Assert.Single(_sender.Sent);
        Assert.Single(_sender.Lookups);
        Assert.Equal(EmailDeliveryStatus.Sent, (await DeliveryAsync(tenantId, userId, $"2021-Q{quarterNumber}")).Status);
    }

    [Fact]
    public async Task SendAsync_OnTakeover_WhenTheLookupFails_KeepsTheClaim_AndThrows()
    {
        var (tenantId, userId) = await TenantAAndUserAsync(TestData.AlphaUserSubject);
        await WriteCsvAsync(tenantId, new ReportingQuarter(2021, 3));
        await InsertClaimAsync(tenantId, userId, "2021-Q3", claimedAgo: QuarterlyReportEmailJob.ClaimLease + TimeSpan.FromMinutes(1));
        _sender.FailNextLookups = 1;

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            RunAsync(job => job.SendAsync(tenantId, userId, 2021, 3, null, CancellationToken.None)));

        Assert.Empty(_sender.Sent);
        Assert.Equal(EmailDeliveryStatus.Sending, (await DeliveryAsync(tenantId, userId, "2021-Q3")).Status);
        Assert.Equal(
            [$"2021-Q3:user-{userId}:lookup-failed"],
            await AuditDetailsAsync(AuditActions.EmailQuarterlyInvoiceReportAttemptFailed, tenantId, "2021-Q3"));
    }

    [Fact]
    public async Task QueueRecipientsAsync_WhenCancelled_Throws_AndQueuesNothing()
    {
        var tenantId = await factory.WithDbAsync(TenantIdAsync);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            RunAsync(job => job.QueueRecipientsAsync(tenantId, 2019, 1, cts.Token)));

        Assert.Empty(_jobs.Created);
    }

    [Fact]
    public async Task SendAsync_WhenCancelledDuringTheProviderLookup_KeepsTheClaim_AndRecordsNoFailure()
    {
        var (tenantId, userId) = await TenantAAndUserAsync(TestData.AlphaUserSubject);
        await WriteCsvAsync(tenantId, new ReportingQuarter(2019, 1));
        await InsertClaimAsync(tenantId, userId, "2019-Q1", claimedAgo: QuarterlyReportEmailJob.ClaimLease + TimeSpan.FromMinutes(1));
        using var cts = new CancellationTokenSource();
        _sender.OnLookup = cts.Cancel;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            RunAsync(job => job.SendAsync(tenantId, userId, 2019, 1, null, cts.Token)));

        Assert.Empty(_sender.Sent);
        Assert.Equal(EmailDeliveryStatus.Sending, (await DeliveryAsync(tenantId, userId, "2019-Q1")).Status);
        await AssertNoFailureRecordedAsync(tenantId, "2019-Q1");
    }

    [Fact]
    public async Task SendAsync_WhenCancelledBeforeZipping_ReleasesTheClaim_AndRecordsNoFailure()
    {
        var (tenantId, userId) = await TenantAAndUserAsync(TestData.AlphaUserSubject);
        await WriteCsvAsync(tenantId, new ReportingQuarter(2019, 2));
        using var cts = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            RunAsync(job => job.SendAsync(tenantId, userId, 2019, 2, null, cts.Token), cancelAfterClaim: cts));

        Assert.Empty(_sender.Sent);
        Assert.Null(await FindDeliveryAsync(tenantId, userId, "2019-Q2"));
        await AssertNoFailureRecordedAsync(tenantId, "2019-Q2");
    }

    /// <summary>
    /// The last retry is where a failure becomes "retries-exhausted". A
    /// cancellation there must not be recorded as a failed email.
    /// </summary>
    [Fact]
    public async Task SendAsync_WhenCancelledMidSendOnTheLastRetry_KeepsTheClaim_AndRecordsNoFailure()
    {
        var (tenantId, userId) = await TenantAAndUserAsync(TestData.AlphaUserSubject);
        await WriteCsvAsync(tenantId, new ReportingQuarter(2019, 3));
        var context = HangfireTestContext.Create(retryCount: QuarterlyReportEmailJob.MaxRetries);
        using var cts = new CancellationTokenSource();
        _sender.OnSend = cts.Cancel;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            RunAsync(job => job.SendAsync(tenantId, userId, 2019, 3, context, cts.Token)));

        Assert.Empty(_sender.Sent);
        // Kept: the email may be out, so a later run asks the provider first.
        Assert.Equal(EmailDeliveryStatus.Sending, (await DeliveryAsync(tenantId, userId, "2019-Q3")).Status);
        await AssertNoFailureRecordedAsync(tenantId, "2019-Q3");
        Assert.Empty(await AuditDetailsAsync(AuditActions.EmailQuarterlyInvoiceReport, tenantId, "2019-Q3"));
    }

    /// <summary>
    /// The tenant name is read just before sending. A cancel there means
    /// nothing was sent, so the claim is freed and a retry can send at once.
    /// </summary>
    [Fact]
    public async Task SendAsync_WhenCancelledWhileReadingTheTenantName_ReleasesTheClaim_AndRecordsNoFailure()
    {
        var (tenantId, userId) = await TenantAAndUserAsync(TestData.AlphaUserSubject);
        await WriteCsvAsync(tenantId, new ReportingQuarter(2017, 1));
        using var cts = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            RunAsync(job => job.SendAsync(tenantId, userId, 2017, 1, null, cts.Token), cancelOnTenantName: cts));

        Assert.Empty(_sender.Sent);
        Assert.Null(await FindDeliveryAsync(tenantId, userId, "2017-Q1"));
        await AssertNoFailureRecordedAsync(tenantId, "2017-Q1");
    }

    /// <summary>Cancels the job's token when the tenant name is read, then reads it with that token.</summary>
    private sealed class CancelOnTenantNameRepository(ITenantRepository inner, CancellationTokenSource cts) : ITenantRepository
    {
        public Task<Api.Tenancy.ResolvedTenant?> FindByKeyAsync(string normalizedKey, CancellationToken cancellationToken) =>
            inner.FindByKeyAsync(normalizedKey, cancellationToken);

        public Task<IReadOnlyList<int>> GetAllIdsAsync(CancellationToken cancellationToken) =>
            inner.GetAllIdsAsync(cancellationToken);

        public async Task<string?> GetDisplayNameAsync(int tenantId, CancellationToken cancellationToken)
        {
            await cts.CancelAsync();
            return await inner.GetDisplayNameAsync(tenantId, cancellationToken);
        }
    }

    private async Task AssertNoFailureRecordedAsync(int tenantId, string quarter)
    {
        Assert.Empty(await AuditDetailsAsync(AuditActions.EmailQuarterlyInvoiceReportAttemptFailed, tenantId, quarter));
        Assert.Empty(await FailedAuditDetailsAsync(tenantId, quarter));
        Assert.Empty(_failedLog.Logger.Entries);
    }

    /// <summary>Cancels the job's token right after it claims the email, before zipping.</summary>
    private sealed class CancelAfterClaimRepository(IEmailDeliveryRepository inner, CancellationTokenSource cts)
        : IEmailDeliveryRepository
    {
        public async Task<EmailDeliveryClaim> TryClaimAsync(
            int tenantId, int userId, string quarter, DateTime nowUtc, TimeSpan lease, CancellationToken cancellationToken)
        {
            var claim = await inner.TryClaimAsync(tenantId, userId, quarter, nowUtc, lease, cancellationToken);
            await cts.CancelAsync();
            return claim;
        }

        public Task MarkSentAsync(EmailDeliveryClaim claim, DateTime nowUtc, AuditLog audit, CancellationToken cancellationToken) =>
            inner.MarkSentAsync(claim, nowUtc, audit, cancellationToken);

        public Task ReleaseAsync(EmailDeliveryClaim claim, CancellationToken cancellationToken) =>
            inner.ReleaseAsync(claim, cancellationToken);
    }

    private Task<QuarterlyReportEmailDelivery> DeliveryAsync(int tenantId, int userId, string quarter) =>
        factory.WithDbAsync(db => db.QuarterlyReportEmailDeliveries.AsNoTracking()
            .SingleAsync(d => d.TenantId == tenantId && d.UserId == userId && d.Quarter == quarter));

    private Task InsertClaimAsync(int tenantId, int userId, string quarter, TimeSpan claimedAgo) =>
        factory.WithDbAsync(async db =>
        {
            db.QuarterlyReportEmailDeliveries.Add(new QuarterlyReportEmailDelivery
            {
                TenantId = tenantId,
                UserId = userId,
                Quarter = quarter,
                Status = EmailDeliveryStatus.Sending,
                ClaimToken = Guid.NewGuid(),
                ClaimedAtUtc = DateTime.UtcNow - claimedAgo,
            });
            return await db.SaveChangesAsync();
        });

    private Task RunAsync(
        Func<QuarterlyReportEmailJob, Task> action,
        bool enabled = true,
        long maxAttachmentBytes = QuarterlyReportEmailOptions.MaxAllowedAttachmentBytes,
        CancellationTokenSource? cancelAfterClaim = null,
        CancellationTokenSource? cancelOnTenantName = null) =>
        factory.WithDbAsync(async db =>
        {
            await action(CreateJob(db, enabled, maxAttachmentBytes, cancelAfterClaim: cancelAfterClaim, cancelOnTenantName: cancelOnTenantName));
            return 0;
        });

    private QuarterlyReportEmailJob CreateJob(
        ReportingDbContext db,
        bool enabled,
        long maxAttachmentBytes = QuarterlyReportEmailOptions.MaxAllowedAttachmentBytes,
        IAuditLogRepository? audits = null,
        CancellationTokenSource? cancelAfterClaim = null,
        CancellationTokenSource? cancelOnTenantName = null) =>
        new(
            new ReportRecipientRepository(db),
            cancelOnTenantName is null
                ? new TenantRepository(db)
                : new CancelOnTenantNameRepository(new TenantRepository(db), cancelOnTenantName),
            cancelAfterClaim is null
                ? new EmailDeliveryRepository(db)
                : new CancelAfterClaimRepository(new EmailDeliveryRepository(db), cancelAfterClaim),
            audits ?? new AuditLogRepository(db),
            _sender,
            CreateCsvWriter(),
            new FailedReportEmailLog(_failedLog),
            _jobs,
            Options.Create(new QuarterlyReportEmailOptions { Enabled = enabled, MaxAttachmentBytes = maxAttachmentBytes }),
            TimeProvider.System,
            NullLogger<QuarterlyReportEmailJob>.Instance);

    private QuarterlyReportCsvWriter CreateCsvWriter() =>
        new(
            Options.Create(new QuarterlyReportOutputOptions { Directory = _outputDirectory }),
            factory.Services.GetRequiredService<IHostEnvironment>());

    private Task<string> WriteCsvAsync(int tenantId, ReportingQuarter quarter) =>
        CreateCsvWriter().WriteAsync(tenantId, quarter, [], 0m, CancellationToken.None);

    private static byte[] Unzip(byte[] zip, string expectedEntryName)
    {
        using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
        var entry = Assert.Single(archive.Entries);
        Assert.Equal(expectedEntryName, entry.FullName);
        using var content = new MemoryStream();
        using (var entryStream = entry.Open())
        {
            entryStream.CopyTo(content);
        }

        return content.ToArray();
    }

    private Task<(int TenantId, int UserId)> TenantAAndUserAsync(string subject) =>
        factory.WithDbAsync(async db => (
            await TenantIdAsync(db),
            await db.Users.Where(u => u.Subject == subject).Select(u => u.Id).SingleAsync()));

    private static Task<int> TenantIdAsync(ReportingDbContext db) =>
        db.Tenants.Where(t => t.Key == TestData.TenantAKey).Select(t => t.Id).SingleAsync();

    public void Dispose()
    {
        if (Directory.Exists(_outputDirectory))
        {
            Directory.Delete(_outputDirectory, recursive: true);
        }
    }
}
