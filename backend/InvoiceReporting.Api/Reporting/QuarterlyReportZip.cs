using System.IO.Compression;

namespace InvoiceReporting.Api.Reporting;

/// <summary>
/// Zips a quarterly report CSV for emailing. CSV text compresses well, so a
/// zipped report fits far more invoices under the email size limit.
/// </summary>
public static class QuarterlyReportZip
{
    public const string ContentType = "application/zip";

    /// <summary>The zip's file name: the CSV's name with a <c>.zip</c> extension.</summary>
    public static string GetFileName(string csvPath) => Path.ChangeExtension(Path.GetFileName(csvPath), ".zip");

    /// <summary>
    /// Returns a zip holding the CSV as its only entry, under the CSV's own file
    /// name. Returns null when the zip would be larger than
    /// <paramref name="maxBytes"/>. The CSV is streamed and compression stops
    /// as soon as the output passes the limit, so memory use stays near
    /// <paramref name="maxBytes"/> however large the CSV is.
    /// </summary>
    public static async Task<byte[]?> CreateAsync(string csvPath, long maxBytes, CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();
        try
        {
            using (var limited = new SizeLimitedStream(output, maxBytes))
            using (var archive = new ZipArchive(limited, ZipArchiveMode.Create, leaveOpen: true))
            {
                var entry = archive.CreateEntry(Path.GetFileName(csvPath), CompressionLevel.Optimal);
                entry.LastWriteTime = File.GetLastWriteTime(csvPath);

                await using var csv = new FileStream(
                    csvPath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 81920, useAsync: true);
                await using var entryStream = entry.Open();
                await csv.CopyToAsync(entryStream, cancellationToken);
            }
        }
        catch (SizeLimitExceededException)
        {
            return null;
        }

        return output.ToArray();
    }

    private sealed class SizeLimitExceededException : Exception;

    /// <summary>
    /// Passes everything through to <c>inner</c>, but throws once its length
    /// would pass <c>maxBytes</c>. Seekable, because the zip writer goes back
    /// to fill in each entry's sizes.
    /// </summary>
    private sealed class SizeLimitedStream(Stream inner, long maxBytes) : Stream
    {
        public override bool CanRead => false;

        public override bool CanSeek => inner.CanSeek;

        public override bool CanWrite => true;

        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            EnsureFits(count);
            inner.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            EnsureFits(buffer.Length);
            inner.Write(buffer);
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            EnsureFits(buffer.Length);
            await inner.WriteAsync(buffer, cancellationToken);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush() => inner.Flush();

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

        public override void SetLength(long value) => inner.SetLength(value);

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private void EnsureFits(int count)
        {
            if (Math.Max(inner.Length, inner.Position + count) > maxBytes)
            {
                throw new SizeLimitExceededException();
            }
        }
    }
}
