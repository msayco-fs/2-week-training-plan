using InvoiceReporting.Api.Data;
using InvoiceReporting.Api.Data.Repositories;
using InvoiceReporting.Api.Domain;
using InvoiceReporting.Api.Reporting;
using InvoiceReporting.Api.Tests.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace InvoiceReporting.Api.Tests.Integration;

/// <summary>
/// Runs several send jobs for the same email at the same time, as happens
/// when the CSV job re-queues recipients on a retry.
/// </summary>
/// <remarks>
/// Uses a temporary SQLite file, not the shared in-memory connection used by
/// other tests. One SQLite connection must not be used by two threads at
/// once. With a file, each job opens its own connection, and SQLite lets one
/// writer in at a time while the others wait, like a real database server.
/// </remarks>
public sealed class QuarterlyReportEmailConcurrencyTests : IDisposable
{
    private const int ConcurrentJobs = 8;

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "invoice-reporting-tests", Guid.NewGuid().ToString("N"));

    private readonly DbContextOptions<ReportingDbContext> _dbOptions;
    private readonly FakeReportEmailSender _sender = new();

    public QuarterlyReportEmailConcurrencyTests()
    {
        Directory.CreateDirectory(_directory);

        // Pooling off, so no connection keeps the file open after the test.
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(_directory, "reporting.db"),
            Pooling = false,
        }.ToString();
        _dbOptions = new DbContextOptionsBuilder<ReportingDbContext>().UseSqlite(connectionString).Options;

        using var db = new ReportingDbContext(_dbOptions);
        db.Database.EnsureCreated();
        TestData.Seed(db);
    }

    [Fact]
    public async Task ManyJobsForTheSameEmailAtOnce_SendExactlyOneEmail()
    {
        var (tenantId, userId) = await TenantAAndUserAsync();
        await CreateCsvWriter().WriteAsync(tenantId, new ReportingQuarter(2026, 3), [], 0m, CancellationToken.None);

        // A slow send keeps the winner's claim in the Sending state while the
        // other jobs try to claim it.
        _sender.Delay = TimeSpan.FromMilliseconds(300);

        using var start = new Barrier(ConcurrentJobs);
        var jobs = Enumerable.Range(0, ConcurrentJobs).Select(_ => Task.Run(async () =>
        {
            start.SignalAndWait(); // Release all jobs together.
            await using var db = new ReportingDbContext(_dbOptions);
            await CreateJob(db).SendAsync(tenantId, userId, 2026, 3, null, CancellationToken.None);
        }));

        var outcomes = await Task.WhenAll(jobs.Select(CaptureAsync));

        Assert.Single(_sender.Sent);

        // Losers either saw the email still in progress (Hangfire retries
        // them later) or already sent (they return). Nothing else may fail.
        Assert.All(outcomes.OfType<Exception>(), ex => Assert.IsType<InvalidOperationException>(ex));

        await using var check = new ReportingDbContext(_dbOptions);
        var delivery = await check.QuarterlyReportEmailDeliveries.SingleAsync();
        Assert.Equal(EmailDeliveryStatus.Sent, delivery.Status);
        Assert.Equal(1, await check.AuditLogs.CountAsync(a => a.Action == AuditActions.EmailQuarterlyInvoiceReport));

        // A Hangfire retry of a loser now finds the email sent and does nothing.
        await using (var retryDb = new ReportingDbContext(_dbOptions))
        {
            await CreateJob(retryDb).SendAsync(tenantId, userId, 2026, 3, null, CancellationToken.None);
        }

        Assert.Single(_sender.Sent);
    }

    private static async Task<Exception?> CaptureAsync(Task task)
    {
        try
        {
            await task;
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    private QuarterlyReportEmailJob CreateJob(ReportingDbContext db) =>
        new(
            new ReportRecipientRepository(db),
            new TenantRepository(db),
            new EmailDeliveryRepository(db),
            new AuditLogRepository(db),
            _sender,
            CreateCsvWriter(),
            new FailedReportEmailLog(NullLoggerFactory.Instance),
            new FakeBackgroundJobClient(),
            Options.Create(new QuarterlyReportEmailOptions { Enabled = true }),
            TimeProvider.System,
            NullLogger<QuarterlyReportEmailJob>.Instance);

    private QuarterlyReportCsvWriter CreateCsvWriter() =>
        new(
            Options.Create(new QuarterlyReportOutputOptions { Directory = Path.Combine(_directory, "reports") }),
            new HostingEnvironment { ContentRootPath = _directory });

    private async Task<(int TenantId, int UserId)> TenantAAndUserAsync()
    {
        await using var db = new ReportingDbContext(_dbOptions);
        var tenantId = await db.Tenants.Where(t => t.Key == TestData.TenantAKey).Select(t => t.Id).SingleAsync();
        var userId = await db.Users.Where(u => u.Subject == TestData.AlphaUserSubject).Select(u => u.Id).SingleAsync();
        return (tenantId, userId);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
