using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using InvoiceReporting.Api.Reporting;

namespace InvoiceReporting.Api.Tests.Reporting;

public sealed class QuarterlyReportZipTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "invoice-reporting-tests", Guid.NewGuid().ToString("N"));

    public QuarterlyReportZipTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task CreateAsync_ReturnsAZipWithTheCsvAsItsOnlyEntry_UnderTheCsvsName()
    {
        var csvPath = WriteFile("2026-Q3-tenant-7.csv", Encoding.UTF8.GetBytes(SampleCsv(rows: 1_000)));

        var zip = await QuarterlyReportZip.CreateAsync(csvPath, maxBytes: 10_000_000, CancellationToken.None);

        Assert.NotNull(zip);
        using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
        var entry = Assert.Single(archive.Entries);
        Assert.Equal("2026-Q3-tenant-7.csv", entry.FullName);
        using var unzipped = new MemoryStream();
        await using (var entryStream = entry.Open())
        {
            await entryStream.CopyToAsync(unzipped);
        }

        Assert.Equal(await File.ReadAllBytesAsync(csvPath), unzipped.ToArray());
    }

    [Fact]
    public async Task CreateAsync_MakesAReportCsvMuchSmaller()
    {
        var csv = Encoding.UTF8.GetBytes(SampleCsv(rows: 20_000));
        var csvPath = WriteFile("report.csv", csv);

        var zip = await QuarterlyReportZip.CreateAsync(csvPath, maxBytes: csv.Length, CancellationToken.None);

        Assert.NotNull(zip);
        Assert.True(zip.Length * 3 < csv.Length, $"Expected at least 3x smaller: CSV {csv.Length} bytes, zip {zip.Length} bytes.");
    }

    [Fact]
    public async Task CreateAsync_WhenTheZipWouldPassTheLimit_ReturnsNull()
    {
        // Random bytes do not compress, so the zip is larger than the file.
        var csvPath = WriteFile("report.csv", RandomNumberGenerator.GetBytes(200_000));

        Assert.Null(await QuarterlyReportZip.CreateAsync(csvPath, maxBytes: 100_000, CancellationToken.None));
    }

    [Fact]
    public async Task CreateAsync_WhenTheZipIsExactlyAtTheLimit_ReturnsIt()
    {
        var csvPath = WriteFile("report.csv", Encoding.UTF8.GetBytes(SampleCsv(rows: 500)));
        var size = (await QuarterlyReportZip.CreateAsync(csvPath, maxBytes: long.MaxValue, CancellationToken.None))!.Length;

        Assert.Equal(size, (await QuarterlyReportZip.CreateAsync(csvPath, maxBytes: size, CancellationToken.None))?.Length);
        Assert.Null(await QuarterlyReportZip.CreateAsync(csvPath, maxBytes: size - 1, CancellationToken.None));
    }

    [Fact]
    public void GetFileName_SwapsTheCsvExtensionForZip()
    {
        Assert.Equal("2026-Q3-tenant-7.zip", QuarterlyReportZip.GetFileName(Path.Combine("reports", "2026-Q3-tenant-7.csv")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private string WriteFile(string name, byte[] content)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllBytes(path, content);
        return path;
    }

    // Shaped like a real report: id, 20-character name, date, amount.
    private static string SampleCsv(int rows)
    {
        var csv = new StringBuilder("InvoiceId,CustomerName,IssuedDate,Amount\n");
        for (var i = 1; i <= rows; i++)
        {
            csv.Append(CultureInfo.InvariantCulture, $"{i},Customer {i:D11},2026-07-{i % 28 + 1:D2},{i % 10_000}.{i % 100:D2}\n");
        }

        return csv.ToString();
    }
}
