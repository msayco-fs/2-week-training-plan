namespace InvoiceReporting.Api.Domain;

public static class AuditActions
{
    public const string ViewMonthlyInvoices = "ViewMonthlyInvoices";

    public const string GenerateQuarterlyInvoiceReport = "GenerateQuarterlyInvoiceReport";

    public const string EmailQuarterlyInvoiceReport = "EmailQuarterlyInvoiceReport";

    /// <summary>
    /// A report email that will not be sent without help, for example too large,
    /// rejected by the provider, or out of retries. Support reviews these.
    /// Written once per tenant, user, quarter and reason.
    /// </summary>
    public const string EmailQuarterlyInvoiceReportFailed = "EmailQuarterlyInvoiceReportFailed";

    /// <summary>One failed send attempt that Hangfire will retry. For troubleshooting only.</summary>
    public const string EmailQuarterlyInvoiceReportAttemptFailed = "EmailQuarterlyInvoiceReportAttemptFailed";
}
