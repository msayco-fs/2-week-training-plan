using Hangfire.Dashboard;

namespace InvoiceReporting.Api.Security;

/// <summary>
/// Opens the Hangfire dashboard only to an authenticated caller with the
/// <see cref="AppRoles.PlatformAdmin"/> role. The dashboard can trigger and
/// delete jobs for every tenant, so this is the same bar as the admin
/// trigger endpoint.
/// </summary>
/// <remarks>
/// The principal comes from the app's normal authentication (JWT bearer),
/// which runs before the dashboard middleware. Hangfire answers 401 when the
/// caller is not authenticated and 403 when they are but this check fails.
/// </remarks>
public sealed class PlatformAdminDashboardAuthorizationFilter : IDashboardAuthorizationFilter
{
    public bool Authorize(DashboardContext context)
    {
        var user = context.GetHttpContext().User;
        return user.Identity?.IsAuthenticated == true && user.IsInRole(AppRoles.PlatformAdmin);
    }
}
