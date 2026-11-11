using System.Net;
using System.Net.Http.Json;
using Hangfire;
using Hangfire.States;
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
    public async Task Trigger_ByPlatformAdmin_Returns202_QueuesTheReportJob_AndAuditsTheCaller()
    {
        using var client = _app.CreateClient();
        var subject = HttpRequestBuilder.NewSubject();

        var response = await client.SendAsync(Trigger(2018, 2, TestData.TenantAKey, subject, roles: AppRoles.PlatformAdmin));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<QuarterlyReportTriggerResponse>();
        Assert.NotNull(body);
        Assert.Equal(new QuarterlyReportTriggerResponse(TestData.TenantAKey, "2018-Q2", body.JobId), body);
        Assert.Equal($"/api/admin/quarterly-reports/runs/{body.JobId}", response.Headers.Location?.AbsolutePath);

        var tenantId = await TenantIdAsync(TestData.TenantAKey);
        var queued = Assert.Single(_jobs.Created);
        Assert.Equal(typeof(QuarterlyReportJob), queued.Type);
        Assert.Equal(nameof(QuarterlyReportJob.RunAsync), queued.Method.Name);
        Assert.Equal([tenantId, 2018, 2], queued.Args.Take(3));

        // Queued, not run: nothing is built during the request.
        Assert.False(File.Exists(Path.Combine(_outputDirectory, $"2018-Q2-tenant-{tenantId}.csv")));
        Assert.False(await GeneratedAuditExistsAsync(tenantId, "2018-Q2"));

        var trigger = Assert.Single(await AuditsBySubjectAsync(subject));
        Assert.Equal(AuditActions.TriggerQuarterlyInvoiceReport, trigger.Action);
        Assert.Equal(tenantId, trigger.TenantId);
        Assert.Equal("2018-Q2", trigger.Detail);
    }

    [Fact]
    public async Task GetRun_ForASucceededRun_ReturnsTheStateAndResult()
    {
        var tenantId = await TenantIdAsync(TestData.TenantAKey);
        var jobId = AddReportJob(tenantId, new SucceededState(QuarterlyReportRunResult.Generated, 1, 1));
        using var client = _app.CreateClient();

        var response = await client.SendAsync(GetRun(jobId, TestData.TenantAKey, roles: AppRoles.PlatformAdmin));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            new QuarterlyReportRunStatusResponse(jobId, TestData.TenantAKey, "2017-Q4", SucceededState.StateName, QuarterlyReportRunResult.Generated),
            await response.Content.ReadFromJsonAsync<QuarterlyReportRunStatusResponse>());
    }

    /// <summary>
    /// A failed run waits in the Scheduled state for its next retry (Hangfire's
    /// retry filter turns Failed into Scheduled until the retries run out).
    /// Scheduled a day ahead, so the test server never runs it.
    /// </summary>
    [Fact]
    public async Task GetRun_ForARunWaitingForARetry_ReturnsScheduled_WithNoResult()
    {
        var tenantId = await TenantIdAsync(TestData.TenantAKey);
        var jobId = AddReportJob(tenantId, new ScheduledState(TimeSpan.FromDays(1)));
        using var client = _app.CreateClient();

        var response = await client.SendAsync(GetRun(jobId, TestData.TenantAKey, roles: AppRoles.PlatformAdmin));

        var body = await response.Content.ReadFromJsonAsync<QuarterlyReportRunStatusResponse>();
        Assert.Equal(ScheduledState.StateName, body?.State);
        Assert.Null(body?.Result);
    }

    /// <summary>A job id must not reveal another tenant's run.</summary>
    [Fact]
    public async Task GetRun_ForAnotherTenantsRun_Returns404()
    {
        var tenantBId = await TenantIdAsync(TestData.TenantBKey);
        var jobId = AddReportJob(tenantBId, new SucceededState(QuarterlyReportRunResult.Generated, 1, 1));
        using var client = _app.CreateClient();

        var response = await client.SendAsync(GetRun(jobId, TestData.TenantAKey, roles: AppRoles.PlatformAdmin));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>A job id must not reveal other kinds of jobs, such as email jobs.</summary>
    [Fact]
    public async Task GetRun_ForAJobThatIsNotAReportRun_OrAnUnknownId_Returns404()
    {
        var tenantId = await TenantIdAsync(TestData.TenantAKey);
        var emailJob = new BackgroundJobClient(_app.Services.GetRequiredService<JobStorage>()).Create<QuarterlyReportEmailJob>(
            job => job.QueueRecipientsAsync(tenantId, 2017, 4, CancellationToken.None),
            new SucceededState(null, 1, 1));
        using var client = _app.CreateClient();

        var notAReport = await client.SendAsync(GetRun(emailJob, TestData.TenantAKey, roles: AppRoles.PlatformAdmin));
        var unknown = await client.SendAsync(GetRun("999999", TestData.TenantAKey, roles: AppRoles.PlatformAdmin));

        Assert.Equal(HttpStatusCode.NotFound, notAReport.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task GetRun_ByANonAdmin_Returns403()
    {
        var tenantId = await TenantIdAsync(TestData.TenantAKey);
        var jobId = AddReportJob(tenantId, new SucceededState(QuarterlyReportRunResult.Generated, 1, 1));
        using var client = _app.CreateClient();

        // Standard user of the run's own tenant: TenantAccess alone would allow this.
        var response = await client.SendAsync(GetRun(jobId, TestData.TenantAKey, tenantClaim: TestData.TenantAKey));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
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

    private static HttpRequestMessage GetRun(string jobId, string tenantKey, string? tenantClaim = null, params string[] roles)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/admin/quarterly-reports/runs/{jobId}");
        request.Headers.Add(TenantResolver.TenantHeaderName, tenantKey);
        request.Headers.Add(TestAuthHandler.SubjectHeader, HttpRequestBuilder.NewSubject());
        if (tenantClaim is not null)
        {
            request.Headers.Add(TestAuthHandler.TenantHeader, tenantClaim);
        }

        if (roles.Length > 0)
        {
            request.Headers.Add(TestAuthHandler.RolesHeader, string.Join(',', roles));
        }

        return request;
    }

    /// <summary>
    /// Adds a 2017-Q4 report run straight into the app's Hangfire storage, in a
    /// state the test server never picks up and runs (final, or scheduled far ahead).
    /// </summary>
    private string AddReportJob(int tenantId, IState state) =>
        new BackgroundJobClient(_app.Services.GetRequiredService<JobStorage>()).Create<QuarterlyReportJob>(
            job => job.RunAsync(tenantId, 2017, 4, CancellationToken.None), state);

    private Task<int> TenantIdAsync(string key) =>
        _factory.WithDbAsync(db => db.Tenants.Where(t => t.Key == key).Select(t => t.Id).SingleAsync());

    private Task<List<AuditLog>> AuditsBySubjectAsync(string subject) =>
        _factory.WithDbAsync(db => db.AuditLogs.AsNoTracking().Where(a => a.ActorSubject == subject).ToListAsync());

    private Task<bool> GeneratedAuditExistsAsync(int tenantId, string quarter) =>
        _factory.WithDbAsync(db => db.AuditLogs.AnyAsync(a =>
            a.TenantId == tenantId && a.Action == AuditActions.GenerateQuarterlyInvoiceReport && a.Detail == quarter));

    public void Dispose()
    {
        _app.Dispose();
        if (Directory.Exists(_outputDirectory))
        {
            Directory.Delete(_outputDirectory, recursive: true);
        }
    }
}
