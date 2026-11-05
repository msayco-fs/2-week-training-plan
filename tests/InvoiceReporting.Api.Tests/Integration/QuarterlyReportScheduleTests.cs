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
    public async Task QueueAllTenantsAsync_QueuesOneReportPerTenant()
    {
        var jobs = new FakeBackgroundJobClient();

        await factory.WithDbAsync(async db =>
        {
            await new QuarterlyReportScheduler(new TenantRepository(db), jobs, TimeProvider.System)
                .QueueAllTenantsAsync(CancellationToken.None);
            return 0;
        });

        var tenantCount = await factory.WithDbAsync(db => db.Tenants.CountAsync());
        Assert.Equal(tenantCount, jobs.Created.Count);
        Assert.All(jobs.Created, job => Assert.Equal(nameof(QuarterlyReportJob.RunAsync), job.Method.Name));
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
