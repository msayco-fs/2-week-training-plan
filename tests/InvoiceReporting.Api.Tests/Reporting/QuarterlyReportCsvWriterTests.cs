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
        var writer = new QuarterlyReportCsvWriter(
            Options.Create(new QuarterlyReportOutputOptions { Directory = _outputDirectory }),
            new FakeEnvironment());
        var invoice = new InvoiceSummary(7, customerName, 12.5m, new DateOnly(2026, 2, 3));

        var path = await writer.WriteAsync(1, new ReportingQuarter(2026, 1), [invoice], 12.5m, CancellationToken.None);

        var lines = await File.ReadAllLinesAsync(path);
        Assert.Equal($"7,{expectedCell},2026-02-03,12.50", lines[1]);
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
        var error = await Assert.ThrowsAnyAsync<Exception>(() => writer.WriteAsync(1, quarter, [], 0m, CancellationToken.None));

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

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writer.WriteAsync(1, quarter, [], 0m, cts.Token));

        var path = writer.GetPath(1, quarter);
        Assert.False(File.Exists(path));
        Assert.False(File.Exists(path + ".tmp"));
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
