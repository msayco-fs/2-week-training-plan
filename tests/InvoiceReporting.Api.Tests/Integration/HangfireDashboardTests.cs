using System.Net;
using InvoiceReporting.Api.Security;
using InvoiceReporting.Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace InvoiceReporting.Api.Tests.Integration;

/// <summary>
/// Tests run in the "Testing" environment, so the dashboard needs both a
/// local request and a PlatformAdmin caller. Test requests have no remote
/// address; <see cref="LoopbackHeader"/> makes one look local.
/// </summary>
public sealed class HangfireDashboardTests(InvoiceReportApiFactory factory) : IClassFixture<InvoiceReportApiFactory>
{
    private const string LoopbackHeader = "X-Test-Loopback";

    [Fact]
    public async Task Dashboard_WithoutAuthentication_Returns401()
    {
        using var client = Dashboard(factory).CreateClient();

        var response = await client.SendAsync(Request(subject: null, local: true));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData(TestData.TenantAKey)]
    [InlineData(null, "platformadmin")]
    [InlineData(null, "Admin")]
    [InlineData(TestData.TenantAKey, "TenantAdmin")]
    public async Task Dashboard_ForANonAdmin_Returns403(string? tenantClaim, params string[] roles)
    {
        using var client = Dashboard(factory).CreateClient();

        var response = await client.SendAsync(Request(HttpRequestBuilder.NewSubject(), local: true, tenantClaim, roles));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Dashboard_ForAPlatformAdmin_FromAnotherMachine_Returns403()
    {
        using var client = Dashboard(factory).CreateClient();

        var response = await client.SendAsync(Request(HttpRequestBuilder.NewSubject(), local: false, roles: AppRoles.PlatformAdmin));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Dashboard_ForALocalPlatformAdmin_Opens()
    {
        using var client = Dashboard(factory).CreateClient();

        var response = await client.SendAsync(Request(HttpRequestBuilder.NewSubject(), local: true, roles: AppRoles.PlatformAdmin));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>The open-dashboard setting is for Development only; elsewhere it is ignored.</summary>
    [Fact]
    public async Task Dashboard_OutsideDevelopment_IgnoresTheAllowAnyCallerSetting()
    {
        using var app = Dashboard(factory, allowAnyCaller: true);
        using var client = app.CreateClient();

        var response = await client.SendAsync(Request(subject: null, local: true));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static WebApplicationFactory<Program> Dashboard(InvoiceReportApiFactory factory, bool allowAnyCaller = false) =>
        factory.WithWebHostBuilder(builder =>
        {
            if (allowAnyCaller)
            {
                builder.UseSetting("Hangfire:Dashboard:AllowAnyCallerInDevelopment", "true");
            }

            builder.ConfigureTestServices(services => services.AddSingleton<IStartupFilter, LoopbackStartupFilter>());
        });

    private static HttpRequestMessage Request(string? subject, bool local, string? tenantClaim = null, params string[] roles)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/hangfire");
        if (local)
        {
            request.Headers.Add(LoopbackHeader, "1");
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

    /// <summary>Runs before the app's middleware and gives marked requests a loopback remote address.</summary>
    private sealed class LoopbackStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((HttpContext context, RequestDelegate nextMiddleware) =>
            {
                if (context.Request.Headers.ContainsKey(LoopbackHeader))
                {
                    context.Connection.RemoteIpAddress = IPAddress.Loopback;
                }

                return nextMiddleware(context);
            });
            next(app);
        };
    }
}
