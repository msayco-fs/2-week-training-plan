namespace InvoiceReporting.Api.Reporting;

/// <summary>
/// Settings for emailing quarterly reports. Bound from <c>Reporting:Email</c>.
/// Development sends over SMTP to a local Mailpit server. Production sends
/// through the Mailjet HTTP API (<see cref="EmailProvider.MailjetApi"/>).
/// Keep <see cref="Password"/>, <see cref="MailjetApiOptions.ApiKey"/> and
/// <see cref="MailjetApiOptions.SecretKey"/> in user secrets or environment
/// variables, never in appsettings.
/// </summary>
public sealed class QuarterlyReportEmailOptions
{
    public const string SectionName = "Reporting:Email";

    /// <summary>
    /// The largest value <see cref="MaxAttachmentBytes"/> may take. Mailjet
    /// rejects messages over 15 MB, and base64 encoding adds about a third,
    /// so a 10 MB attachment is the most that fits safely.
    /// </summary>
    public const long MaxAllowedAttachmentBytes = 10 * 1024 * 1024;

    /// <summary>When false, reports are still written to disk but no email is sent.</summary>
    public bool Enabled { get; set; }

    /// <summary>Which provider sends the email. Development uses <see cref="EmailProvider.Smtp"/> with Mailpit.</summary>
    public EmailProvider Provider { get; set; } = EmailProvider.Smtp;

    /// <summary>
    /// The largest attachment the job will email. Reports are attached as a zip
    /// (see <see cref="QuarterlyReportZip"/>), so this limits the zipped size, not
    /// the CSV's. Larger reports are not sent and are recorded as failed for
    /// support. At most <see cref="MaxAllowedAttachmentBytes"/>.
    /// </summary>
    public long MaxAttachmentBytes { get; set; } = MaxAllowedAttachmentBytes;

    /// <summary>
    /// Folder for the failed-email log files that support reads. A relative path
    /// is resolved against the content root. In containers, mount a volume at
    /// <c>/app/logs</c>. Never point it inside <c>wwwroot</c>.
    /// </summary>
    public string FailedLogDirectory { get; set; } = "logs";

    /// <summary>How many daily failed-email log files to keep.</summary>
    public int FailedLogRetentionDays { get; set; } = 90;

    // SMTP settings, used when Provider is Smtp.

    public string Host { get; set; } = "";

    public int Port { get; set; }

    public SmtpSecurity Security { get; set; } = SmtpSecurity.StartTls;

    /// <summary>Leave empty for servers without authentication, such as Mailpit.</summary>
    public string? Username { get; set; }

    public string? Password { get; set; }

    // Shared by every provider.

    public string FromAddress { get; set; } = "";

    public string FromName { get; set; } = "Invoice Reporting";

    /// <summary>Mailjet settings, used when Provider is MailjetApi.</summary>
    public MailjetApiOptions Mailjet { get; set; } = new();
}

public enum EmailProvider
{
    /// <summary>Any SMTP server. Used for local development with Mailpit.</summary>
    Smtp,

    /// <summary>Mailjet Send API v3.1 over HTTPS. Used in production.</summary>
    MailjetApi,
}

/// <summary>Mailjet HTTP API settings. Bound from <c>Reporting:Email:Mailjet</c>.</summary>
public sealed class MailjetApiOptions
{
    public string? ApiKey { get; set; }

    public string? SecretKey { get; set; }

    public string BaseUrl { get; set; } = "https://api.mailjet.com";

    /// <summary>Timeout for one HTTP call to Mailjet.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>When true, Mailjet checks the request but delivers nothing. For manual testing only.</summary>
    public bool SandboxMode { get; set; }
}

public enum SmtpSecurity
{
    /// <summary>No encryption. Only for local test servers such as Mailpit.</summary>
    None,

    /// <summary>Plain connection upgraded with STARTTLS (usually port 587).</summary>
    StartTls,

    /// <summary>TLS from the first byte (usually port 465).</summary>
    SslOnConnect,
}
