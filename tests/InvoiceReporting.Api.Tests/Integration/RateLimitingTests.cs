using System.Net;
using InvoiceReporting.Api.Security;
using InvoiceReporting.Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace InvoiceReporting.Api.Tests.Integration;

/// <summary>The PerCaller rate limit, with a low limit so tests run quickly.</summary>
public sealed class RateLimitingTests : IClassFixture<InvoiceReportApiFactory>, IDisposable
{
    private const int PermitLimit = 2;

    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public RateLimitingTests(InvoiceReportApiFactory factory)
    {
        _factory = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.Configure<PerCallerRateLimitOptions>(options =>
            {
                options.PermitLimit = PermitLimit;
                options.Window = TimeSpan.FromMinutes(10);
            })));
        _client = _factory.CreateClient();
    }

    [Fact]
    public async Task CallerOverThePermitLimit_Gets429_WithRetryAfter_AndNoInvoiceData()
    {
        var subject = HttpRequestBuilder.NewSubject();
        for (var i = 0; i < PermitLimit; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await _client.SendAsync(OwnReport(subject))).StatusCode);
        }

        var limited = await _client.SendAsync(OwnReport(subject));

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.NotNull(limited.Headers.RetryAfter);
        Assert.DoesNotContain("Customer", await limited.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Limit_IsPerCaller_SoAnotherSubjectIsNotThrottled()
    {
        var first = HttpRequestBuilder.NewSubject();
        for (var i = 0; i <= PermitLimit; i++)
        {
            await _client.SendAsync(OwnReport(first));
        }

        var response = await _client.SendAsync(OwnReport(HttpRequestBuilder.NewSubject()));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task SpoofedForwardedFor_FromAnUntrustedSource_DoesNotEscapeTheLimit()
    {
        // Anonymous callers are limited per IP. X-Forwarded-For is honored only
        // from configured proxies, so a new fake address per request must not
        // give the caller a fresh budget.
        using var factory = WithClientAddress(knownProxy: null);

        var statuses = await SendAnonymousWithNewForwardedForEachTimeAsync(factory.CreateClient());

        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[^1]);
    }

    [Fact]
    public async Task ForwardedFor_FromAConfiguredProxy_IsHonored()
    {
        // From a trusted proxy, each forwarded address is a different client
        // with its own budget, so no request is limited.
        using var factory = WithClientAddress(knownProxy: ClientAddress);

        var statuses = await SendAnonymousWithNewForwardedForEachTimeAsync(factory.CreateClient());

        Assert.All(statuses, status => Assert.Equal(HttpStatusCode.Unauthorized, status));
    }

    public void Dispose() => _factory.Dispose();

    // TestServer leaves RemoteIpAddress null, and ForwardedHeadersMiddleware
    // skips the known-proxy check for a null address. Kestrel over TCP always
    // sets it, so these tests give every request a fixed address first.
    private const string ClientAddress = "198.51.100.7";

    private WebApplicationFactory<Program> WithClientAddress(string? knownProxy) =>
        _factory.WithWebHostBuilder(builder =>
        {
            if (knownProxy is not null)
            {
                builder.UseSetting("ForwardedHeaders:KnownProxies:0", knownProxy);
            }

            builder.ConfigureTestServices(services =>
                services.AddSingleton<IStartupFilter>(new FixedRemoteAddressStartupFilter(IPAddress.Parse(ClientAddress))));
        });

    private static async Task<List<HttpStatusCode>> SendAnonymousWithNewForwardedForEachTimeAsync(HttpClient client)
    {
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i <= PermitLimit; i++)
        {
            var request = HttpRequestBuilder.MonthlyReport(TestData.ReportMonth, TestData.TenantAKey, subject: null);
            request.Headers.Add("X-Forwarded-For", $"203.0.113.{i + 1}");
            statuses.Add((await client.SendAsync(request)).StatusCode);
        }

        return statuses;
    }

    private sealed class FixedRemoteAddressStartupFilter(IPAddress address) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                context.Connection.RemoteIpAddress = address;
                return nextMiddleware(context);
            });
            next(app);
        };
    }

    private static HttpRequestMessage OwnReport(string subject) => HttpRequestBuilder.MonthlyReport(
        TestData.ReportMonth, TestData.TenantAKey, subject, TestData.TenantAKey);
}
