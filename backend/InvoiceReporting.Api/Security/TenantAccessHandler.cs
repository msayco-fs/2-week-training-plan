using System.Text.RegularExpressions;
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
/// </summary>
public sealed partial class TenantAccessHandler : AuthorizationHandler<TenantAccessRequirement, TenantScope>
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
        var tenantClaimsValue = tenantClaims.Count == 1 ? tenantClaims[0].Value.Trim() : null;
        if (tenantClaims.Count == 1
            && TenantClaimPattern().IsMatch(tenantClaimsValue)
            && !string.IsNullOrEmpty(tenantClaimsValue)
            && string.Equals(tenantClaimsValue.ToLowerInvariant(), resource.TenantKey, StringComparison.Ordinal))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
    // Explicit A-Z, not RegexOptions.IgnoreCase: under the invariant culture
    // IgnoreCase also matches U+212A for 'k'. \z, not $, so a trailing "\n" fails.
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9-]{0,31}\z", RegexOptions.CultureInvariant)]
    private static partial Regex TenantClaimPattern();
}
