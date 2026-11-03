using System.Globalization;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Extensions.Logging;

namespace InvoiceReporting.Api.Reporting;

/// <summary>
/// Writes the <see cref="FailedReportEmailLog"/> category to a daily file,
/// <c>failed-report-emails-YYYYMMDD.log</c>, for support to read. Other log
/// categories never reach this file, and console logging is unchanged.
/// Everything uses UTC: the time on each line, the date in the file name, and
/// the age used to delete old files.
/// </summary>
/// <remarks>
/// The folder sits outside wwwroot and the app serves no static files, so the
/// files are never reachable over HTTP. Do not move the folder under wwwroot.
/// </remarks>
public static class FailedReportEmailLogFile
{
    private const string FileNamePrefix = "failed-report-emails-";
    private const string FileNameExtension = ".log";
    private const string DateFormat = "yyyyMMdd";

    private const string UtcTimestampProperty = "UtcTimestamp";
    private const string UtcDateProperty = "UtcDate";

    // One line per failure, timestamp in UTC.
    private const string OutputTemplate = "{UtcTimestamp:yyyy-MM-dd'T'HH:mm:ss.fff'Z'} {Message:lj}{NewLine}";

    /// <summary>The file name for one UTC day, for example <c>failed-report-emails-20261103.log</c>.</summary>
    public static string FileNameFor(DateOnly utcDate) =>
        FileNamePrefix + utcDate.ToString(DateFormat, CultureInfo.InvariantCulture) + FileNameExtension;

    /// <summary>
    /// Resolves <see cref="QuarterlyReportEmailOptions.FailedLogDirectory"/>
    /// against the content root (the backend project folder, or <c>/app</c> in
    /// the container). An absolute setting is used as it is.
    /// </summary>
    public static string ResolveDirectory(string contentRootPath, string failedLogDirectory) =>
        Path.GetFullPath(Path.Combine(contentRootPath, failedLogDirectory));

    /// <summary>Adds the file as an extra logging provider that only accepts the failed-email category.</summary>
    public static ILoggingBuilder AddFailedReportEmailLogFile(this ILoggingBuilder logging, string directory, int retentionDays)
    {
        // Serilog's own daily rolling names files by local date, so each UTC
        // day is routed to its own plain file instead. A new day opens a new
        // file, closes the previous one and deletes expired files.
        var fileLogger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .Enrich.With<UtcEnricher>()
            .WriteTo.Map(
                UtcDateProperty,
                defaultKey: string.Empty,
                (utcDate, sink) =>
                {
                    var date = DateOnly.ParseExact(utcDate, DateFormat, CultureInfo.InvariantCulture);
                    DeleteExpiredFiles(directory, date, retentionDays);
                    sink.File(
                        Path.Combine(directory, FileNameFor(date)),
                        outputTemplate: OutputTemplate,
                        formatProvider: CultureInfo.InvariantCulture,
                        // Several Hangfire workers, or app instances on one volume, can write at once.
                        shared: true);
                },
                sinkMapCountLimit: 1)
            .CreateLogger();

        // Registered with a factory, not AddProvider(instance): DI disposes
        // only what it creates, and disposing closes and flushes the file.
        logging.Services.AddSingleton<ILoggerProvider>(_ => new SerilogLoggerProvider(fileLogger, dispose: true));

        // A provider rule wins over the app's general Logging:LogLevel rules,
        // so this alone decides what reaches the file.
        logging.AddFilter<SerilogLoggerProvider>(
            (category, _) => category == FailedReportEmailLog.Category);

        return logging;
    }

    /// <summary>
    /// Creates the folder and checks it can be written. Returns the error, or
    /// null when the folder is usable. Never throws: the audit log still
    /// records failures when the file can't be written.
    /// </summary>
    public static Exception? CheckDirectory(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, $".write-check-{Guid.NewGuid():N}");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    /// <summary>
    /// Deletes log files whose UTC date in the name is <paramref name="retentionDays"/>
    /// or more days before <paramref name="todayUtc"/>. With 90 days, today and
    /// the 89 days before it are kept. Files with other names are never touched.
    /// Never throws: a file that can't be deleted is tried again the next day.
    /// </summary>
    public static void DeleteExpiredFiles(string directory, DateOnly todayUtc, int retentionDays)
    {
        try
        {
            var oldestKept = todayUtc.AddDays(-(retentionDays - 1));
            foreach (var path in Directory.EnumerateFiles(directory, FileNamePrefix + "*" + FileNameExtension))
            {
                var name = Path.GetFileName(path);
                var datePart = name[FileNamePrefix.Length..^FileNameExtension.Length];
                if (DateOnly.TryParseExact(datePart, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var fileDate)
                    && fileDate < oldestKept)
                {
                    File.Delete(path);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The folder check at startup reports permission problems.
        }
    }

    /// <summary>Serilog stamps events in local time; the file and its name use UTC.</summary>
    private sealed class UtcEnricher : ILogEventEnricher
    {
        public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
        {
            var utc = logEvent.Timestamp.UtcDateTime;
            logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty(UtcTimestampProperty, utc));
            logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty(
                UtcDateProperty, utc.ToString(DateFormat, CultureInfo.InvariantCulture)));
        }
    }
}
