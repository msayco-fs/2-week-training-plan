namespace InvoiceReporting.Api.Reporting;

public interface IReportEmailSender
{
    Task SendAsync(ReportEmail email, CancellationToken cancellationToken);
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
