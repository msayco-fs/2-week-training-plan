using System.Net;
using InvoiceReporting.Api.Tests.Infrastructure;

namespace InvoiceReporting.Api.Tests.Integration;

public sealed class HangfireDashboardTests(InvoiceReportApiFactory factory) : IClassFixture<InvoiceReportApiFactory>
{
    /// <summary>
    /// Tests run in the "Testing" environment, so the dashboard keeps Hangfire's
    /// local-requests-only rule. Test requests have no remote address, so they
    /// are refused. Only Development opens the dashboard to other callers.
    /// </summary>
    [Fact]
    public async Task Dashboard_OutsideDevelopment_RefusesNonLocalRequests()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/hangfire");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
