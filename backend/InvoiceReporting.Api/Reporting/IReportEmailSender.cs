namespace InvoiceReporting.Api.Reporting;

public interface IReportEmailSender
{
    /// <summary>
    /// Sends the email. Throws <see cref="ReportEmailPermanentException"/> when a
    /// retry cannot help, and <see cref="ReportEmailOutcomeUnknownException"/> when
    /// the email may or may not have been sent. Any other exception means it was not sent.
    /// </summary>
    Task SendAsync(ReportEmail email, CancellationToken cancellationToken);

    /// <summary>
    /// Asks the provider whether an email with this <see cref="ReportEmail.IdempotencyKey"/>
    /// was already sent. Used before resending after an earlier attempt may have
    /// succeeded. Throws when the provider cannot be asked right now.
    /// </summary>
    Task<PriorSendStatus> FindPriorSendAsync(string idempotencyKey, CancellationToken cancellationToken);
}

public enum PriorSendStatus
{
    /// <summary>The provider has a record of this email. Do not send it again.</summary>
    Sent,

    /// <summary>The provider has no record of this email. Send it.</summary>
    NotFound,

    /// <summary>The provider cannot look up past emails (for example SMTP). Send it.</summary>
    Unsupported,
}

/// <summary>
/// One report email with a single CSV attachment. <paramref name="IdempotencyKey"/>
/// is the same on every attempt for the same tenant, user and quarter. It
/// becomes the Message-ID, so mail systems that drop duplicate Message-IDs
/// can discard a repeat sent after a crash.
/// </summary>
public sealed record ReportEmail(
    string IdempotencyKey,
    string ToAddress,
    string Subject,
    string Body,
    string AttachmentFileName,
    byte[] AttachmentContent);
