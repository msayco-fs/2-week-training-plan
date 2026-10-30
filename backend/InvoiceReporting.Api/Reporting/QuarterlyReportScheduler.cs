using Hangfire;
using InvoiceReporting.Api.Data.Repositories;

namespace InvoiceReporting.Api.Reporting;

/// <summary>
/// Recurring Hangfire job. At the start of each quarter it queues one
/// <see cref="QuarterlyReportJob"/> per tenant for the quarter that just ended,
/// so a failure for one tenant does not block the others.
/// </summary>
public sealed class QuarterlyReportScheduler(
    ITenantRepository tenants,
    IBackgroundJobClient jobs,
    TimeProvider timeProvider)
{
    public const string RecurringJobId = "quarterly-invoice-report";

    /// <summary>00:00 UTC on 1 January, 1 April, 1 July and 1 October.</summary>
    public const string CronExpression = "0 0 1 1,4,7,10 *";

    // Stops a manual "Trigger now" from queuing every tenant twice while the
    // scheduled run is still going.
    [DisableConcurrentExecution(timeoutInSeconds: 600)]
    public async Task QueueAllTenantsAsync(CancellationToken cancellationToken)
    {
        var today = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);
        var quarter = ReportingQuarter.Containing(today).Previous;

        var tenantIds = await tenants.GetAllIdsAsync(cancellationToken);

        // The quarter is fixed here and passed as arguments, so a retry days
        // later still reports on the same quarter.
        foreach (var tenantId in tenantIds)
        {
            jobs.Enqueue<QuarterlyReportJob>(job =>
                job.RunAsync(tenantId, quarter.Year, quarter.Number, CancellationToken.None));
        }
    }
}
