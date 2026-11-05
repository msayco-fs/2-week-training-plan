using Hangfire;
using Hangfire.Common;
using Hangfire.InMemory;
using Hangfire.Server;

namespace InvoiceReporting.Api.Tests.Infrastructure;

/// <summary>
/// Builds a real Hangfire <see cref="PerformContext"/> backed by in-memory
/// storage, as Hangfire passes to a job on a given retry.
/// </summary>
public static class HangfireTestContext
{
    /// <param name="retryCount">0 for the first run; Hangfire's AutomaticRetry sets 1, 2, … before each retry.</param>
    public static PerformContext Create(int retryCount)
    {
        var storage = new InMemoryStorage();
        var connection = storage.GetConnection();
        var job = Job.FromExpression(() => Console.WriteLine());
        var createdAt = DateTime.UtcNow;
        var jobId = connection.CreateExpiredJob(job, new Dictionary<string, string>(), createdAt, TimeSpan.FromHours(1));

        var context = new PerformContext(
            storage, connection, new BackgroundJob(jobId, job, createdAt), new JobCancellationToken(false));

        // Same call AutomaticRetryAttribute makes, so the value is stored the same way.
        if (retryCount > 0)
        {
            context.SetJobParameter("RetryCount", retryCount);
        }

        return context;
    }
}
