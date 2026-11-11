using Hangfire;
using Microsoft.Extensions.Options;

namespace InvoiceReporting.Api.Reporting;

/// <summary>
/// Hangfire job queued by <see cref="QuarterlyReportScheduler"/> after it has
/// queued every tenant. Waits until the scheduler and every report job have
/// finished, then deletes stale CSV temp files
/// (see <see cref="QuarterlyReportCsvWriter.DeleteStaleTempFiles"/>).
/// </summary>
public sealed class QuarterlyReportTempFileCleanupJob(
    QuarterlyReportCsvWriter csvWriter,
    IQuarterlyReportActivity reportActivity,
    IBackgroundJobClient jobs,
    IOptions<QuarterlyReportOutputOptions> options,
    TimeProvider timeProvider,
    ILogger<QuarterlyReportTempFileCleanupJob> logger)
{
    /// <summary>How long to wait before checking again while report work is pending.</summary>
    public static readonly TimeSpan CheckAgainAfter = TimeSpan.FromMinutes(5);

    /// <summary>How long after the scheduler run the cleanup keeps waiting before it gives up.</summary>
    public static readonly TimeSpan MaxWait = TimeSpan.FromHours(24);

    // Not retried: a failed cleanup is harmless, and the next quarter's run tries again.
    [AutomaticRetry(Attempts = 0)]
    public void Run(DateTimeOffset giveUpAtUtc, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();

        if (reportActivity.HasPendingReportWork())
        {
            if (now >= giveUpAtUtc)
            {
                logger.LogWarning(
                    "Skipped the quarterly report temp file cleanup: report jobs were still pending at {GiveUpAt}. The next schedule run tries again",
                    giveUpAtUtc);
                return;
            }

            // A new scheduled job, not a wait inside this one: no worker is held,
            // and the wait survives a restart.
            cancellationToken.ThrowIfCancellationRequested();
            jobs.Schedule<QuarterlyReportTempFileCleanupJob>(
                job => job.Run(giveUpAtUtc, CancellationToken.None), CheckAgainAfter);
            return;
        }

        // The age check stays: a run can start between the check above and
        // this delete.
        var writtenBefore = now - options.Value.StaleTempFileAge;
        var deleted = csvWriter.DeleteStaleTempFiles(writtenBefore, cancellationToken);

        if (deleted > 0)
        {
            logger.LogInformation(
                "Deleted {Count} stale quarterly report temp files last written before {WrittenBefore}", deleted, writtenBefore);
        }
    }
}
