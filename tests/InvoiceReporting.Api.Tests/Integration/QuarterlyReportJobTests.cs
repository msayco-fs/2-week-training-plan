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

    private readonly FakeBackgroundJobClient _jobs = new();

    private QuarterlyReportJob CreateJob(Data.ReportingDbContext db)
    {
        var writer = new QuarterlyReportCsvWriter(
            Options.Create(new QuarterlyReportOutputOptions { Directory = _outputDirectory }),
            factory.Services.GetRequiredService<IHostEnvironment>());
        return new QuarterlyReportJob(
            new InvoiceRepository(db),
            new AuditLogRepository(db),
            TimeProvider.System, writer, _jobs, NullLogger<QuarterlyReportJob>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_outputDirectory))
        {
            Directory.Delete(_outputDirectory, recursive: true);
        }
    }
}
