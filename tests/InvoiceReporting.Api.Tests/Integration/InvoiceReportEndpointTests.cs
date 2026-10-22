using System.Net;
using System.Net.Http.Json;
using InvoiceReporting.Api.Domain;
using InvoiceReporting.Api.Reporting;
using InvoiceReporting.Api.Security;
using InvoiceReporting.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace InvoiceReporting.Api.Tests.Integration;

/// <summary>
/// Phase 1 HTTP tests for the monthly invoice report, run through the full
/// ASP.NET Core pipeline (routing, authentication, authorization, MVC, EF Core).
/// </summary>
public sealed class InvoiceReportEndpointTests(InvoiceReportApiFactory factory) : IClassFixture<InvoiceReportApiFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task SameTenant_ReturnsOnlyThatTenantsInvoicesForTheMonth_AndCorrectTotal()
    {
        var request = HttpRequestBuilder.MonthlyReport(
            TestData.ReportMonth, TestData.TenantAKey, HttpRequestBuilder.NewSubject(), TestData.TenantAKey);

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var report = await response.Content.ReadFromJsonAsync<MonthlyInvoiceReport>();
        Assert.NotNull(report);
        Assert.Equal(TestData.ReportMonth, report.Month);
        Assert.Equal(TestData.TenantASeptemberTotal, report.Total);
        Assert.Equal(["A-Customer-1", "A-Customer-2", "A-Customer-3"], report.Invoices.Select(i => i.CustomerName));
        Assert.All(report.Invoices, i => Assert.Equal(2026, i.IssuedDate.Year));
        Assert.All(report.Invoices, i => Assert.Equal(9, i.IssuedDate.Month));
    }

    [Fact]
    public async Task MonthWithNoInvoices_ReturnsEmptyListAndZeroTotal()
    {
        var request = HttpRequestBuilder.MonthlyReport(
            "2025-01", TestData.TenantAKey, HttpRequestBuilder.NewSubject(), TestData.TenantAKey);

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var report = await response.Content.ReadFromJsonAsync<MonthlyInvoiceReport>();
        Assert.NotNull(report);
        Assert.Empty(report.Invoices);
        Assert.Equal(0m, report.Total);
    }

    [Fact]
    public async Task SuccessfulReport_WritesOneAuditLogEntryForTheResolvedTenant()
    {
        var subject = HttpRequestBuilder.NewSubject();
        var request = HttpRequestBuilder.MonthlyReport(TestData.ReportMonth, TestData.TenantAKey, subject, TestData.TenantAKey);

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var entries = await factory.WithDbAsync(db => db.AuditLogs
            .Where(a => a.ActorSubject == subject)
            .Select(a => new { a.Action, a.Detail, TenantKey = a.Tenant.Key })
            .ToListAsync());
        var entry = Assert.Single(entries);
        Assert.Equal(AuditActions.ViewMonthlyInvoices, entry.Action);
        Assert.Equal(TestData.ReportMonth, entry.Detail);
        Assert.Equal(TestData.TenantAKey, entry.TenantKey);
    }

    [Fact]
    public async Task Unauthenticated_Returns401_AndWritesNoAuditLog()
    {
        var request = HttpRequestBuilder.MonthlyReport(TestData.ReportMonth, TestData.TenantAKey, subject: null);
        var auditCountBefore = await factory.WithDbAsync(db => db.AuditLogs.CountAsync());

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(auditCountBefore, await factory.WithDbAsync(db => db.AuditLogs.CountAsync()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("2026-13")]
    [InlineData("September")]
    public async Task InvalidMonth_Returns400(string? month)
    {
        var request = HttpRequestBuilder.MonthlyReport(month, TestData.TenantAKey, HttpRequestBuilder.NewSubject(), TestData.TenantAKey);

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not a valid key!")]
    public async Task MissingOrMalformedTenantHeader_Returns400_AndWritesNoAuditLog(string? dealerId)
    {
        var subject = HttpRequestBuilder.NewSubject();
        var request = HttpRequestBuilder.MonthlyReport(TestData.ReportMonth, dealerId, subject, TestData.TenantAKey);

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await factory.WithDbAsync(db => db.AuditLogs.CountAsync(a => a.ActorSubject == subject)));
    }

    [Fact]
    public async Task UnknownTenant_RequestedByAdministrator_Returns404()
    {
        var request = HttpRequestBuilder.MonthlyReport(
            TestData.ReportMonth, "dealer-unknown", HttpRequestBuilder.NewSubject(), tenantClaim: null, AppRoles.PlatformAdmin);

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
