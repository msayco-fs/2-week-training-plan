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
        Assert.False(File.Exists(path + ".tmp"));
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
        Assert.False(File.Exists(path + ".tmp"));
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
        Assert.False(File.Exists(older.Path + ".tmp"));
    }

    private static IAsyncEnumerable<InvoiceSummary> Empty => AsyncEnumerable.Empty<InvoiceSummary>();

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
