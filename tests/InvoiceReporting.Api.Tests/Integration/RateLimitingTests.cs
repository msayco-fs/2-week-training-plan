using System.Net;
using InvoiceReporting.Api.Security;
using InvoiceReporting.Api.Tests.Infrastructure;
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

    public void Dispose() => _factory.Dispose();

    private static HttpRequestMessage OwnReport(string subject) => HttpRequestBuilder.MonthlyReport(
        TestData.ReportMonth, TestData.TenantAKey, subject, TestData.TenantAKey);
}
