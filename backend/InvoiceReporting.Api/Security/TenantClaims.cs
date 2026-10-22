using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;
using System.Text.RegularExpressions;

namespace InvoiceReporting.Api.Security;

public static partial class TenantClaims
{
    /// <summary>
    /// Reads the caller's tenant key from the authenticated principal. Succeeds only
    /// when there is exactly one <see cref="TenantClaimTypes.TenantId"/> claim that,
    /// after trimming, is a well-formed ASCII tenant key. The key is returned
    /// lower-case to match stored tenant keys.
    /// </summary>
    public static bool TryGetTenantKey(ClaimsPrincipal user, [NotNullWhen(true)] out string? tenantKey)
    {
        tenantKey = null;

        // More than one tenant claim is ambiguous, so it is rejected rather than
        // matched against any one of them.
        var claims = user.FindAll(TenantClaimTypes.TenantId).Take(2).ToList();
        if (claims.Count != 1)
        {
            return false;
        }

        var value = claims[0].Value.Trim();
        if (!TenantClaimPattern().IsMatch(value))
        {
            return false;
        }

        tenantKey = value.ToLowerInvariant();
        return true;
    }

    // Explicit A-Z, not RegexOptions.IgnoreCase: under the invariant culture
    // IgnoreCase also matches U+212A for 'k'. \z, not $, so a trailing "\n" fails.
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9-]{0,31}\z", RegexOptions.CultureInvariant)]
    private static partial Regex TenantClaimPattern();
}
