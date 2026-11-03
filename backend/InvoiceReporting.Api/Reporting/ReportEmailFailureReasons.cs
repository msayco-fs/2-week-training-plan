using System.Globalization;

namespace InvoiceReporting.Api.Reporting;

/// <summary>
/// Short, fixed codes for why a report email failed. They go into the audit
/// log and the failed-email log file, so they never hold an email address, an
/// API key or report content.
/// </summary>
public static class ReportEmailFailureReasons
{
    /// <summary>The audit log's Detail column holds at most this many characters.</summary>
    public const int MaxDetailLength = 64;

    /// <summary>The CSV is larger than <see cref="QuarterlyReportEmailOptions.MaxAttachmentBytes"/>.</summary>
    public const string TooLarge = "too-large";

    /// <summary>A temporary error, such as a rate limit, a server error or no connection.</summary>
    public const string Temporary = "temporary";

    /// <summary>The provider did not answer, so the email may or may not have been sent.</summary>
    public const string OutcomeUnknown = "outcome-unknown";

    /// <summary>The provider could not be asked whether an earlier attempt already sent the email.</summary>
    public const string LookupFailed = "lookup-failed";

    /// <summary>The quarterly CSV was not there to attach.</summary>
    public const string CsvMissing = "csv-missing";

    /// <summary>Another job held the claim for this email, so this attempt did not send.</summary>
    public const string InProgress = "in-progress";

    /// <summary>The provider refused the email. <paramref name="providerReason"/> is a safe code such as <c>http-400:mj-0013</c>.</summary>
    public static string Rejected(string providerReason) => $"rejected:{providerReason}";

    /// <summary>The last Hangfire retry failed. <paramref name="lastFailure"/> is one of the codes above.</summary>
    public static string RetriesExhausted(string lastFailure) => $"retries-exhausted:{lastFailure}";

    /// <summary>
    /// Builds the audit detail <c>{quarter}:user-{id}:{reason}</c>. Cut to
    /// <see cref="MaxDetailLength"/> so an unexpected long reason can never fail the save.
    /// </summary>
    public static string Detail(ReportingQuarter quarter, int userId, string reason)
    {
        var detail = string.Create(CultureInfo.InvariantCulture, $"{quarter}:user-{userId}:{reason}");
        return detail.Length <= MaxDetailLength ? detail : detail[..MaxDetailLength];
    }
}
