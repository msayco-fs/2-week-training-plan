using System.Runtime.CompilerServices;
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
        // ExistsAsync, then TryAddUniqueAsync.
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

    /// <summary>
    /// The invoices are streamed into the file. A cancel after the first row
    /// is read must stop the run mid-file, with no report file, temp file,
    /// audit entry or email job left behind.
    /// </summary>
    [Fact]
    public async Task RunAsync_WhenCancelledWhileStreamingTheInvoices_StopsBeforeTheAuditEntry_AndLeavesNoFiles()
    {
        var tenantId = await TenantAIdAsync();
        await AddInvoicesAsync(tenantId, new DateOnly(2019, 8, 1), 3);
        using var cts = new CancellationTokenSource();
        CapturingInvoiceRepository? invoices = null;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            factory.WithDbAsync(db =>
            {
                invoices = new CapturingInvoiceRepository(new InvoiceRepository(db), afterEachRow: cts.Cancel);
                return CreateJob(db, invoices).RunAsync(tenantId, 2019, 3, cts.Token);
            }));

        // Stopped before the end of the stream. Whether the second row is read
        // depends on where the cancel is first seen: by EF Core or the file write.
        Assert.InRange(invoices!.RowsRead, 1, 2);
        Assert.False(File.Exists(CsvPath(tenantId, "2019-Q3")));
        Assert.False(File.Exists(CsvPath(tenantId, "2019-Q3") + ".tmp"));
        Assert.Empty(_jobs.Created);
        Assert.False(await GeneratedAuditExistsAsync(tenantId, "2019-Q3"));
    }

    [Fact]
    public async Task RunAsync_StreamsTheQuarter_WithoutLoadingItAsAList()
    {
        var tenantId = await TenantAIdAsync();
        await AddInvoicesAsync(tenantId, new DateOnly(2017, 11, 1), 3);

        var invoices = await factory.WithDbAsync(async db =>
        {
            var invoices = new CapturingInvoiceRepository(new InvoiceRepository(db));
            await CreateJob(db, invoices).RunAsync(tenantId, 2017, 4, CancellationToken.None);
            return invoices;
        });

        Assert.Equal(0, invoices.ListCalls);
        Assert.Equal(3, invoices.RowsRead);
        var lines = await File.ReadAllLinesAsync(CsvPath(tenantId, "2017-Q4"));
        Assert.Equal(5, lines.Length); // Header, 3 rows, total.
        Assert.Equal("Total,,,6.00", lines[^1]);
    }

    /// <summary>Adds invoices of 1.00, 2.00, 3.00 … on consecutive days from <paramref name="firstDay"/>.</summary>
    private Task AddInvoicesAsync(int tenantId, DateOnly firstDay, int count) =>
        factory.WithDbAsync(async db =>
        {
            for (var i = 0; i < count; i++)
            {
                db.Invoices.Add(new Domain.Invoice
                {
                    TenantId = tenantId,
                    CustomerName = $"Stream-{firstDay:yyyyMMdd}-{i}",
                    Amount = i + 1,
                    IssuedDate = firstDay.AddDays(i),
                });
            }

            return await db.SaveChangesAsync();
        });

    /// <summary>
    /// Two runs for the same quarter at once (the schedule and an admin
    /// trigger). The second passes the "already generated" check before the
    /// first saves. The unique index must stop it recording the quarter twice.
    /// </summary>
    [Fact]
    public async Task RunAsync_WhenAnotherRunSavedTheQuarterAfterTheCheck_ReportsAlreadyGenerated_WithOneAuditEntry()
    {
        var tenantId = await TenantAIdAsync();
        await factory.WithDbAsync(db => CreateJob(db).RunAsync(tenantId, 2019, 4, CancellationToken.None));

        var result = await factory.WithDbAsync(db =>
            CreateJob(db, audits: new CapturingAuditLogRepository(new AuditLogRepository(db), missFirstCheck: true))
                .RunAsync(tenantId, 2019, 4, CancellationToken.None));

        Assert.Equal(QuarterlyReportRunResult.AlreadyGenerated, result);
        var entries = await factory.WithDbAsync(db => db.AuditLogs.CountAsync(a =>
            a.TenantId == tenantId && a.Action == Domain.AuditActions.GenerateQuarterlyInvoiceReport && a.Detail == "2019-Q4"));
        Assert.Equal(1, entries);
        // Both runs still queue the emails; each send job skips people already emailed.
        Assert.Equal(2, _jobs.Created.Count);
    }

    [Fact]
    public async Task TryAddUniqueAsync_AllowsOneGeneratedEntryPerQuarter_ButOtherActionsMayRepeat()
    {
        var tenantId = await TenantAIdAsync();

        var (first, second) = await factory.WithDbAsync(async db =>
        {
            var repository = new AuditLogRepository(db);
            return (
                await repository.TryAddUniqueAsync(Entry(tenantId, Domain.AuditActions.GenerateQuarterlyInvoiceReport, "2018-Q1"), CancellationToken.None),
                await repository.TryAddUniqueAsync(Entry(tenantId, Domain.AuditActions.GenerateQuarterlyInvoiceReport, "2018-Q1"), CancellationToken.None));
        });

        Assert.True(first);
        Assert.False(second);

        // The index is filtered to the "generated" action only.
        await factory.WithDbAsync(async db =>
        {
            var repository = new AuditLogRepository(db);
            await repository.AddAsync(Entry(tenantId, Domain.AuditActions.ViewMonthlyInvoices, "2018-01"), CancellationToken.None);
            await repository.AddAsync(Entry(tenantId, Domain.AuditActions.ViewMonthlyInvoices, "2018-01"), CancellationToken.None);
            return 0;
        });
    }

    private static Domain.AuditLog Entry(int tenantId, string action, string detail) => new()
    {
        TenantId = tenantId,
        Action = action,
        Detail = detail,
        OccurredAtUtc = DateTimeOffset.UtcNow,
    };

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

    /// <summary>
    /// Records the token of each streaming call and counts the rows read.
    /// Can run an action after each row, for example to cancel.
    /// </summary>
    private sealed class CapturingInvoiceRepository(IInvoiceRepository inner, Action? afterEachRow = null) : IInvoiceRepository
    {
        public List<CancellationToken> Tokens { get; } = [];

        public int RowsRead { get; private set; }

        public int ListCalls { get; private set; }

        public Task<IReadOnlyList<InvoiceSummary>> GetSummariesAsync(
            int tenantId, DateOnly fromInclusive, DateOnly toExclusive, CancellationToken cancellationToken)
        {
            ListCalls++;
            return inner.GetSummariesAsync(tenantId, fromInclusive, toExclusive, cancellationToken);
        }

        public async IAsyncEnumerable<InvoiceSummary> StreamSummariesAsync(
            int tenantId, DateOnly fromInclusive, DateOnly toExclusive,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Tokens.Add(cancellationToken);
            await foreach (var invoice in inner.StreamSummariesAsync(tenantId, fromInclusive, toExclusive, cancellationToken))
            {
                RowsRead++;
                yield return invoice;
                afterEachRow?.Invoke();
            }
        }
    }

    /// <summary>
    /// Records the token of each call. With <paramref name="missFirstCheck"/>,
    /// the first ExistsAsync answers false: like a second run that checked
    /// just before the first run saved its entry.
    /// </summary>
    private sealed class CapturingAuditLogRepository(IAuditLogRepository inner, bool missFirstCheck = false) : IAuditLogRepository
    {
        private bool _missed;

        public List<CancellationToken> Tokens { get; } = [];

        public Task<bool> ExistsAsync(int tenantId, string action, string detail, CancellationToken cancellationToken)
        {
            Tokens.Add(cancellationToken);
            if (missFirstCheck && !_missed)
            {
                _missed = true;
                return Task.FromResult(false);
            }

            return inner.ExistsAsync(tenantId, action, detail, cancellationToken);
        }

        public Task AddAsync(Domain.AuditLog entry, CancellationToken cancellationToken)
        {
            Tokens.Add(cancellationToken);
            return inner.AddAsync(entry, cancellationToken);
        }

        public Task<bool> TryAddUniqueAsync(Domain.AuditLog entry, CancellationToken cancellationToken)
        {
            Tokens.Add(cancellationToken);
            return inner.TryAddUniqueAsync(entry, cancellationToken);
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
