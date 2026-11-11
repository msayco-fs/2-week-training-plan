using Hangfire;
using Hangfire.Common;
using Hangfire.Storage.Monitoring;

namespace InvoiceReporting.Api.Reporting;

/// <summary>Tells whether quarterly report work is still waiting or running in Hangfire.</summary>
public interface IQuarterlyReportActivity
{
    /// <summary>
    /// True while a scheduler run or a report job is processing, queued, or
    /// waiting for a retry. Report runs started by the admin endpoint run
    /// inside the HTTP request, not in Hangfire, so they are not seen here.
    /// </summary>
    bool HasPendingReportWork();
}

/// <summary>Reads Hangfire's monitoring API, the same data the dashboard shows.</summary>
public sealed class HangfireQuarterlyReportActivity(JobStorage storage) : IQuarterlyReportActivity
{
    private const int PageSize = 500;

    // Only these jobs write report temp files. Email jobs are not waited for.
    private static readonly Type[] ReportJobTypes = [typeof(QuarterlyReportScheduler), typeof(QuarterlyReportJob)];

    public bool HasPendingReportWork()
    {
        var monitoring = storage.GetMonitoringApi();

        return AnyReportJob(from => monitoring.ProcessingJobs(from, PageSize), dto => dto.Job, monitoring.ProcessingCount())
            || AnyReportJob(from => monitoring.ScheduledJobs(from, PageSize), dto => dto.Job, monitoring.ScheduledCount())
            || monitoring.Queues().Any(queue =>
                AnyReportJob(from => monitoring.EnqueuedJobs(queue.Name, from, PageSize), dto => dto.Job, queue.Length));
    }

    private static bool AnyReportJob<TDto>(Func<int, JobList<TDto>> getPage, Func<TDto, Job?> getJob, long total)
    {
        for (var from = 0; from < total; from += PageSize)
        {
            var page = getPage(from);
            if (page.Count == 0)
            {
                return false;
            }

            // Job is null when Hangfire can't load the job's type; that is not report work.
            if (page.Any(entry => getJob(entry.Value) is { } job && ReportJobTypes.Contains(job.Type)))
            {
                return true;
            }
        }

        return false;
    }
}
