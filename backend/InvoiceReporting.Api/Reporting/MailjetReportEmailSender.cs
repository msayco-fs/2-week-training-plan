using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace InvoiceReporting.Api.Reporting;

/// <summary>
/// Sends report emails through the Mailjet Send API v3.1. All Mailjet-specific
/// code lives in this class. Registered as a typed <see cref="HttpClient"/>;
/// the base address and timeout come from <see cref="MailjetApiOptions"/>.
/// </summary>
/// <remarks>
/// The API keys are added to each request, never to the client's default
/// headers. HttpClient logging redacts header values by default; do not turn
/// that off, and never log request or response bodies, which hold the
/// recipient address and the report.
/// </remarks>
public sealed partial class MailjetReportEmailSender(
    HttpClient http,
    IOptions<QuarterlyReportEmailOptions> options) : IReportEmailSender
{
    // Mailjet expects PascalCase property names exactly as declared.
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = null };

    public async Task SendAsync(ReportEmail email, CancellationToken cancellationToken)
    {
        var settings = options.Value;

        var payload = new SendRequest(
            SandboxMode: settings.Mailjet.SandboxMode,
            Messages:
            [
                new Message(
                    From: new Contact(settings.FromAddress, settings.FromName),
                    // Exactly one recipient, and no Cc or Bcc, so one tenant's
                    // report can never be copied to anyone else.
                    To: [new Contact(email.ToAddress, null)],
                    Subject: email.Subject,
                    TextPart: email.Body,
                    Attachments:
                    [
                        new Attachment(email.AttachmentContentType, email.AttachmentFileName, Convert.ToBase64String(email.AttachmentContent)),
                    ],
                    CustomID: email.IdempotencyKey),
            ]);

        using var request = new HttpRequestMessage(HttpMethod.Post, "v3.1/send")
        {
            Content = JsonContent.Create(payload, options: JsonOptions),
        };
        AddAuthorization(request, settings.Mailjet);

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException ex) when (IsBeforeRequestWasSent(ex))
        {
            // Never reached Mailjet, so nothing was sent. Plain retry is safe.
            throw;
        }
        catch (HttpRequestException ex)
        {
            // The connection broke after the request went out.
            throw new ReportEmailOutcomeUnknownException(ex);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The job was stopped mid-request. Rethrown as a cancellation, not
            // a failure: the caller keeps its claim (Mailjet may have the
            // email) and records nothing, and Hangfire requeues the job.
            throw;
        }
        catch (OperationCanceledException ex)
        {
            // HttpClient timeout: Mailjet may already have accepted the email.
            throw new ReportEmailOutcomeUnknownException(ex);
        }

        using (response)
        {
            if (response.IsSuccessStatusCode)
            {
                return;
            }

            var status = (int)response.StatusCode;
            if (IsTemporary(response.StatusCode))
            {
                throw new HttpRequestException(
                    string.Create(CultureInfo.InvariantCulture, $"Mailjet returned HTTP {status}; the email was not sent and will be retried."),
                    inner: null,
                    response.StatusCode);
            }

            var errorCode = await ReadErrorCodeAsync(response, cancellationToken);
            var reason = errorCode is null
                ? string.Create(CultureInfo.InvariantCulture, $"http-{status}")
                : string.Create(CultureInfo.InvariantCulture, $"http-{status}:{errorCode}");
            throw new ReportEmailPermanentException(reason);
        }
    }

    public async Task<PriorSendStatus> FindPriorSendAsync(string idempotencyKey, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"v3/REST/message?CustomID={Uri.EscapeDataString(idempotencyKey)}");
        AddAuthorization(request, options.Value.Mailjet);

        // Any failure here throws: the caller keeps its claim and asks again later.
        using var response = await http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                string.Create(CultureInfo.InvariantCulture, $"Mailjet message lookup returned HTTP {(int)response.StatusCode}."),
                inner: null,
                response.StatusCode);
        }

        var result = await response.Content.ReadFromJsonAsync<MessageLookupResponse>(JsonOptions, cancellationToken);
        return result?.Count > 0 ? PriorSendStatus.Sent : PriorSendStatus.NotFound;
    }

    private static void AddAuthorization(HttpRequestMessage request, MailjetApiOptions mailjet)
    {
        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{mailjet.ApiKey}:{mailjet.SecretKey}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
    }

    // Failures that happen before any bytes reach Mailjet.
    private static bool IsBeforeRequestWasSent(HttpRequestException ex) => ex.HttpRequestError is
        HttpRequestError.NameResolutionError
        or HttpRequestError.ConnectionError
        or HttpRequestError.SecureConnectionError
        or HttpRequestError.ProxyTunnelError;

    // Rate limits, timeouts and server errors: Mailjet did not take the email,
    // so a retry may succeed. Every other 4xx means the request itself is wrong.
    private static bool IsTemporary(HttpStatusCode status) =>
        status is HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout || (int)status >= 500;

    /// <summary>
    /// Reads Mailjet's error code (for example <c>mj-0013</c>) from an error
    /// response. Returns only codes that match the expected pattern, so no
    /// part of the error message (which can echo the address) leaks into logs.
    /// </summary>
    private static async Task<string?> ReadErrorCodeAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            using var document = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);

            if (document.RootElement.TryGetProperty("Messages", out var messages)
                && messages.ValueKind == JsonValueKind.Array
                && messages.GetArrayLength() > 0
                && messages[0].TryGetProperty("Errors", out var errors)
                && errors.ValueKind == JsonValueKind.Array
                && errors.GetArrayLength() > 0
                && errors[0].TryGetProperty("ErrorCode", out var code)
                && code.ValueKind == JsonValueKind.String)
            {
                var value = code.GetString();
                return value is not null && SafeErrorCode().IsMatch(value) ? value : null;
            }
        }
        catch (JsonException)
        {
            // Not JSON (for example an HTML error page). The status code is enough.
        }

        return null;
    }

    [GeneratedRegex("^[a-z]{2}-[0-9]{4}$")]
    private static partial Regex SafeErrorCode();

    private sealed record SendRequest(bool SandboxMode, IReadOnlyList<Message> Messages);

    private sealed record Message(
        Contact From,
        IReadOnlyList<Contact> To,
        string Subject,
        string TextPart,
        IReadOnlyList<Attachment> Attachments,
        string CustomID);

    private sealed record Contact(string Email, string? Name);

    private sealed record Attachment(string ContentType, string Filename, string Base64Content);

    private sealed record MessageLookupResponse(int Count);
}
