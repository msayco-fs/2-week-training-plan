using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace InvoiceReporting.Api.Reporting;

/// <summary>
/// Writes one tenant's quarterly report as a CSV file: one row per invoice,
/// then a total row.
/// </summary>
public sealed class QuarterlyReportCsvWriter(
    IOptions<QuarterlyReportOutputOptions> options,
    IHostEnvironment environment)
{
    // Temp file names: "<report name>.<32 hex chars>.tmp". The old fixed name
    // "<report name>.tmp" also matches, so leftovers from before are removed too.
    private static readonly Regex TempFileName = new(
        @"^\d{4}-Q[1-4]-tenant-\d+\.csv(\.[0-9a-f]{32})?\.tmp$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public string OutputDirectory =>
        Path.GetFullPath(Path.Combine(environment.ContentRootPath, options.Value.Directory));

    /// <summary>Full path of one tenant's report file for a quarter, whether or not it exists yet.</summary>
    public string GetPath(int tenantId, ReportingQuarter quarter) =>
        // Tenant id, not tenant key, so the file name never contains stored text.
        Path.Combine(OutputDirectory, string.Create(CultureInfo.InvariantCulture, $"{quarter}-tenant-{tenantId}.csv"));

    /// <summary>
    /// Writes the file from <paramref name="invoices"/>, one row as each
    /// invoice arrives, then the total row. Memory use stays the same however
    /// many invoices the quarter has. An existing file for the same quarter is
    /// replaced only once the new one is complete.
    /// </summary>
    public async Task<QuarterlyReportCsvFile> WriteAsync(
        int tenantId,
        ReportingQuarter quarter,
        IAsyncEnumerable<InvoiceSummary> invoices,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(OutputDirectory);
        var path = GetPath(tenantId, quarter);

        // Summed here, in C#, like the monthly report: SQLite (used in tests)
        // cannot aggregate decimal columns.
        var count = 0;
        var total = 0m;

        // Write to a temporary file first, so a crash never leaves a half-written report.
        // Each run gets its own temp name: two runs for the same quarter at once
        // (the schedule and an admin trigger) never open or delete each other's file.
        var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using (var file = new FileStream(
                tempPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 81920, useAsync: true))
            await using (var csv = new StreamWriter(file, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)))
            {
                await csv.WriteLineAsync("InvoiceId,CustomerName,IssuedDate,Amount".AsMemory(), cancellationToken);
                await foreach (var invoice in invoices.WithCancellation(cancellationToken))
                {
                    await csv.WriteLineAsync(FormatRow(invoice).AsMemory(), cancellationToken);
                    count++;
                    total += invoice.Amount;
                }

                await csv.WriteLineAsync(("Total,,," + total.ToString("0.00", CultureInfo.InvariantCulture)).AsMemory(), cancellationToken);
                await csv.FlushAsync(cancellationToken);
            }

            File.Move(tempPath, path, overwrite: true);
        }
        catch
        {
            // Cancelled or failed, also while reading the invoices: don't leave
            // a partial temp file behind. An older report file for the same
            // quarter is kept. The original error (including
            // OperationCanceledException) is rethrown.
            _ = TryDelete(tempPath);
            throw;
        }

        return new QuarterlyReportCsvFile(path, count, total);
    }

    /// <summary>
    /// Deletes temp files left by runs that stopped without cleaning up, for
    /// example when the process was killed. Only files last written before
    /// <paramref name="writtenBeforeUtc"/> are deleted, so a write still in
    /// progress is never touched. Returns how many files were deleted.
    /// </summary>
    public int DeleteStaleTempFiles(DateTimeOffset writtenBeforeUtc, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(OutputDirectory))
        {
            return 0;
        }

        var deleted = 0;
        foreach (var file in Directory.EnumerateFiles(OutputDirectory, "*.tmp", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var info = new FileInfo(file);
            if (!TempFileName.IsMatch(info.Name) || !info.Exists || info.LastWriteTimeUtc >= writtenBeforeUtc.UtcDateTime)
            {
                continue;
            }

            if (TryDelete(file))
            {
                deleted++;
            }
        }

        return deleted;
    }

    private static string FormatRow(InvoiceSummary invoice) =>
        string.Join(',',
            invoice.Id.ToString(CultureInfo.InvariantCulture),
            Escape(invoice.CustomerName),
            invoice.IssuedDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            invoice.Amount.ToString("0.00", CultureInfo.InvariantCulture));

    private static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort. A leftover temp file has a unique name, so it never
            // blocks a later run; the stale-file cleanup removes it.
            return false;
        }
    }

    private static string Escape(string value)
    {
        // Spreadsheet apps run cells that start with these characters as
        // formulas (CSV injection). A leading apostrophe makes them plain text.
        if (value.Length > 0 && "=+-@\t\r".Contains(value[0]))
        {
            value = "'" + value;
        }

        return value.IndexOfAny([',', '"', '\r', '\n']) >= 0
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;
    }
}

/// <summary>A written report: its full path, how many invoices it lists and their total.</summary>
public sealed record QuarterlyReportCsvFile(string Path, int InvoiceCount, decimal Total);
