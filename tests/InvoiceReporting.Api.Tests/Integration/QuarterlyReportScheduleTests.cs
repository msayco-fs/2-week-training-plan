using Hangfire;
using Hangfire.Storage;
using InvoiceReporting.Api.Reporting;
using InvoiceReporting.Api.Tests.Infrastructure;
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
}
