using System.Collections.Concurrent;
using InvoiceReporting.Api.Reporting;

namespace InvoiceReporting.Api.Tests.Infrastructure;

/// <summary>
/// Records emails instead of sending them. Safe to call from several threads.
/// Can fail the next sends with a chosen kind of error, wait before recording
/// to imitate a slow provider, and answer provider lookups with a set result.
/// </summary>
public sealed class FakeReportEmailSender : IReportEmailSender
{
    private readonly ConcurrentQueue<ReportEmail> _sent = new();
    private readonly ConcurrentQueue<string> _lookups = new();
    private int _failNextSends;
    private int _failNextLookups;

    public IReadOnlyList<ReportEmail> Sent => [.. _sent];

    /// <summary>Idempotency keys passed to <see cref="FindPriorSendAsync"/>, in call order.</summary>
    public IReadOnlyList<string> Lookups => [.. _lookups];

    /// <summary>Number of upcoming sends that throw instead of recording the email.</summary>
    public int FailNextSends
    {
        get => Volatile.Read(ref _failNextSends);
        set => Volatile.Write(ref _failNextSends, value);
    }

    /// <summary>Which error a failing send throws. Defaults to a temporary failure.</summary>
    public FakeSendFailure FailureKind { get; set; } = FakeSendFailure.Temporary;

    /// <summary>How long each send takes.</summary>
    public TimeSpan Delay { get; set; } = TimeSpan.Zero;

    /// <summary>What <see cref="FindPriorSendAsync"/> returns. Defaults to NotFound.</summary>
    public PriorSendStatus LookupResult { get; set; } = PriorSendStatus.NotFound;

    /// <summary>Number of upcoming lookups that throw instead of answering.</summary>
    public int FailNextLookups
    {
        get => Volatile.Read(ref _failNextLookups);
        set => Volatile.Write(ref _failNextLookups, value);
    }

    /// <summary>Runs at the start of each send, for example to cancel the job's token mid-send.</summary>
    public Action? OnSend { get; set; }

    /// <summary>Runs at the start of each lookup, for example to cancel the job's token mid-lookup.</summary>
    public Action? OnLookup { get; set; }

    public async Task SendAsync(ReportEmail email, CancellationToken cancellationToken)
    {
        OnSend?.Invoke();

        // Like MailKit and HttpClient: a cancelled token stops the call.
        cancellationToken.ThrowIfCancellationRequested();

        if (TryConsume(ref _failNextSends))
        {
            throw FailureKind switch
            {
                FakeSendFailure.Permanent => new ReportEmailPermanentException("http-400:mj-0013"),
                FakeSendFailure.OutcomeUnknown => new ReportEmailOutcomeUnknownException(new TimeoutException("Simulated timeout.")),
                _ => new IOException("Simulated provider failure."),
            };
        }

        if (Delay > TimeSpan.Zero)
        {
            await Task.Delay(Delay, cancellationToken);
        }

        _sent.Enqueue(email);
    }

    public Task<PriorSendStatus> FindPriorSendAsync(string idempotencyKey, CancellationToken cancellationToken)
    {
        _lookups.Enqueue(idempotencyKey);

        OnLookup?.Invoke();
        cancellationToken.ThrowIfCancellationRequested();

        if (TryConsume(ref _failNextLookups))
        {
            throw new HttpRequestException("Simulated lookup failure.");
        }

        return Task.FromResult(LookupResult);
    }

    /// <summary>Takes one from the counter if it is above zero. Thread-safe.</summary>
    private static bool TryConsume(ref int counter)
    {
        if (Interlocked.Decrement(ref counter) >= 0)
        {
            return true;
        }

        // Undo the decrement when nothing was pending, so the count stays at zero.
        Interlocked.Increment(ref counter);
        return false;
    }
}

public enum FakeSendFailure
{
    /// <summary>An ordinary error: not sent, retry later.</summary>
    Temporary,

    /// <summary><see cref="ReportEmailPermanentException"/>: rejected, do not retry.</summary>
    Permanent,

    /// <summary><see cref="ReportEmailOutcomeUnknownException"/>: may or may not have been sent.</summary>
    OutcomeUnknown,
}
