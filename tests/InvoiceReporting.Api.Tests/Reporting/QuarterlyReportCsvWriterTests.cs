using System.Runtime.CompilerServices;
using InvoiceReporting.Api.Reporting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace InvoiceReporting.Api.Tests.Reporting;

public sealed class QuarterlyReportCsvWriterTests : IDisposable
{
    private readonly string _outputDirectory =
        Path.Combine(Path.GetTempPath(), "invoice-reporting-tests", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("Smith, Jones & Co", "\"Smith, Jones & Co\"")]
    [InlineData("The \"Best\" Cars", "\"The \"\"Best\"\" Cars\"")]
    [InlineData("=HYPERLINK(\"x\")", "\"'=HYPERLINK(\"\"x\"\")\"")]
    [InlineData("-5+3", "'-5+3")]
    [InlineData("@SUM", "'@SUM")]
    public async Task WriteAsync_EscapesCustomerName(string customerName, string expectedCell)
    {
        var writer = CreateWriter();
        var invoice = new InvoiceSummary(7, customerName, 12.5m, new DateOnly(2026, 2, 3));

        var report = await writer.WriteAsync(1, new ReportingQuarter(2026, 1), new[] { invoice }.ToAsyncEnumerable(), CancellationToken.None);

        var lines = await File.ReadAllLinesAsync(report.Path);
        Assert.Equal($"7,{expectedCell},2026-02-03,12.50", lines[1]);
    }

    [Fact]
    public async Task WriteAsync_WritesEveryRowInOrder_AndReturnsTheCountAndTotal()
    {
        var writer = CreateWriter();
        var invoices = new[]
        {
            new InvoiceSummary(1, "First", 10.25m, new DateOnly(2026, 4, 1)),
            new InvoiceSummary(2, "Second", 0.75m, new DateOnly(2026, 5, 2)),
            new InvoiceSummary(3, "Third", 100m, new DateOnly(2026, 6, 3)),
        };

        var report = await writer.WriteAsync(1, new ReportingQuarter(2026, 2), invoices.ToAsyncEnumerable(), CancellationToken.None);

        Assert.Equal(3, report.InvoiceCount);
        Assert.Equal(111.00m, report.Total);
        Assert.Equal(
            [
                "InvoiceId,CustomerName,IssuedDate,Amount",
                "1,First,2026-04-01,10.25",
                "2,Second,2026-05-02,0.75",
                "3,Third,2026-06-03,100.00",
                "Total,,,111.00",
            ],
            await File.ReadAllLinesAsync(report.Path));
    }

    /// <summary>
    /// A folder sits where the report file should go, so the final move
    /// fails after the temp file is written. The temp file must not be left behind.
    /// </summary>
    [Fact]
    public async Task WriteAsync_WhenTheFinalMoveFails_RemovesTheTempFile_AndThrows()
    {
        var writer = CreateWriter();
        var quarter = new ReportingQuarter(2026, 2);
        var path = writer.GetPath(1, quarter);
        Directory.CreateDirectory(path);

        // Windows reports UnauthorizedAccessException, Linux an IOException.
        var error = await Assert.ThrowsAnyAsync<Exception>(() => writer.WriteAsync(1, quarter, Empty, CancellationToken.None));

        Assert.True(error is IOException or UnauthorizedAccessException, $"Unexpected {error.GetType().Name}");
        Assert.Empty(TempFiles());
    }

    [Fact]
    public async Task WriteAsync_WithACancelledToken_Throws_AndLeavesNoFiles()
    {
        var writer = CreateWriter();
        var quarter = new ReportingQuarter(2026, 3);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writer.WriteAsync(1, quarter, Empty, cts.Token));

        var path = writer.GetPath(1, quarter);
        Assert.False(File.Exists(path));
        Assert.Empty(TempFiles());
    }

    /// <summary>
    /// The invoice stream fails part way (for example, the database
    /// connection drops). The older report for the quarter must be kept
    /// unchanged and no temp file left behind.
    /// </summary>
    [Fact]
    public async Task WriteAsync_WhenTheInvoiceStreamFails_KeepsTheOlderReport_AndRemovesTheTempFile()
    {
        var writer = CreateWriter();
        var quarter = new ReportingQuarter(2026, 4);
        var older = await writer.WriteAsync(1, quarter, Empty, CancellationToken.None);
        var olderText = await File.ReadAllTextAsync(older.Path);

        await Assert.ThrowsAsync<InvalidOperationException>(() => writer.WriteAsync(1, quarter, FailAfterOneRow(), CancellationToken.None));

        Assert.Equal(olderText, await File.ReadAllTextAsync(older.Path));
        Assert.Empty(TempFiles());
    }

    /// <summary>
    /// Two writes for the same tenant and quarter at once. The first is held
    /// with its temp file open while the second runs start to finish. Both
    /// must succeed, the report must be complete, and no temp file may be left.
    /// </summary>
    [Fact]
    public async Task WriteAsync_TwoWritesForTheSameQuarterAtOnce_BothSucceed_AndTheReportIsComplete()
    {
        var writer = CreateWriter();
        var quarter = new ReportingQuarter(2025, 1);
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = writer.WriteAsync(
            1, quarter, Held(firstStarted, releaseFirst, new InvoiceSummary(1, "First", 1m, new DateOnly(2025, 1, 2))),
            CancellationToken.None);
        QuarterlyReportCsvFile second;
        try
        {
            // The first write now holds its temp file open.
            await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            second = await writer.WriteAsync(
                1, quarter, new[] { new InvoiceSummary(2, "Second", 2m, new DateOnly(2025, 2, 3)) }.ToAsyncEnumerable(),
                CancellationToken.None);
        }
        finally
        {
            // Always release, so a failure never leaves the first write (and its open file) stuck.
            releaseFirst.TrySetResult();
        }

        var firstReport = await first;

        Assert.Equal(second.Path, firstReport.Path);
        // The first write moved its file in last, so its rows are the final report.
        Assert.Equal(
            ["InvoiceId,CustomerName,IssuedDate,Amount", "1,First,2025-01-02,1.00", "Total,,,1.00"],
            await File.ReadAllLinesAsync(firstReport.Path));
        Assert.Empty(TempFiles());
    }

    /// <summary>
    /// Only report temp files older than the cut-off are deleted. A recent temp
    /// file (a write still in progress), the report itself and other .tmp files stay.
    /// </summary>
    [Fact]
    public void DeleteStaleTempFiles_DeletesOnlyOldReportTempFiles()
    {
        var writer = CreateWriter();
        var now = DateTime.UtcNow;
        var oldTemp = CreateFile($"2026-Q1-tenant-1.csv.{Guid.NewGuid():N}.tmp", now.AddDays(-2));
        var oldLegacyTemp = CreateFile("2026-Q1-tenant-2.csv.tmp", now.AddDays(-2));
        var activeTemp = CreateFile($"2026-Q1-tenant-3.csv.{Guid.NewGuid():N}.tmp", now);
        var report = CreateFile("2026-Q1-tenant-1.csv", now.AddDays(-2));
        var otherTemp = CreateFile("notes.tmp", now.AddDays(-2));

        var deleted = writer.DeleteStaleTempFiles(now.AddDays(-1), CancellationToken.None);

        Assert.Equal(2, deleted);
        Assert.False(File.Exists(oldTemp));
        Assert.False(File.Exists(oldLegacyTemp));
        Assert.True(File.Exists(activeTemp));
        Assert.True(File.Exists(report));
        Assert.True(File.Exists(otherTemp));
    }

    [Fact]
    public void DeleteStaleTempFiles_WhenTheFolderDoesNotExist_DeletesNothing()
    {
        Assert.Equal(0, CreateWriter().DeleteStaleTempFiles(DateTimeOffset.UtcNow, CancellationToken.None));
    }

    private static IAsyncEnumerable<InvoiceSummary> Empty => AsyncEnumerable.Empty<InvoiceSummary>();

    private string CreateFile(string name, DateTime lastWriteUtc)
    {
        Directory.CreateDirectory(_outputDirectory);
        var path = Path.Combine(_outputDirectory, name);
        File.WriteAllText(path, "x");
        File.SetLastWriteTimeUtc(path, lastWriteUtc);
        return path;
    }

    private string[] TempFiles() =>
        Directory.Exists(_outputDirectory) ? Directory.GetFiles(_outputDirectory, "*.tmp") : [];

    /// <summary>Signals <paramref name="started"/>, waits for <paramref name="release"/>, then yields one invoice.</summary>
    private static async IAsyncEnumerable<InvoiceSummary> Held(
        TaskCompletionSource started, TaskCompletionSource release, InvoiceSummary invoice,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        started.TrySetResult();
        await release.Task.WaitAsync(cancellationToken);
        yield return invoice;
    }

    private static async IAsyncEnumerable<InvoiceSummary> FailAfterOneRow([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield return new InvoiceSummary(1, "First", 1m, new DateOnly(2026, 10, 1));
        await Task.Yield();
        throw new InvalidOperationException("Connection lost.");
    }

    private QuarterlyReportCsvWriter CreateWriter() =>
        new(Options.Create(new QuarterlyReportOutputOptions { Directory = _outputDirectory }), new FakeEnvironment());

    public void Dispose()
    {
        if (Directory.Exists(_outputDirectory))
        {
            Directory.Delete(_outputDirectory, recursive: true);
        }
    }

    private sealed class FakeEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "Tests";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}
