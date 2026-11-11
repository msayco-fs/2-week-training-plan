using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Hangfire.Storage.Monitoring;

namespace InvoiceReporting.Api.Reporting;

/// <summary>Reads quarterly report work from Hangfire.</summary>
public interface IQuarterlyReportActivity
{
    /// <summary>
    /// True while a scheduler run or a report job is processing, queued, or
    /// waiting for a retry. Includes runs queued by the admin endpoint.
    /// </summary>
    bool HasPendingReportWork();

    /// <summary>
    /// The <see cref="QuarterlyReportJob.RunAsync"/> job with this id, or null
    /// when there is no such job or it is another kind of job.
    /// </summary>
    QuarterlyReportRun? FindRun(string jobId);
}

/// <summary>One queued quarterly report run, as Hangfire last recorded it.</summary>
/// <param name="State">Hangfire's state name, such as Enqueued, Processing, Succeeded or Failed.</param>
/// <param name="Result">What the run did; only set once it has succeeded.</param>
public sealed record QuarterlyReportRun(
    string JobId, int TenantId, ReportingQuarter Quarter, string? State, QuarterlyReportRunResult? Result);

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

    public QuarterlyReportRun? FindRun(string jobId)
    {
        var details = storage.GetMonitoringApi().JobDetails(jobId);
        if (details?.Job is not { } job
            || job.Type != typeof(QuarterlyReportJob)
            || job.Method.Name != nameof(QuarterlyReportJob.RunAsync)
            || job.Args is not [int tenantId, int year, int quarterNumber, ..])
        {
            return null;
        }

        // History is newest first.
        var current = details.History?.FirstOrDefault();
        QuarterlyReportRunResult? result = null;
        if (current?.StateName == SucceededState.StateName
            && current.Data is not null
            && current.Data.TryGetValue("Result", out var serialized)
            && serialized is not null)
        {
            // Stored by Hangfire's own serializer, so read back with it.
            result = SerializationHelper.Deserialize<QuarterlyReportRunResult>(serialized, SerializationOption.User);
        }

        return new QuarterlyReportRun(jobId, tenantId, new ReportingQuarter(year, quarterNumber), current?.StateName, result);
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
