using System.Net;
using System.Net.Http.Json;
using Hangfire;
using InvoiceReporting.Api.Controllers;
using InvoiceReporting.Api.Data.Repositories;
using InvoiceReporting.Api.Domain;
using InvoiceReporting.Api.Reporting;
using InvoiceReporting.Api.Security;
using InvoiceReporting.Api.Tenancy;
using InvoiceReporting.Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace InvoiceReporting.Api.Tests.Integration;

/// <summary>
/// The admin trigger over HTTP, through the real authentication pipeline, the
/// real <c>[Authorize(Roles = PlatformAdmin)]</c> check and the real
/// <see cref="AuthorizationPolicies.TenantAccess"/> policy. Nothing about
/// authorization is mocked; only the identity comes from <see cref="TestAuthHandler"/>.
/// </summary>
public sealed class QuarterlyReportTriggerEndpointTests : IClassFixture<InvoiceReportApiFactory>, IDisposable
{
    private const string UnknownTenantKey = "dealer-zulu";

    private readonly InvoiceReportApiFactory _factory;
    private readonly WebApplicationFactory<Program> _app;
    private readonly FakeBackgroundJobClient _jobs = new();
    private readonly string _outputDirectory =
        Path.Combine(Path.GetTempPath(), "invoice-reporting-tests", Guid.NewGuid().ToString("N"));

    public QuarterlyReportTriggerEndpointTests(InvoiceReportApiFactory factory)
    {
        _factory = factory;
        _app = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting($"{QuarterlyReportOutputOptions.SectionName}:Directory", _outputDirectory);
            builder.ConfigureTestServices(services =>
            {
                // Records the queued email jobs instead of running them.
                services.RemoveAll<IBackgroundJobClient>();
                services.AddSingleton<IBackgroundJobClient>(_jobs);
            });
        });
    }

    public static TheoryData<string?, string, string[]> NotAdminCallers => new()
    {
        // Standard user, own tenant: TenantAccess alone would allow this.
        { TestData.TenantAKey, TestData.TenantAKey, [] },
        // Standard user, another tenant.
        { TestData.TenantAKey, TestData.TenantBKey, [] },
        // No tenant claim, no role.
        { null, TestData.TenantAKey, [] },
        // Role look-alikes are not the PlatformAdmin role.
        { null, TestData.TenantAKey, ["platformadmin"] },
        { null, TestData.TenantAKey, ["Admin"] },
        { TestData.TenantAKey, TestData.TenantAKey, ["TenantAdmin"] },
    };

    [Fact]
    public async Task Trigger_WithoutAuthentication_Returns401_AndRunsNothing()
    {
        using var client = _app.CreateClient();

        var response = await client.SendAsync(Trigger(2018, 1, TestData.TenantAKey, subject: null));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(_jobs.Created);
        Assert.False(Directory.Exists(_outputDirectory));
    }

    [Theory]
    [MemberData(nameof(NotAdminCallers))]
    public async Task Trigger_ByANonAdmin_Returns403_AndRunsNothing(string? tenantClaim, string requestedTenant, string[] roles)
    {
        using var client = _app.CreateClient();
        var subject = HttpRequestBuilder.NewSubject();

        var response = await client.SendAsync(Trigger(2018, 1, requestedTenant, subject, tenantClaim, roles));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(_jobs.Created);
        Assert.False(Directory.Exists(_outputDirectory));
        Assert.Empty(await AuditsBySubjectAsync(subject));
    }

    [Fact]
    public async Task Trigger_ByPlatformAdmin_BuildsTheReport_QueuesEmails_AndAuditsTheCaller()
    {
        using var client = _app.CreateClient();
        var subject = HttpRequestBuilder.NewSubject();

        var response = await client.SendAsync(Trigger(2018, 2, TestData.TenantAKey, subject, roles: AppRoles.PlatformAdmin));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<QuarterlyReportTriggerResponse>();
        Assert.Equal(new QuarterlyReportTriggerResponse(TestData.TenantAKey, "2018-Q2", QuarterlyReportRunResult.Generated), body);

        var tenantId = await TenantIdAsync(TestData.TenantAKey);
        Assert.True(File.Exists(Path.Combine(_outputDirectory, $"2018-Q2-tenant-{tenantId}.csv")));
        var queued = Assert.Single(_jobs.Created);
        Assert.Equal(nameof(QuarterlyReportEmailJob.QueueRecipientsAsync), queued.Method.Name);

        var trigger = Assert.Single(await AuditsBySubjectAsync(subject));
        Assert.Equal(AuditActions.TriggerQuarterlyInvoiceReport, trigger.Action);
        Assert.Equal(tenantId, trigger.TenantId);
        Assert.Equal("2018-Q2", trigger.Detail);
        Assert.True(await GeneratedAuditExistsAsync(tenantId, "2018-Q2"));
    }

    [Fact]
    public async Task Trigger_ByPlatformAdmin_ForAQuarterAlreadyReported_ReturnsAlreadyGenerated()
    {
        using var client = _app.CreateClient();

        var first = await client.SendAsync(Trigger(2018, 3, TestData.TenantBKey, HttpRequestBuilder.NewSubject(), roles: AppRoles.PlatformAdmin));
        var second = await client.SendAsync(Trigger(2018, 3, TestData.TenantBKey, HttpRequestBuilder.NewSubject(), roles: AppRoles.PlatformAdmin));

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var body = await second.Content.ReadFromJsonAsync<QuarterlyReportTriggerResponse>();
        Assert.Equal(QuarterlyReportRunResult.AlreadyGenerated, body!.Result);
    }

    [Fact]
    public async Task Trigger_ByPlatformAdmin_ForAnUnknownTenant_Returns404()
    {
        using var client = _app.CreateClient();

        var response = await client.SendAsync(Trigger(2018, 1, UnknownTenantKey, HttpRequestBuilder.NewSubject(), roles: AppRoles.PlatformAdmin));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(_jobs.Created);
    }

    [Theory]
    [InlineData(2018, 0, TestData.TenantAKey)]
    [InlineData(2018, 5, TestData.TenantAKey)]
    [InlineData(1999, 1, TestData.TenantAKey)]
    [InlineData(2018, 1, null)]
    [InlineData(2018, 1, "Not A Key!")]
    public async Task Trigger_ByPlatformAdmin_WithBadInput_Returns400(int year, int quarter, string? tenantKey)
    {
        using var client = _app.CreateClient();

        var response = await client.SendAsync(Trigger(year, quarter, tenantKey, HttpRequestBuilder.NewSubject(), roles: AppRoles.PlatformAdmin));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_jobs.Created);
    }

    /// <summary>
    /// The client gives up mid-run. The request-aborted token must reach the
    /// repository inside the job, and the job must stop without writing the
    /// CSV, the "generated" audit entry or the email jobs.
    /// </summary>
    [Fact]
    public async Task Trigger_WhenTheClientCancels_CancelsTheRunningJob_AndRecordsNoReport()
    {
        var invoices = new BlockingInvoiceRepository();
        using var app = _app.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IInvoiceRepository>();
            services.AddSingleton<IInvoiceRepository>(invoices);
        }));
        using var client = app.CreateClient();
        var subject = HttpRequestBuilder.NewSubject();
        using var cts = new CancellationTokenSource();

        var call = client.SendAsync(Trigger(2018, 4, TestData.TenantAKey, subject, roles: AppRoles.PlatformAdmin), cts.Token);
        await invoices.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        Assert.True(await invoices.SawCancellation.Task.WaitAsync(TimeSpan.FromSeconds(10)));

        var tenantId = await TenantIdAsync(TestData.TenantAKey);
        Assert.False(File.Exists(Path.Combine(_outputDirectory, $"2018-Q4-tenant-{tenantId}.csv")));
        Assert.False(await GeneratedAuditExistsAsync(tenantId, "2018-Q4"));
        Assert.Empty(_jobs.Created);
        // The trigger itself is still traceable to the administrator.
        Assert.Equal(AuditActions.TriggerQuarterlyInvoiceReport, Assert.Single(await AuditsBySubjectAsync(subject)).Action);
    }

    private static HttpRequestMessage Trigger(
        int year, int quarter, string? tenantKey, string? subject, string? tenantClaim = null, params string[] roles)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/admin/quarterly-reports/{year}/{quarter}");
        if (tenantKey is not null)
        {
            request.Headers.Add(TenantResolver.TenantHeaderName, tenantKey);
        }

        if (subject is not null)
        {
            request.Headers.Add(TestAuthHandler.SubjectHeader, subject);
            if (tenantClaim is not null)
            {
                request.Headers.Add(TestAuthHandler.TenantHeader, tenantClaim);
            }

            if (roles.Length > 0)
            {
                request.Headers.Add(TestAuthHandler.RolesHeader, string.Join(',', roles));
            }
        }

        return request;
    }

    private Task<int> TenantIdAsync(string key) =>
        _factory.WithDbAsync(db => db.Tenants.Where(t => t.Key == key).Select(t => t.Id).SingleAsync());

    private Task<List<AuditLog>> AuditsBySubjectAsync(string subject) =>
        _factory.WithDbAsync(db => db.AuditLogs.AsNoTracking().Where(a => a.ActorSubject == subject).ToListAsync());

    private Task<bool> GeneratedAuditExistsAsync(int tenantId, string quarter) =>
        _factory.WithDbAsync(db => db.AuditLogs.AnyAsync(a =>
            a.TenantId == tenantId && a.Action == AuditActions.GenerateQuarterlyInvoiceReport && a.Detail == quarter));

    /// <summary>Waits on the query until its token is cancelled, and reports that it was.</summary>
    private sealed class BlockingInvoiceRepository : IInvoiceRepository
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> SawCancellation { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<IReadOnlyList<InvoiceSummary>> GetSummariesAsync(
            int tenantId, DateOnly fromInclusive, DateOnly toExclusive, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                SawCancellation.TrySetResult(true);
                throw;
            }

            return [];
        }
    }

    public void Dispose()
    {
        _app.Dispose();
        if (Directory.Exists(_outputDirectory))
        {
            Directory.Delete(_outputDirectory, recursive: true);
        }
    }
}
