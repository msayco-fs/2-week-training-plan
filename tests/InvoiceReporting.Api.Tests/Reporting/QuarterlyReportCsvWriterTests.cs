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
