using InvoiceReporting.Api.Data.Repositories;
using InvoiceReporting.Api.Reporting;
using InvoiceReporting.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace InvoiceReporting.Api.Tests.Integration;

public sealed class QuarterlyReportJobTests(InvoiceReportApiFactory factory)
    : IClassFixture<InvoiceReportApiFactory>, IDisposable
{
    private readonly string _outputDirectory =
        Path.Combine(Path.GetTempPath(), "invoice-reporting-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task RunAsync_WritesCsvWithEveryInvoiceInTheQuarter_AndTotal()
    {
        var tenantId = await factory.WithDbAsync(db =>
            db.Tenants.Where(t => t.Key == TestData.TenantAKey).Select(t => t.Id).SingleAsync());

        await factory.WithDbAsync(async db =>
        {
            await CreateJob(db).RunAsync(tenantId, 2026, 3, CancellationToken.None);
            return 0;
        });

        var path = Path.Combine(_outputDirectory, $"2026-Q3-tenant-{tenantId}.csv");
        var lines = await File.ReadAllLinesAsync(path);

        // Tenant A, Q3 2026: August row plus the three September rows, in date
        // order. The October row belongs to Q4 and must be excluded.
        Assert.Equal("InvoiceId,CustomerName,IssuedDate,Amount", lines[0]);
        Assert.Equal(
            ["A-Customer-Aug", "A-Customer-1", "A-Customer-2", "A-Customer-3"],
            lines[1..^1].Select(l => l.Split(',')[1]));
        Assert.EndsWith(",A-Customer-Aug,2026-08-31,999.99", lines[1]);
        Assert.Equal("Total,,,1399.99", lines[^1]);
    }

    [Fact]
    public async Task RunAsync_QueuesTheEmails_BothOnTheFirstRunAndWhenAlreadyGenerated()
    {
        var tenantId = await factory.WithDbAsync(db =>
            db.Tenants.Where(t => t.Key == TestData.TenantBKey).Select(t => t.Id).SingleAsync());

        // The second run finds the audit entry and skips the CSV, but still
        // queues the emails in case the first run failed before queuing them.
        for (var run = 0; run < 2; run++)
        {
            await factory.WithDbAsync(async db =>
            {
                await CreateJob(db).RunAsync(tenantId, 2026, 2, CancellationToken.None);
                return 0;
            });
        }

        Assert.Equal(2, _jobs.Created.Count);
        Assert.All(_jobs.Created, job =>
        {
            Assert.Equal(typeof(QuarterlyReportEmailJob), job.Type);
            Assert.Equal(nameof(QuarterlyReportEmailJob.QueueRecipientsAsync), job.Method.Name);
            Assert.Equal(new object[] { tenantId, 2026, 2 }, job.Args.Take(3));
        });
    }

    [Fact]
    public async Task RunAsync_PassesTheCallersTokenToEveryRepositoryCall_AndReportsGenerated()
    {
        var tenantId = await TenantAIdAsync();
        using var cts = new CancellationTokenSource();

        var (result, invoices, audits) = await factory.WithDbAsync(async db =>
        {
            var invoices = new CapturingInvoiceRepository(new InvoiceRepository(db));
            var audits = new CapturingAuditLogRepository(new AuditLogRepository(db));
            var result = await CreateJob(db, invoices, audits).RunAsync(tenantId, 2019, 1, cts.Token);
            return (result, invoices, audits);
        });

        Assert.Equal(QuarterlyReportRunResult.Generated, result);
        Assert.Equal([cts.Token], invoices.Tokens);
        // ExistsAsync, then AddAsync.
        Assert.Equal([cts.Token, cts.Token], audits.Tokens);
        Assert.True(File.Exists(CsvPath(tenantId, "2019-Q1")));
        Assert.Single(_jobs.Created);
    }

    /// <summary>Uses the real repositories, so this also proves EF Core receives the token.</summary>
    [Fact]
    public async Task RunAsync_WithACancelledToken_Throws_AndWritesQueuesAndAuditsNothing()
    {
        var tenantId = await TenantAIdAsync();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            factory.WithDbAsync(db => CreateJob(db).RunAsync(tenantId, 2019, 2, cts.Token)));

        Assert.False(File.Exists(CsvPath(tenantId, "2019-Q2")));
        Assert.Empty(_jobs.Created);
        Assert.False(await GeneratedAuditExistsAsync(tenantId, "2019-Q2"));
    }

    [Fact]
    public async Task RunAsync_WhenCancelledAfterTheQuery_StopsBeforeTheCsvAndTheAuditEntry()
    {
        var tenantId = await TenantAIdAsync();
        using var cts = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            factory.WithDbAsync(db =>
            {
                var invoices = new CapturingInvoiceRepository(new InvoiceRepository(db), afterQuery: cts.Cancel);
                return CreateJob(db, invoices).RunAsync(tenantId, 2019, 3, cts.Token);
            }));

        Assert.False(File.Exists(CsvPath(tenantId, "2019-Q3")));
        Assert.Empty(_jobs.Created);
        Assert.False(await GeneratedAuditExistsAsync(tenantId, "2019-Q3"));
    }

    private readonly FakeBackgroundJobClient _jobs = new();

    private QuarterlyReportJob CreateJob(
        Data.ReportingDbContext db,
        IInvoiceRepository? invoices = null,
        IAuditLogRepository? audits = null)
    {
        var writer = new QuarterlyReportCsvWriter(
            Options.Create(new QuarterlyReportOutputOptions { Directory = _outputDirectory }),
            factory.Services.GetRequiredService<IHostEnvironment>());
        return new QuarterlyReportJob(
            invoices ?? new InvoiceRepository(db),
            audits ?? new AuditLogRepository(db),
            TimeProvider.System, writer, _jobs, NullLogger<QuarterlyReportJob>.Instance);
    }

    private string CsvPath(int tenantId, string quarter) => Path.Combine(_outputDirectory, $"{quarter}-tenant-{tenantId}.csv");

    private Task<int> TenantAIdAsync() =>
        factory.WithDbAsync(db => db.Tenants.Where(t => t.Key == TestData.TenantAKey).Select(t => t.Id).SingleAsync());

    private Task<bool> GeneratedAuditExistsAsync(int tenantId, string quarter) =>
        factory.WithDbAsync(db => db.AuditLogs.AnyAsync(a =>
            a.TenantId == tenantId && a.Action == Domain.AuditActions.GenerateQuarterlyInvoiceReport && a.Detail == quarter));

    /// <summary>Records the token of each call. Can run an action after the query, for example to cancel.</summary>
    private sealed class CapturingInvoiceRepository(IInvoiceRepository inner, Action? afterQuery = null) : IInvoiceRepository
    {
        public List<CancellationToken> Tokens { get; } = [];

        public async Task<IReadOnlyList<InvoiceSummary>> GetSummariesAsync(
            int tenantId, DateOnly fromInclusive, DateOnly toExclusive, CancellationToken cancellationToken)
        {
            Tokens.Add(cancellationToken);
            var result = await inner.GetSummariesAsync(tenantId, fromInclusive, toExclusive, cancellationToken);
            afterQuery?.Invoke();
            return result;
        }
    }

    private sealed class CapturingAuditLogRepository(IAuditLogRepository inner) : IAuditLogRepository
    {
        public List<CancellationToken> Tokens { get; } = [];

        public Task<bool> ExistsAsync(int tenantId, string action, string detail, CancellationToken cancellationToken)
        {
            Tokens.Add(cancellationToken);
            return inner.ExistsAsync(tenantId, action, detail, cancellationToken);
        }

        public Task AddAsync(Domain.AuditLog entry, CancellationToken cancellationToken)
        {
            Tokens.Add(cancellationToken);
            return inner.AddAsync(entry, cancellationToken);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_outputDirectory))
        {
            Directory.Delete(_outputDirectory, recursive: true);
        }
    }
}
