using Hangfire;
using Hangfire.Storage;
using InvoiceReporting.Api.Data.Repositories;
using InvoiceReporting.Api.Reporting;
using InvoiceReporting.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace InvoiceReporting.Api.Tests.Integration;

public sealed class QuarterlyReportScheduleTests(InvoiceReportApiFactory factory) : IClassFixture<InvoiceReportApiFactory>
{
    [Fact]
    public void Startup_RegistersQuarterlyJobAtMidnightUtc()
    {
        _ = factory.CreateClient();

        using var connection = factory.Services.GetRequiredService<JobStorage>().GetConnection();
        var job = Assert.Single(connection.GetRecurringJobs(), j => j.Id == QuarterlyReportScheduler.RecurringJobId);

        Assert.Equal("0 0 1 1,4,7,10 *", job.Cron);
        Assert.Equal(TimeZoneInfo.Utc.Id, job.TimeZoneId);
        Assert.Equal(typeof(QuarterlyReportScheduler), job.Job.Type);
    }

    [Fact]
    public async Task QueueAllTenantsAsync_QueuesOneReportPerTenant_ThenTheTempFileCleanup()
    {
        var jobs = new FakeBackgroundJobClient();

        await factory.WithDbAsync(async db =>
        {
            await new QuarterlyReportScheduler(new TenantRepository(db), jobs, TimeProvider.System)
                .QueueAllTenantsAsync(CancellationToken.None);
            return 0;
        });

        var tenantCount = await factory.WithDbAsync(db => db.Tenants.CountAsync());
        Assert.Equal(tenantCount + 1, jobs.Created.Count);
        Assert.All(jobs.Created[..^1], job => Assert.Equal(nameof(QuarterlyReportJob.RunAsync), job.Method.Name));

        // The cleanup is queued last, only after every tenant.
        var cleanup = jobs.Created[^1];
        Assert.Equal(typeof(QuarterlyReportTempFileCleanupJob), cleanup.Type);
        Assert.Equal(nameof(QuarterlyReportTempFileCleanupJob.Run), cleanup.Method.Name);
        var giveUpAt = Assert.IsType<DateTimeOffset>(cleanup.Args[0]);
        Assert.InRange(
            giveUpAt - DateTimeOffset.UtcNow,
            QuarterlyReportTempFileCleanupJob.MaxWait - TimeSpan.FromMinutes(1),
            QuarterlyReportTempFileCleanupJob.MaxWait);
    }

    [Fact]
    public async Task QueueAllTenantsAsync_WhenCancelled_Throws_AndQueuesNothing()
    {
        var jobs = new FakeBackgroundJobClient();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            factory.WithDbAsync(async db =>
            {
                await new QuarterlyReportScheduler(new TenantRepository(db), jobs, TimeProvider.System)
                    .QueueAllTenantsAsync(cts.Token);
                return 0;
            }));

        Assert.Empty(jobs.Created);
    }
}
