using System.Collections.Concurrent;
using InvoiceReporting.Api.Reporting;

namespace InvoiceReporting.Api.Tests.Infrastructure;

/// <summary>
/// Records emails instead of sending them. Safe to call from several threads.
/// Can fail the next sends, or wait before recording to imitate a slow SMTP server.
/// </summary>
public sealed class FakeReportEmailSender : IReportEmailSender
{
    private readonly ConcurrentQueue<ReportEmail> _sent = new();
    private int _failNextSends;

    public IReadOnlyList<ReportEmail> Sent => [.. _sent];

    /// <summary>Number of upcoming sends that throw instead of recording the email.</summary>
    public int FailNextSends
    {
        get => Volatile.Read(ref _failNextSends);
        set => Volatile.Write(ref _failNextSends, value);
    }

    /// <summary>How long each send takes.</summary>
    public TimeSpan Delay { get; set; } = TimeSpan.Zero;

    public async Task SendAsync(ReportEmail email, CancellationToken cancellationToken)
    {
        if (Interlocked.Decrement(ref _failNextSends) >= 0)
        {
            throw new IOException("Simulated SMTP failure.");
        }

        // Undo the decrement when no failure was pending, so the count stays at zero.
        Interlocked.Increment(ref _failNextSends);

        if (Delay > TimeSpan.Zero)
        {
            await Task.Delay(Delay, cancellationToken);
        }

        _sent.Enqueue(email);
    }
}
