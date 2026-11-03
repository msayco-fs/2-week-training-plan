using System.Globalization;
using System.Text.RegularExpressions;
using InvoiceReporting.Api.Reporting;
using Microsoft.Extensions.Logging;

namespace InvoiceReporting.Api.Tests.Reporting;

public sealed class FailedReportEmailLogFileTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "invoice-reporting-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Record_WritesOneLineInTheSupportFormat_ToTodaysFile()
    {
        using (var loggers = CreateLoggerFactory())
        {
            new FailedReportEmailLog(loggers).Record(12, 345, new ReportingQuarter(2026, 3), "rejected:http-400:mj-0013", "8812");
        }

        var file = Assert.Single(Directory.GetFiles(_directory));
        // Named after the UTC date, like the timestamps inside.
        Assert.Equal(
            $"failed-report-emails-{DateTime.UtcNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}.log",
            Path.GetFileName(file));

        var line = Assert.Single(File.ReadAllLines(file));
        Assert.Matches(
            new Regex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z FAILED tenant=12 user=345 quarter=2026-Q3 reason=rejected:http-400:mj-0013 job=8812$"),
            line);
    }

    [Fact]
    public void Record_StampsTheLineInUtc()
    {
        var before = DateTime.UtcNow;
        using (var loggers = CreateLoggerFactory())
        {
            new FailedReportEmailLog(loggers).Record(1, 2, new ReportingQuarter(2026, 1), ReportEmailFailureReasons.TooLarge, null);
        }

        var line = File.ReadAllLines(Assert.Single(Directory.GetFiles(_directory))).Single();
        var stamp = DateTime.ParseExact(
            line[..24], "yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

        Assert.InRange(stamp, before.AddSeconds(-1), DateTime.UtcNow.AddSeconds(1));
        Assert.EndsWith("reason=too-large job=-", line);
    }

    [Fact]
    public void OtherCategories_NeverReachTheFile()
    {
        using (var loggers = CreateLoggerFactory())
        {
            loggers.CreateLogger("InvoiceReporting.Api.Reporting.QuarterlyReportEmailJob")
                .LogError("Something else went wrong for alpha.user@example.com");
            loggers.CreateLogger("Microsoft.AspNetCore.Hosting").LogWarning("Host warning");
        }

        Assert.False(
            Directory.Exists(_directory) && Directory.GetFiles(_directory).Length > 0,
            "Only the failed-email category may be written to the file.");
    }

    [Fact]
    public void DeleteExpiredFiles_KeepsTheRetentionWindow_ByTheUtcDateInTheName()
    {
        Directory.CreateDirectory(_directory);
        var today = new DateOnly(2026, 11, 3);
        string Create(string name)
        {
            File.WriteAllText(Path.Combine(_directory, name), "x");
            return name;
        }

        var kept = new[]
        {
            Create(FailedReportEmailLogFile.FileNameFor(today)),
            Create(FailedReportEmailLogFile.FileNameFor(today.AddDays(-89))), // Oldest day of 90.
            Create("failed-report-emails-notadate.log"), // Not ours to judge.
            Create("other-20200101.log"),
        };
        Create(FailedReportEmailLogFile.FileNameFor(today.AddDays(-90)));
        Create(FailedReportEmailLogFile.FileNameFor(today.AddDays(-400)));

        FailedReportEmailLogFile.DeleteExpiredFiles(_directory, today, retentionDays: 90);

        Assert.Equal(kept.Order(), Directory.GetFiles(_directory).Select(Path.GetFileName).Order());
    }

    [Fact]
    public void DeleteExpiredFiles_WhenTheFolderIsMissing_DoesNotThrow()
    {
        FailedReportEmailLogFile.DeleteExpiredFiles(Path.Combine(_directory, "missing"), new DateOnly(2026, 11, 3), 90);
    }

    [Fact]
    public void FirstWriteOfTheDay_DeletesExpiredFiles()
    {
        Directory.CreateDirectory(_directory);
        var expired = Path.Combine(
            _directory, FailedReportEmailLogFile.FileNameFor(DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-200)));
        File.WriteAllText(expired, "old");

        using (var loggers = CreateLoggerFactory())
        {
            new FailedReportEmailLog(loggers).Record(1, 2, new ReportingQuarter(2026, 1), ReportEmailFailureReasons.TooLarge, null);
        }

        Assert.False(File.Exists(expired));
    }

    [Fact]
    public void CheckDirectory_CreatesTheFolder_AndReportsNoError()
    {
        var folder = Path.Combine(_directory, "nested", "logs");

        Assert.Null(FailedReportEmailLogFile.CheckDirectory(folder));
        Assert.True(Directory.Exists(folder));
        Assert.Empty(Directory.GetFiles(folder)); // The write probe is removed.
    }

    [Fact]
    public void CheckDirectory_WhenThePathIsAFile_ReturnsTheErrorInsteadOfThrowing()
    {
        Directory.CreateDirectory(_directory);
        var file = Path.Combine(_directory, "not-a-folder");
        File.WriteAllText(file, "x");

        Assert.NotNull(FailedReportEmailLogFile.CheckDirectory(file));
    }

    [Fact]
    public void ResolveDirectory_IsRelativeToTheContentRoot_UnlessAbsolute()
    {
        var root = Path.Combine(_directory, "app");

        Assert.Equal(Path.Combine(root, "logs"), FailedReportEmailLogFile.ResolveDirectory(root, "logs"));
        Assert.Equal(Path.Combine(_directory, "elsewhere"), FailedReportEmailLogFile.ResolveDirectory(root, Path.Combine(_directory, "elsewhere")));
    }

    // Same provider and filter as the app, with the general minimum level the app uses.
    private ILoggerFactory CreateLoggerFactory() =>
        LoggerFactory.Create(logging => logging
            .SetMinimumLevel(LogLevel.Information)
            .AddFailedReportEmailLogFile(_directory, retentionDays: 90));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
