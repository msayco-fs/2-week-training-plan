using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;

namespace InvoiceReporting.Api.Reporting;

/// <summary>Sends report emails through the SMTP server in <see cref="QuarterlyReportEmailOptions"/>.</summary>
public sealed class SmtpReportEmailSender(IOptions<QuarterlyReportEmailOptions> options) : IReportEmailSender
{
    public async Task SendAsync(ReportEmail email, CancellationToken cancellationToken)
    {
        var settings = options.Value;

        var from = new MailboxAddress(settings.FromName, settings.FromAddress);
        var message = new MimeMessage
        {
            // Same Message-ID on every retry of the same email.
            MessageId = $"{email.IdempotencyKey}@{from.Domain}",
        };
        message.From.Add(from);
        message.To.Add(MailboxAddress.Parse(email.ToAddress));
        message.Subject = email.Subject;

        var body = new BodyBuilder { TextBody = email.Body };
        body.Attachments.Add(email.AttachmentFileName, email.AttachmentContent, new ContentType("text", "csv"));
        message.Body = body.ToMessageBody();

        using var client = new SmtpClient();
        await client.ConnectAsync(settings.Host, settings.Port, ToSocketOptions(settings.Security), cancellationToken);
        if (!string.IsNullOrEmpty(settings.Username))
        {
            await client.AuthenticateAsync(settings.Username, settings.Password ?? "", cancellationToken);
        }

        await client.SendAsync(message, cancellationToken);

        // The server has accepted the email. A failed QUIT must not surface as
        // a failed send, or the caller would release its claim and send again.
        try
        {
            await client.DisconnectAsync(quit: true, CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or MailKit.ProtocolException or MailKit.Net.Smtp.SmtpCommandException)
        {
        }
    }

    private static SecureSocketOptions ToSocketOptions(SmtpSecurity security) => security switch
    {
        SmtpSecurity.None => SecureSocketOptions.None,
        SmtpSecurity.StartTls => SecureSocketOptions.StartTls,
        SmtpSecurity.SslOnConnect => SecureSocketOptions.SslOnConnect,
        _ => throw new ArgumentOutOfRangeException(nameof(security), security, null),
    };
}
