namespace InvoiceReporting.Api.Reporting;

/// <summary>
/// SMTP settings for emailing quarterly reports. Bound from
/// <c>Reporting:Email</c>. Development points at a local Mailpit server.
/// Production can use any SMTP provider (for example Mailjet) by changing
/// these settings only. Keep <see cref="Username"/> and <see cref="Password"/>
/// in user secrets, environment variables or a secret store, never in appsettings.
/// </summary>
public sealed class QuarterlyReportEmailOptions
{
    public const string SectionName = "Reporting:Email";

    /// <summary>When false, reports are still written to disk but no email is sent.</summary>
    public bool Enabled { get; set; }

    public string Host { get; set; } = "";

    public int Port { get; set; }

    public SmtpSecurity Security { get; set; } = SmtpSecurity.StartTls;

    /// <summary>Leave empty for servers without authentication, such as Mailpit.</summary>
    public string? Username { get; set; }

    public string? Password { get; set; }

    public string FromAddress { get; set; } = "";

    public string FromName { get; set; } = "Invoice Reporting";
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
