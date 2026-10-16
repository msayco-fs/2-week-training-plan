using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace InvoiceReporting.Api.Tests.Infrastructure;

/// <summary>
/// Deterministic authentication for integration tests only. It builds the
/// principal from test-only request headers so each test controls its own
/// identity without a live identity provider. This handler is registered by
/// <see cref="InvoiceReportApiFactory"/> and never by the application itself.
/// </summary>
public sealed class TestAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Test";
    public const string SubjectHeader = "X-Test-Subject";
    public const string TenantHeader = "X-Test-Tenant";
    public const string RolesHeader = "X-Test-Roles";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var subject = Request.Headers[SubjectHeader].ToString();
        if (string.IsNullOrEmpty(subject))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, subject) };

        // Comma-separated values become separate tenant_id claims, and a present
        // but empty header becomes one empty claim, so tests can exercise the
        // ambiguous and blank-claim cases.
        if (Request.Headers.TryGetValue(TenantHeader, out var tenantValues))
        {
            foreach (var tenant in tenantValues.ToString().Split(','))
            {
                claims.Add(new Claim(Security.TenantClaimTypes.TenantId, tenant.Trim()));
            }
        }

        foreach (var role in Request.Headers[RolesHeader].ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }

        var identity = new ClaimsIdentity(claims, SchemeName);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
