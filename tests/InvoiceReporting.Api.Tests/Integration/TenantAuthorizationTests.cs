using System.Net;
using System.Net.Http.Json;
using InvoiceReporting.Api.Reporting;
using InvoiceReporting.Api.Security;
using InvoiceReporting.Api.Tenancy;
using InvoiceReporting.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace InvoiceReporting.Api.Tests.Integration;

/// <summary>
/// Phase 2 HTTP integration tests for the tenant-scoped authorization policy.
/// Every test goes through the real pipeline: routing, authentication,
/// [Authorize], the TenantAccess policy, MVC and EF Core.
/// </summary>
public sealed class TenantAuthorizationTests(InvoiceReportApiFactory factory) : IClassFixture<InvoiceReportApiFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    // ---- Test A: cross-tenant access is forbidden (regression test for the Phase 1 flaw)

    [Fact]
    public async Task A_CrossTenantRequest_ByStandardUser_Returns403_WithNoInvoiceData_AndDoesNotRunReport()
    {
        var subject = HttpRequestBuilder.NewSubject();
        var request = HttpRequestBuilder.MonthlyReport(
            TestData.ReportMonth, TestData.TenantBKey, subject, tenantClaim: TestData.TenantAKey);

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await AssertNoReportDataAsync(response);
        await AssertReportNotExecutedAsync(subject);
    }

    // ---- Test B: same-tenant access is allowed

    [Fact]
    public async Task B_SameTenantRequest_ByStandardUser_Returns200_WithOnlyThatTenantsInvoicesAndTotal()
    {
        var request = HttpRequestBuilder.MonthlyReport(
            TestData.ReportMonth, TestData.TenantAKey, HttpRequestBuilder.NewSubject(), tenantClaim: TestData.TenantAKey);

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var report = await response.Content.ReadFromJsonAsync<MonthlyInvoiceReport>();
        Assert.NotNull(report);
        Assert.Equal(TestData.ReportMonth, report.Month);
        Assert.Equal(TestData.TenantASeptemberTotal, report.Total);
        Assert.Equal(3, report.Invoices.Count);
        Assert.All(report.Invoices, i => Assert.StartsWith("A-", i.CustomerName));
    }

    // ---- Test C: administrator bypass is allowed

    [Theory]
    [InlineData(null)]                 // admin with no tenant claim
    [InlineData(TestData.TenantAKey)]  // admin whose own tenant differs from the requested one
    public async Task C_CrossTenantRequest_ByAdministrator_Returns200_WithRequestedTenantsReport(string? adminTenantClaim)
    {
        var subject = HttpRequestBuilder.NewSubject();
        var request = HttpRequestBuilder.MonthlyReport(
            TestData.ReportMonth, TestData.TenantBKey, subject, adminTenantClaim, AppRoles.PlatformAdmin);

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var report = await response.Content.ReadFromJsonAsync<MonthlyInvoiceReport>();
        Assert.NotNull(report);
        Assert.Equal(TestData.ReportMonth, report.Month);
        Assert.Equal(TestData.TenantBSeptemberTotal, report.Total);
        Assert.Equal(2, report.Invoices.Count);
        Assert.All(report.Invoices, i => Assert.StartsWith("B-", i.CustomerName));

        // The admin read is audited against the tenant that was read.
        var auditedTenant = await factory.WithDbAsync(db => db.AuditLogs
            .Where(a => a.ActorSubject == subject)
            .Select(a => a.Tenant.Key)
            .SingleAsync());
        Assert.Equal(TestData.TenantBKey, auditedTenant);
    }

    // ---- Test D: unauthenticated request is rejected

    [Theory]
    [InlineData(TestData.TenantAKey)]
    [InlineData(TestData.TenantBKey)]
    public async Task D_UnauthenticatedRequest_Returns401_WithNoInvoiceData(string dealerId)
    {
        var auditCountBefore = await factory.WithDbAsync(db => db.AuditLogs.CountAsync());
        var request = HttpRequestBuilder.MonthlyReport(TestData.ReportMonth, dealerId, subject: null);

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await AssertNoReportDataAsync(response);
        Assert.Equal(auditCountBefore, await factory.WithDbAsync(db => db.AuditLogs.CountAsync()));
    }

    // ---- Test E: missing or invalid tenant information is denied

    public static TheoryData<string, string?> InvalidTenantClaims => new()
    {
        { "no tenant_id claim", null },
        { "empty tenant_id claim", "" },
        { "whitespace tenant_id claim", " " },
        { "claim for a tenant that does not exist", "dealer-unknown" },
        { "two tenant_id claims, one matching", $"{TestData.TenantAKey},{TestData.TenantBKey}" },
    };

    [Theory]
    [MemberData(nameof(InvalidTenantClaims))]
    public async Task E_StandardUser_WithMissingOrInvalidTenantClaim_Returns403(string scenario, string? tenantClaim)
    {
        var subject = HttpRequestBuilder.NewSubject();
        var request = HttpRequestBuilder.MonthlyReport(TestData.ReportMonth, TestData.TenantAKey, subject, tenantClaim);

        var response = await _client.SendAsync(request);

        Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{scenario}: expected 403, got {(int)response.StatusCode}");
        await AssertNoReportDataAsync(response);
        await AssertReportNotExecutedAsync(subject);
    }

    [Fact]
    public async Task E_StandardUser_RequestingUnknownTenant_Returns403_NotFound404()
    {
        // A 404 here would let any user probe which tenant keys exist.
        var subject = HttpRequestBuilder.NewSubject();
        var request = HttpRequestBuilder.MonthlyReport(TestData.ReportMonth, "dealer-unknown", subject, TestData.TenantAKey);

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await AssertNoReportDataAsync(response);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not a valid key!")]
    public async Task E_MissingOrMalformedTenantHeader_Returns400_WithNoInvoiceData(string? dealerId)
    {
        var subject = HttpRequestBuilder.NewSubject();
        var request = HttpRequestBuilder.MonthlyReport(TestData.ReportMonth, dealerId, subject, TestData.TenantAKey);

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertNoReportDataAsync(response);
        await AssertReportNotExecutedAsync(subject);
    }

    [Fact]
    public async Task E_TenantHeaderWithDifferentCase_StillResolvesToRequestedTenant_AndIsForbiddenCrossTenant()
    {
        // Normalization must not open a gap: "DEALER-BRAVO" is still tenant B.
        var subject = HttpRequestBuilder.NewSubject();
        var request = HttpRequestBuilder.MonthlyReport(TestData.ReportMonth, "DEALER-BRAVO", subject, TestData.TenantAKey);

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await AssertReportNotExecutedAsync(subject);
    }

    // ---- Test F: tenant claim casing from the identity provider is normalized

    [Theory]
    [InlineData("DEALER-ALPHA")]
    [InlineData("Dealer-Alpha")]
    [InlineData("dEaLeR-aLpHa")]
    public async Task F_SameTenantRequest_WithMixedCaseTenantClaim_Returns200_WithOnlyThatTenantsInvoices(string tenantClaim)
    {
        var request = HttpRequestBuilder.MonthlyReport(
            TestData.ReportMonth, TestData.TenantAKey, HttpRequestBuilder.NewSubject(), tenantClaim);

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var report = await response.Content.ReadFromJsonAsync<MonthlyInvoiceReport>();
        Assert.NotNull(report);
        Assert.Equal(TestData.TenantASeptemberTotal, report.Total);
        Assert.Equal(3, report.Invoices.Count);
        Assert.All(report.Invoices, i => Assert.StartsWith("A-", i.CustomerName));
    }

    [Theory]
    [InlineData("DEALER-ALPHA")]
    [InlineData("Dealer-Alpha")]
    public async Task F_CrossTenantRequest_WithMixedCaseTenantClaim_Returns403_AndDoesNotRunReport(string tenantClaim)
    {
        // Normalizing the claim must not widen access: a tenant A claim in any
        // casing is still not tenant B.
        var subject = HttpRequestBuilder.NewSubject();
        var request = HttpRequestBuilder.MonthlyReport(TestData.ReportMonth, TestData.TenantBKey, subject, tenantClaim);

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await AssertNoReportDataAsync(response);
        await AssertReportNotExecutedAsync(subject);
    }

    // ---- Administrator bypass cannot be self-assigned

    [Fact]
    public async Task AdminBypass_CannotBeClaimedThroughQueryStringOrRequestHeaders()
    {
        var subject = HttpRequestBuilder.NewSubject();
        var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"{HttpRequestBuilder.MonthlyReportPath}?month={TestData.ReportMonth}&role={AppRoles.PlatformAdmin}&isAdmin=true");
        request.Headers.Add(TenantResolver.TenantHeaderName, TestData.TenantBKey);
        request.Headers.Add(TestAuthHandler.SubjectHeader, subject);
        request.Headers.Add(TestAuthHandler.TenantHeader, TestData.TenantAKey);
        request.Headers.Add("X-Role", AppRoles.PlatformAdmin);
        request.Headers.Add("X-Admin", "true");

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await AssertNoReportDataAsync(response);
        await AssertReportNotExecutedAsync(subject);
    }

    [Theory]
    [InlineData("platformadmin")]
    [InlineData("Admin")]
    [InlineData("TenantAdmin")]
    public async Task AdminBypass_RequiresTheExactConfiguredRole(string role)
    {
        var request = HttpRequestBuilder.MonthlyReport(
            TestData.ReportMonth, TestData.TenantBKey, HttpRequestBuilder.NewSubject(), TestData.TenantAKey, role);

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await AssertNoReportDataAsync(response);
    }

    private static async Task AssertNoReportDataAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Customer", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("invoices", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("total", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(TestData.TenantBKey, body, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The report service always writes an audit row, so no row means it never ran.</summary>
    private async Task AssertReportNotExecutedAsync(string subject)
    {
        var auditRows = await factory.WithDbAsync(db => db.AuditLogs.CountAsync(a => a.ActorSubject == subject));
        Assert.Equal(0, auditRows);
    }
}
