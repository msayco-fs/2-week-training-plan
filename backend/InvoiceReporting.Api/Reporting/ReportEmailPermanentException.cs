namespace InvoiceReporting.Api.Reporting;

/// <summary>
/// The email provider refused the email in a way a retry cannot fix, for
/// example an invalid recipient address or bad credentials. The job records
/// the failure for support and does not retry.
/// </summary>
/// <remarks>
/// <see cref="Reason"/> goes into the audit log and the failed-email log file,
/// so it must hold only safe codes such as <c>http-400:mj-0013</c>. Never put
/// an email address, an API key or report content in it.
/// </remarks>
public sealed class ReportEmailPermanentException : Exception
{
    public ReportEmailPermanentException(string reason)
        : base($"The email provider rejected the report email ({reason}).")
    {
        Reason = reason;
    }

    /// <summary>A short, non-sensitive code for why the provider rejected the email.</summary>
    public string Reason { get; }
}
