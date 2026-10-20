using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;

namespace InvoiceReporting.Api.Security;

/// <summary>
/// Grants <see cref="TenantAccessRequirement"/> when, and only when:
/// <list type="bullet">
/// <item>the caller is authenticated, and</item>
/// <item>the caller has the <see cref="AppRoles.PlatformAdmin"/> role claim, or</item>
/// <item>the caller has exactly one <see cref="TenantClaimTypes.TenantId"/> claim that,
/// after trimming, is a well-formed ASCII tenant key and equals the requested
/// tenant key ignoring case.</item>
/// </list>
/// Every other case is left unsatisfied, so the policy fails closed. Both the
/// role and the tenant claim come from the authenticated principal, which the
/// trusted identity provider issued; nothing here reads request input.
/// Every denial is logged as a <c>TenantAccessDenied</c> warning so that
/// cross-tenant probing can be detected, and is counted by
/// <see cref="TenantDenialMonitor"/>, which raises an alert on bursts.
/// </summary>
public sealed partial class TenantAccessHandler(ILogger<TenantAccessHandler> logger, TenantDenialMonitor denialMonitor)
    : AuthorizationHandler<TenantAccessRequirement, TenantScope>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        TenantAccessRequirement requirement,
        TenantScope resource)
    {
        if (context.User.Identity?.IsAuthenticated != true)
        {
            LogDenied(context.User, resource, "NotAuthenticated");
            return Task.CompletedTask;
        }

        if (string.IsNullOrEmpty(resource.TenantKey))
        {
            LogDenied(context.User, resource, "NoTenantRequested");
            return Task.CompletedTask;
        }

        if (context.User.IsInRole(AppRoles.PlatformAdmin))
        {
            context.Succeed(requirement);
            return Task.CompletedTask;
        }

        if (!TenantClaims.TryGetTenantKey(context.User, out var callerTenantKey))
        {
            LogDenied(context.User, resource, "MissingOrInvalidTenantClaim");
            return Task.CompletedTask;
        }

        if (!string.Equals(callerTenantKey, resource.TenantKey, StringComparison.Ordinal))
        {
            LogDenied(context.User, resource, "TenantMismatch");
            return Task.CompletedTask;
        }

        context.Succeed(requirement);
        return Task.CompletedTask;
    }

    // The requested key has already passed the resolver's strict pattern, and the
    // raw claim value is never logged, so log lines cannot be forged.
    private void LogDenied(ClaimsPrincipal user, TenantScope resource, string reason)
    {
        var subject = user.FindFirstValue(ClaimTypes.NameIdentifier);
        LogTenantAccessDenied(logger, subject, resource.TenantKey, reason);
        denialMonitor.RecordDenial(subject);
    }

    [LoggerMessage(EventId = 4030, EventName = "TenantAccessDenied", Level = LogLevel.Warning,
        Message = "Tenant access denied for subject {Subject} requesting tenant {RequestedTenant}: {Reason}")]
    private static partial void LogTenantAccessDenied(ILogger logger, string? subject, string? requestedTenant, string reason);
}
