using System.Globalization;
using System.Text;
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
        var tempPath = path + ".tmp";
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
            TryDelete(tempPath);
            throw;
        }

        return new QuarterlyReportCsvFile(path, count, total);
    }

    private static string FormatRow(InvoiceSummary invoice) =>
        string.Join(',',
            invoice.Id.ToString(CultureInfo.InvariantCulture),
            Escape(invoice.CustomerName),
            invoice.IssuedDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            invoice.Amount.ToString("0.00", CultureInfo.InvariantCulture));

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort. The next run overwrites the file anyway.
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
