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

    /// <summary>Writes the file and returns its full path. An existing file for the same quarter is replaced.</summary>
    public async Task<string> WriteAsync(
        int tenantId,
        ReportingQuarter quarter,
        IReadOnlyList<InvoiceSummary> invoices,
        decimal total,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(OutputDirectory);
        var path = GetPath(tenantId, quarter);

        var csv = new StringBuilder();
        csv.AppendLine("InvoiceId,CustomerName,IssuedDate,Amount");
        foreach (var invoice in invoices)
        {
            csv.Append(invoice.Id.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(Escape(invoice.CustomerName)).Append(',')
                .Append(invoice.IssuedDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append(',')
                .AppendLine(invoice.Amount.ToString("0.00", CultureInfo.InvariantCulture));
        }

        csv.Append("Total,,,").AppendLine(total.ToString("0.00", CultureInfo.InvariantCulture));

        // Write to a temporary file first, so a crash never leaves a half-written report.
        var tempPath = path + ".tmp";
        try
        {
            await File.WriteAllTextAsync(tempPath, csv.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), cancellationToken);
            File.Move(tempPath, path, overwrite: true);
        }
        catch
        {
            // Cancelled or failed: don't leave a partial temp file behind.
            // The original error (including OperationCanceledException) is rethrown.
            TryDelete(tempPath);
            throw;
        }

        return path;
    }

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
