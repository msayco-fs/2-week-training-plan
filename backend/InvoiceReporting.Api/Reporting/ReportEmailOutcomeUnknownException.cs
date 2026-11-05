namespace InvoiceReporting.Api.Reporting;

/// <summary>
/// The request reached the email provider, but no answer came back (for
/// example a timeout or a dropped connection). The email may or may not have
/// been sent.
/// </summary>
/// <remarks>
/// The job keeps its delivery claim instead of releasing it, so a retry does
/// not send straight away. Once the claim expires, the retry asks the provider
/// whether the email was already sent before sending it again.
/// </remarks>
public sealed class ReportEmailOutcomeUnknownException : Exception
{
    public ReportEmailOutcomeUnknownException(Exception innerException)
        : base("The email provider did not answer, so the report email may or may not have been sent.", innerException)
    {
    }
}
