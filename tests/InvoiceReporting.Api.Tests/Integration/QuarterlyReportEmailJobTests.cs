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
    public async Task SendAsync_EmailsTheCsv_AndASecondRunSendsNothing()
    {
        var (tenantId, userId) = await TenantAAndUserAsync(TestData.AlphaUserSubject);
        var csvPath = await WriteCsvAsync(tenantId, new ReportingQuarter(2026, 2));

        await RunAsync(job => job.SendAsync(tenantId, userId, 2026, 2, CancellationToken.None));
        await RunAsync(job => job.SendAsync(tenantId, userId, 2026, 2, CancellationToken.None));

        var email = Assert.Single(_sender.Sent);
        Assert.Equal($"quarterly-report.2026-Q2.t{tenantId}.u{userId}", email.IdempotencyKey);
        Assert.Equal("alpha.user@example.com", email.ToAddress);
        Assert.Equal("Quarterly invoice report 2026-Q2 – Tenant A (test)", email.Subject);
        Assert.Equal(Path.GetFileName(csvPath), email.AttachmentFileName);
        Assert.Equal(await File.ReadAllBytesAsync(csvPath), email.AttachmentContent);
    }

    [Theory]
    [InlineData(TestData.AlphaOptOutSubject)]
    [InlineData(TestData.BravoUserWithStrayAssignmentSubject)]
    [InlineData(TestData.AdminWithoutAssignmentsSubject)]
    public async Task SendAsync_ToAUserWhoDoesNotQualify_SendsNothing(string subject)
    {
        var (tenantId, userId) = await TenantAAndUserAsync(subject);
        await WriteCsvAsync(tenantId, new ReportingQuarter(2026, 3));

        await RunAsync(job => job.SendAsync(tenantId, userId, 2026, 3, CancellationToken.None));

        Assert.Empty(_sender.Sent);
    }

    [Fact]
    public async Task SendAsync_WhenTheCsvIsMissing_Throws_SoHangfireRetries()
    {
        var (tenantId, userId) = await TenantAAndUserAsync(TestData.AlphaUserSubject);

        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            RunAsync(job => job.SendAsync(tenantId, userId, 2026, 4, CancellationToken.None)));
        Assert.Empty(_sender.Sent);
    }

    [Fact]
    public async Task SendAsync_WhenTheSendFails_ReleasesTheClaim_SoTheRetrySends()
    {
        var (tenantId, userId) = await TenantAAndUserAsync(TestData.AlphaUserSubject);
        await WriteCsvAsync(tenantId, new ReportingQuarter(2025, 1));
        _sender.FailNextSends = 1;

        await Assert.ThrowsAsync<IOException>(() =>
            RunAsync(job => job.SendAsync(tenantId, userId, 2025, 1, CancellationToken.None)));
        await RunAsync(job => job.SendAsync(tenantId, userId, 2025, 1, CancellationToken.None));

        Assert.Single(_sender.Sent);
        Assert.Equal(EmailDeliveryStatus.Sent, (await DeliveryAsync(tenantId, userId, "2025-Q1")).Status);
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
            RunAsync(job => job.SendAsync(tenantId, userId, 2025, 2, CancellationToken.None)));

        Assert.Empty(_sender.Sent);
    }

    [Fact]
    public async Task SendAsync_TakesOverAnExpiredClaim_AndSendsOnce()
    {
        // An expired claim means a job crashed mid-send.
        var (tenantId, userId) = await TenantAAndUserAsync(TestData.AlphaUserSubject);
        await WriteCsvAsync(tenantId, new ReportingQuarter(2025, 3));
        await InsertClaimAsync(tenantId, userId, "2025-Q3", claimedAgo: QuarterlyReportEmailJob.ClaimLease + TimeSpan.FromMinutes(1));

        await RunAsync(job => job.SendAsync(tenantId, userId, 2025, 3, CancellationToken.None));
        await RunAsync(job => job.SendAsync(tenantId, userId, 2025, 3, CancellationToken.None));

        var email = Assert.Single(_sender.Sent);
        Assert.Equal($"quarterly-report.2025-Q3.t{tenantId}.u{userId}", email.IdempotencyKey);
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

    private Task RunAsync(Func<QuarterlyReportEmailJob, Task> action, bool enabled = true) =>
        factory.WithDbAsync(async db =>
        {
            await action(CreateJob(db, enabled));
            return 0;
        });

    private QuarterlyReportEmailJob CreateJob(ReportingDbContext db, bool enabled) =>
        new(
            new ReportRecipientRepository(db),
            new TenantRepository(db),
            new EmailDeliveryRepository(db),
            _sender,
            CreateCsvWriter(),
            _jobs,
            Options.Create(new QuarterlyReportEmailOptions { Enabled = enabled }),
            TimeProvider.System,
            NullLogger<QuarterlyReportEmailJob>.Instance);

    private QuarterlyReportCsvWriter CreateCsvWriter() =>
        new(
            Options.Create(new QuarterlyReportOutputOptions { Directory = _outputDirectory }),
            factory.Services.GetRequiredService<IHostEnvironment>());

    private Task<string> WriteCsvAsync(int tenantId, ReportingQuarter quarter) =>
        CreateCsvWriter().WriteAsync(tenantId, quarter, [], 0m, CancellationToken.None);

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
