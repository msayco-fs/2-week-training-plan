using System.Net;
using System.Text;
using System.Text.Json;
using InvoiceReporting.Api.Reporting;
using Microsoft.Extensions.Options;

namespace InvoiceReporting.Api.Tests.Reporting;

public sealed class MailjetReportEmailSenderTests
{
    private const string ApiKey = "test-api-key-123";
    private const string SecretKey = "test-secret-key-456";
    private const string Recipient = "finance@tenant-a.test";
    private const string CsvText = "Invoice,Customer,Amount\nINV-1,Secret Customer,10.00\n";

    private readonly StubHandler _handler = new();

    [Fact]
    public async Task SendAsync_PostsOneMessage_WithBasicAuth_TheCsv_AndTheCustomId()
    {
        await CreateSender().SendAsync(CreateEmail(), CancellationToken.None);

        var request = Assert.Single(_handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://api.mailjet.test/v3.1/send", request.Uri);
        Assert.Equal("Basic", request.AuthScheme);
        Assert.Equal($"{ApiKey}:{SecretKey}", Encoding.UTF8.GetString(Convert.FromBase64String(request.AuthParameter!)));

        using var json = JsonDocument.Parse(request.Body!);
        Assert.False(json.RootElement.GetProperty("SandboxMode").GetBoolean());
        var message = Assert.Single(json.RootElement.GetProperty("Messages").EnumerateArray());
        Assert.Equal("reports@example.test", message.GetProperty("From").GetProperty("Email").GetString());
        Assert.Equal(Recipient, Assert.Single(message.GetProperty("To").EnumerateArray()).GetProperty("Email").GetString());
        Assert.False(message.TryGetProperty("Cc", out _));
        Assert.False(message.TryGetProperty("Bcc", out _));
        Assert.Equal("quarterly-report.2026-Q3.t1.u2", message.GetProperty("CustomID").GetString());
        var attachment = Assert.Single(message.GetProperty("Attachments").EnumerateArray());
        // Passed through from the email, not fixed by the sender.
        Assert.Equal("application/zip", attachment.GetProperty("ContentType").GetString());
        Assert.Equal("report.zip", attachment.GetProperty("Filename").GetString());
        Assert.Equal(CsvText, Encoding.UTF8.GetString(Convert.FromBase64String(attachment.GetProperty("Base64Content").GetString()!)));
    }

    [Fact]
    public async Task SendAsync_InSandboxMode_SetsSandboxMode()
    {
        await CreateSender(sandboxMode: true).SendAsync(CreateEmail(), CancellationToken.None);

        using var json = JsonDocument.Parse(Assert.Single(_handler.Requests).Body!);
        Assert.True(json.RootElement.GetProperty("SandboxMode").GetBoolean());
    }

    [Fact]
    public async Task SendAsync_With400_ThrowsPermanent_WithOnlyTheMailjetErrorCode()
    {
        _handler.Respond(HttpStatusCode.BadRequest,
            $$"""{"Messages":[{"Status":"error","Errors":[{"ErrorCode":"mj-0013","StatusCode":400,"ErrorMessage":"\"{{Recipient}}\" is an invalid email address."}]}]}""");

        var exception = await Assert.ThrowsAsync<ReportEmailPermanentException>(
            () => CreateSender().SendAsync(CreateEmail(), CancellationToken.None));

        Assert.Equal("http-400:mj-0013", exception.Reason);
        AssertHasNoSecrets(exception);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "http-401")]
    [InlineData(HttpStatusCode.Forbidden, "http-403")]
    public async Task SendAsync_WithAuthErrors_ThrowsPermanent(HttpStatusCode status, string reason)
    {
        _handler.Respond(status, "<html>Unauthorized</html>");

        var exception = await Assert.ThrowsAsync<ReportEmailPermanentException>(
            () => CreateSender().SendAsync(CreateEmail(), CancellationToken.None));

        Assert.Equal(reason, exception.Reason);
    }

    [Fact]
    public async Task SendAsync_WithAnUnexpectedErrorCodeFormat_LeavesItOut()
    {
        _handler.Respond(HttpStatusCode.BadRequest,
            $$"""{"Messages":[{"Errors":[{"ErrorCode":"{{Recipient}}"}]}]}""");

        var exception = await Assert.ThrowsAsync<ReportEmailPermanentException>(
            () => CreateSender().SendAsync(CreateEmail(), CancellationToken.None));

        Assert.Equal("http-400", exception.Reason);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task SendAsync_WithRateLimitOrServerError_ThrowsATemporaryError(HttpStatusCode status)
    {
        _handler.Respond(status, "{}");

        var exception = await Assert.ThrowsAsync<HttpRequestException>(
            () => CreateSender().SendAsync(CreateEmail(), CancellationToken.None));

        Assert.Equal(status, exception.StatusCode);
        AssertHasNoSecrets(exception);
    }

    [Fact]
    public async Task SendAsync_WhenMailjetCannotBeReached_ThrowsATemporaryError()
    {
        _handler.Throw(new HttpRequestException(HttpRequestError.ConnectionError, "Connection refused."));

        await Assert.ThrowsAsync<HttpRequestException>(
            () => CreateSender().SendAsync(CreateEmail(), CancellationToken.None));
    }

    [Fact]
    public async Task SendAsync_WhenTheConnectionDropsAfterSending_ThrowsOutcomeUnknown()
    {
        _handler.Throw(new HttpRequestException(HttpRequestError.ResponseEnded, "The response ended prematurely."));

        await Assert.ThrowsAsync<ReportEmailOutcomeUnknownException>(
            () => CreateSender().SendAsync(CreateEmail(), CancellationToken.None));
    }

    [Fact]
    public async Task SendAsync_WhenMailjetTimesOut_ThrowsOutcomeUnknown()
    {
        _handler.Hang();

        var exception = await Assert.ThrowsAsync<ReportEmailOutcomeUnknownException>(
            () => CreateSender(timeout: TimeSpan.FromMilliseconds(50)).SendAsync(CreateEmail(), CancellationToken.None));

        AssertHasNoSecrets(exception);
    }

    /// <summary>
    /// A stopped job is not a failed send: it surfaces as a cancellation, so
    /// the email job records nothing. A timeout (above) stays OutcomeUnknown.
    /// </summary>
    [Fact]
    public async Task SendAsync_WhenTheJobIsCancelledMidRequest_ThrowsOperationCanceled_NotOutcomeUnknown()
    {
        _handler.Hang();
        using var cts = new CancellationTokenSource();

        var send = CreateSender().SendAsync(CreateEmail(), cts.Token);
        await cts.CancelAsync();

        // ReportEmailOutcomeUnknownException is not an OperationCanceledException,
        // so this also proves the cancellation was not wrapped.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => send);
    }

    [Fact]
    public async Task FindPriorSendAsync_WhenTheJobIsCancelled_ThrowsOperationCanceled()
    {
        _handler.Hang();
        using var cts = new CancellationTokenSource();

        var lookup = CreateSender().FindPriorSendAsync("quarterly-report.2026-Q3.t1.u2", cts.Token);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lookup);
    }

    [Theory]
    [InlineData(1, PriorSendStatus.Sent)]
    [InlineData(0, PriorSendStatus.NotFound)]
    public async Task FindPriorSendAsync_LooksUpTheCustomId(int count, PriorSendStatus expected)
    {
        _handler.Respond(HttpStatusCode.OK, $$"""{"Count":{{count}},"Data":[],"Total":{{count}}}""");

        var status = await CreateSender().FindPriorSendAsync("quarterly-report.2026-Q3.t1.u2", CancellationToken.None);

        Assert.Equal(expected, status);
        var request = Assert.Single(_handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("https://api.mailjet.test/v3/REST/message?CustomID=quarterly-report.2026-Q3.t1.u2", request.Uri);
        Assert.Equal("Basic", request.AuthScheme);
    }

    [Fact]
    public async Task FindPriorSendAsync_WhenMailjetFails_Throws()
    {
        _handler.Respond(HttpStatusCode.InternalServerError, "{}");

        await Assert.ThrowsAsync<HttpRequestException>(
            () => CreateSender().FindPriorSendAsync("quarterly-report.2026-Q3.t1.u2", CancellationToken.None));
    }

    private MailjetReportEmailSender CreateSender(bool sandboxMode = false, TimeSpan? timeout = null)
    {
        var client = new HttpClient(_handler)
        {
            BaseAddress = new Uri("https://api.mailjet.test/"),
            Timeout = timeout ?? TimeSpan.FromSeconds(10),
        };

        return new MailjetReportEmailSender(client, Options.Create(new QuarterlyReportEmailOptions
        {
            Enabled = true,
            Provider = EmailProvider.MailjetApi,
            FromAddress = "reports@example.test",
            Mailjet = new MailjetApiOptions { ApiKey = ApiKey, SecretKey = SecretKey, SandboxMode = sandboxMode },
        }));
    }

    private static ReportEmail CreateEmail() => new(
        IdempotencyKey: "quarterly-report.2026-Q3.t1.u2",
        ToAddress: Recipient,
        Subject: "Quarterly invoice report 2026-Q3 – Tenant A",
        Body: "Attached is the report.",
        AttachmentFileName: "report.zip",
        AttachmentContentType: "application/zip",
        AttachmentContent: Encoding.UTF8.GetBytes(CsvText));

    /// <summary>The exception chain must never carry keys, the address or report content.</summary>
    private static void AssertHasNoSecrets(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            Assert.DoesNotContain(ApiKey, current.Message);
            Assert.DoesNotContain(SecretKey, current.Message);
            Assert.DoesNotContain(Recipient, current.Message);
            Assert.DoesNotContain("Secret Customer", current.Message);
        }
    }

    private sealed record RecordedRequest(HttpMethod Method, string Uri, string? AuthScheme, string? AuthParameter, string? Body);

    /// <summary>Records each request and returns a set response, throws, or never answers.</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private HttpStatusCode _status = HttpStatusCode.OK;
        private string _body = """{"Messages":[{"Status":"success"}]}""";
        private Exception? _exception;
        private bool _hang;

        public List<RecordedRequest> Requests { get; } = [];

        public void Respond(HttpStatusCode status, string body) => (_status, _body) = (status, body);

        public void Throw(Exception exception) => _exception = exception;

        public void Hang() => _hang = true;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new RecordedRequest(
                request.Method,
                request.RequestUri!.ToString(),
                request.Headers.Authorization?.Scheme,
                request.Headers.Authorization?.Parameter,
                request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken)));

            if (_hang)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            if (_exception is not null)
            {
                throw _exception;
            }

            return new HttpResponseMessage(_status) { Content = new StringContent(_body, Encoding.UTF8, "application/json") };
        }
    }
}
