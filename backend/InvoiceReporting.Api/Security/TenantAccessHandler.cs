using Microsoft.AspNetCore.Authorization;

namespace InvoiceReporting.Api.Security;

/// <summary>
/// Grants <see cref="TenantAccessRequirement"/> when, and only when:
/// <list type="bullet">
/// <item>the caller is authenticated, and</item>
/// <item>the caller has the <see cref="AppRoles.PlatformAdmin"/> role claim, or</item>
/// <item>the caller has exactly one non-empty <see cref="TenantClaimTypes.TenantId"/>
/// claim whose value equals the requested tenant key (ordinal comparison).</item>
/// </list>
/// Every other case is left unsatisfied, so the policy fails closed. Both the
/// role and the tenant claim come from the authenticated principal, which the
/// trusted identity provider issued; nothing here reads request input.
/// </summary>
public sealed class TenantAccessHandler : AuthorizationHandler<TenantAccessRequirement, TenantScope>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        TenantAccessRequirement requirement,
        TenantScope resource)
    {
        if (context.User.Identity?.IsAuthenticated != true || string.IsNullOrEmpty(resource.TenantKey))
        {
            return Task.CompletedTask;
        }

        if (context.User.IsInRole(AppRoles.PlatformAdmin))
        {
            context.Succeed(requirement);
            return Task.CompletedTask;
        }

        // More than one tenant claim is ambiguous, so it is denied rather than
        // matched against any one of them.
        var tenantClaims = context.User.FindAll(TenantClaimTypes.TenantId).Take(2).ToList();
        if (tenantClaims.Count == 1
            && !string.IsNullOrEmpty(tenantClaims[0].Value)
            && string.Equals(tenantClaims[0].Value, resource.TenantKey, StringComparison.Ordinal))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
