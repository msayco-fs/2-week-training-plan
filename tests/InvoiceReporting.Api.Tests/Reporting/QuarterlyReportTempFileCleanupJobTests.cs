using InvoiceReporting.Api.Reporting;
using InvoiceReporting.Api.Tests.Infrastructure;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace InvoiceReporting.Api.Tests.Reporting;

public sealed class QuarterlyReportTempFileCleanupJobTests : IDisposable
{
    private readonly string _outputDirectory =
        Path.Combine(Path.GetTempPath(), "invoice-reporting-tests", Guid.NewGuid().ToString("N"));

    private readonly FakeBackgroundJobClient _jobs = new();

    [Fact]
    public void Run_WhileReportWorkIsPending_DeletesNothing_AndChecksAgainLater()
    {
        var stale = CreateStaleTempFile();
        var giveUpAt = DateTimeOffset.UtcNow.AddHours(1);

        CreateJob(pending: true).Run(giveUpAt, CancellationToken.None);

        Assert.True(File.Exists(stale));
        var next = Assert.Single(_jobs.Created);
        Assert.Equal(typeof(QuarterlyReportTempFileCleanupJob), next.Type);
        Assert.Equal(giveUpAt, next.Args[0]); // Same deadline, never extended.
    }

    [Fact]
    public void Run_WhenNoReportWorkIsPending_DeletesStaleFiles_AndQueuesNothing()
    {
        var stale = CreateStaleTempFile();

        CreateJob(pending: false).Run(DateTimeOffset.UtcNow.AddHours(1), CancellationToken.None);

        Assert.False(File.Exists(stale));
        Assert.Empty(_jobs.Created);
    }

    [Fact]
    public void Run_WhenStillPendingAtTheDeadline_GivesUp_DeletesNothing_AndQueuesNothing()
    {
        var stale = CreateStaleTempFile();

        CreateJob(pending: true).Run(DateTimeOffset.UtcNow.AddMinutes(-1), CancellationToken.None);

        Assert.True(File.Exists(stale));
        Assert.Empty(_jobs.Created);
    }

    private string CreateStaleTempFile()
    {
        Directory.CreateDirectory(_outputDirectory);
        var path = Path.Combine(_outputDirectory, $"2026-Q1-tenant-1.csv.{Guid.NewGuid():N}.tmp");
        File.WriteAllText(path, "x");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-2));
        return path;
    }

    private QuarterlyReportTempFileCleanupJob CreateJob(bool pending)
    {
        var options = Options.Create(new QuarterlyReportOutputOptions { Directory = _outputDirectory });
        return new QuarterlyReportTempFileCleanupJob(
            new QuarterlyReportCsvWriter(options, new FakeEnvironment()),
            new FakeActivity(pending), _jobs, options, TimeProvider.System,
            NullLogger<QuarterlyReportTempFileCleanupJob>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_outputDirectory))
        {
            Directory.Delete(_outputDirectory, recursive: true);
        }
    }

    private sealed class FakeActivity(bool pending) : IQuarterlyReportActivity
    {
        public bool HasPendingReportWork() => pending;
    }

    private sealed class FakeEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "Tests";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}
