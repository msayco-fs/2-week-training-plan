using Hangfire.Dashboard;

namespace InvoiceReporting.Api.Security;

/// <summary>
/// Lets any caller open the Hangfire dashboard. Development only: Hangfire's
/// default filter allows only requests from the same machine, and requests
/// through Docker's port mapping come from the Docker network instead.
/// </summary>
/// <remarks>
/// Used only in Development with <c>Hangfire:Dashboard:AllowAnyCallerInDevelopment</c>
/// set to true (see appsettings.Development.json); otherwise
/// <see cref="PlatformAdminDashboardAuthorizationFilter"/> applies.
/// Never register this outside Development. The dashboard can trigger and
/// delete jobs. docker-compose.yml publishes the API on 127.0.0.1 only, so the
/// dashboard is still not reachable from other machines.
/// </remarks>
public sealed class DevelopmentDashboardAuthorizationFilter : IDashboardAuthorizationFilter
{
    public bool Authorize(DashboardContext context) => true;
}
