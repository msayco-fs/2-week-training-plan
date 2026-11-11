using Hangfire;
using Hangfire.InMemory;
using Hangfire.States;
using InvoiceReporting.Api.Reporting;

namespace InvoiceReporting.Api.Tests.Integration;

/// <summary>
/// Runs the real Hangfire check against in-memory storage. No Hangfire server
/// runs, so each job stays in the state the test puts it in.
/// </summary>
public sealed class QuarterlyReportActivityTests
{
    private readonly InMemoryStorage _storage = new();

    private BackgroundJobClient Client => new(_storage);

    private HangfireQuarterlyReportActivity Activity => new(_storage);

    [Fact]
    public void HasPendingReportWork_WithNoJobs_IsFalse() =>
        Assert.False(Activity.HasPendingReportWork());

    [Fact]
    public void HasPendingReportWork_WithOnlyAnEmailJobQueued_IsFalse()
    {
        Client.Enqueue<QuarterlyReportEmailJob>(job => job.QueueRecipientsAsync(1, 2026, 1, CancellationToken.None));

        Assert.False(Activity.HasPendingReportWork());
    }

    [Fact]
    public void HasPendingReportWork_WithAReportJobQueued_IsTrue()
    {
        Client.Enqueue<QuarterlyReportJob>(job => job.RunAsync(1, 2026, 1, CancellationToken.None));

        Assert.True(Activity.HasPendingReportWork());
    }

    /// <summary>A failed report job waiting for its next retry is in the Scheduled state.</summary>
    [Fact]
    public void HasPendingReportWork_WithAReportJobWaitingForARetry_IsTrue()
    {
        Client.Schedule<QuarterlyReportJob>(job => job.RunAsync(1, 2026, 1, CancellationToken.None), TimeSpan.FromMinutes(10));

        Assert.True(Activity.HasPendingReportWork());
    }

    [Fact]
    public void HasPendingReportWork_WhileTheSchedulerIsRunning_IsTrue()
    {
        var id = Client.Enqueue<QuarterlyReportScheduler>(job => job.QueueAllTenantsAsync(CancellationToken.None));
        Assert.True(Client.ChangeState(id, new TestProcessingState()));

        Assert.True(Activity.HasPendingReportWork());
    }

    /// <summary>
    /// Hangfire's <see cref="ProcessingState"/> can only be created by a worker,
    /// so this stands in for it: same state name and data.
    /// </summary>
    private sealed class TestProcessingState : IState
    {
        public string Name => ProcessingState.StateName;
        public string? Reason => null;
        public bool IsFinal => false;
        public bool IgnoreJobLoadException => false;

        public Dictionary<string, string> SerializeData() => new()
        {
            ["StartedAt"] = Hangfire.Common.JobHelper.SerializeDateTime(DateTime.UtcNow),
            ["ServerId"] = "test-server",
            ["WorkerId"] = "worker-1",
        };
    }
}
