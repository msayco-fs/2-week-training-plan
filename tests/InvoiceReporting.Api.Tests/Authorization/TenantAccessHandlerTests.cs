using System.Security.Claims;
using InvoiceReporting.Api.Security;
using InvoiceReporting.Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Logging;

namespace InvoiceReporting.Api.Tests.Authorization;

/// <summary>
/// Direct tests of <see cref="TenantAccessHandler"/> for claim values that the
/// HTTP test harness cannot send, such as non-ASCII characters, and for the
/// denial log.
/// </summary>
public sealed class TenantAccessHandlerTests
{
    private const string TenantKey = "dealer-kilo";

    [Theory]
    [InlineData("dealer-Kilo")]   // Kelvin sign: ToLowerInvariant gives "dealer-kilo"
    [InlineData("dealer kilo")]
    [InlineData("dealer-kilo,dealer-alpha")]
    public async Task NonConformingTenantClaim_IsDenied_EvenWhenItLowercasesToTheKey(string claim)
    {
        Assert.False(await AuthorizeAsync(claim));
    }

    [Theory]
    [InlineData("dealer-kilo")]
    [InlineData("DEALER-KILO")]
    [InlineData(" Dealer-Kilo ")]
    public async Task ConformingTenantClaim_InAnyCase_IsAllowed(string claim)
    {
        Assert.True(await AuthorizeAsync(claim));
    }

    [Theory]
    [InlineData("dealer-alpha", "TenantMismatch")]
    [InlineData("dealer kilo", "MissingOrInvalidTenantClaim")]
    [InlineData(null, "MissingOrInvalidTenantClaim")]
    public async Task DeniedRequest_LogsOneWarning_WithSubjectRequestedTenantAndReason(string? claim, string reason)
    {
        var logger = new CapturingLogger<TenantAccessHandler>();

        Assert.False(await AuthorizeAsync(claim, logger, subject: "user-1"));

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal(4030, entry.EventId.Id);
        Assert.Equal("TenantAccessDenied", entry.EventId.Name);
        Assert.Equal("user-1", entry.State["Subject"]);
        Assert.Equal(TenantKey, entry.State["RequestedTenant"]);
        Assert.Equal(reason, entry.State["Reason"]);
    }

    [Fact]
    public async Task UnauthenticatedCaller_IsDenied_AndLogsNotAuthenticated_WithNoSubject()
    {
        var logger = new CapturingLogger<TenantAccessHandler>();

        // Anonymous: no authentication type and no claims, like a request with no token.
        Assert.False(await AuthorizeAsync(claim: null, logger, subject: null, authenticated: false));

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(4030, entry.EventId.Id);
        Assert.Null(entry.State["Subject"]);
        Assert.Equal(TenantKey, entry.State["RequestedTenant"]);
        Assert.Equal("NotAuthenticated", entry.State["Reason"]);
    }

    [Fact]
    public async Task EmptyRequestedTenant_IsDenied_AndLogsNoTenantRequested()
    {
        var logger = new CapturingLogger<TenantAccessHandler>();

        // Denied even though the caller has a valid tenant claim.
        Assert.False(await AuthorizeAsync(TenantKey, logger, subject: "user-1", requestedTenant: ""));

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(4030, entry.EventId.Id);
        Assert.Equal("user-1", entry.State["Subject"]);
        Assert.Equal("NoTenantRequested", entry.State["Reason"]);
    }

    [Theory]
    [InlineData(TenantKey, null)]                       // Matching tenant claim
    [InlineData("dealer-alpha", AppRoles.PlatformAdmin)] // Admin, other tenant's claim
    [InlineData(null, AppRoles.PlatformAdmin)]           // Admin, no tenant claim
    public async Task AllowedRequest_LogsNothing(string? claim, string? role)
    {
        var logger = new CapturingLogger<TenantAccessHandler>();
        var monitorLogger = new CapturingLogger<TenantDenialMonitor>();

        Assert.True(await AuthorizeAsync(claim, logger, monitor: TenantDenialMonitorTests.Create(monitorLogger, threshold: 1), role: role));
        Assert.Empty(logger.Entries);

        // Threshold 1 alerts on the first denial, so no alert proves nothing was counted.
        Assert.Empty(monitorLogger.Entries);
    }

    [Fact]
    public async Task RepeatedDenials_ForOneSubject_RaiseOneProbeAlert()
    {
        var monitorLogger = new CapturingLogger<TenantDenialMonitor>();
        var monitor = TenantDenialMonitorTests.Create(monitorLogger, threshold: 3);

        for (var i = 0; i < 4; i++)
        {
            Assert.False(await AuthorizeAsync("dealer-alpha", subject: "prober", monitor: monitor));
        }

        var alert = Assert.Single(monitorLogger.Entries);
        Assert.Equal("TenantAccessProbeSuspected", alert.EventId.Name);
        Assert.Equal("prober", alert.State["Subject"]);
    }

    private static async Task<bool> AuthorizeAsync(
        string? claim,
        CapturingLogger<TenantAccessHandler>? logger = null,
        string? subject = "test-user",
        TenantDenialMonitor? monitor = null,
        bool authenticated = true,
        string requestedTenant = TenantKey,
        string? role = null)
    {
        // A null claim or subject leaves that claim out entirely.
        List<Claim> claims = [];
        if (subject is not null)
        {
            claims.Add(new Claim(ClaimTypes.NameIdentifier, subject));
        }

        if (claim is not null)
        {
            claims.Add(new Claim(TenantClaimTypes.TenantId, claim));
        }

        if (role is not null)
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }

        // An identity without an authentication type is not authenticated.
        var identity = new ClaimsIdentity(claims, authenticationType: authenticated ? "Test" : null);
        var requirement = new TenantAccessRequirement();
        var context = new AuthorizationHandlerContext([requirement], new ClaimsPrincipal(identity), new TenantScope(requestedTenant));

        var handler = new TenantAccessHandler(
            logger ?? new CapturingLogger<TenantAccessHandler>(),
            monitor ?? TenantDenialMonitorTests.Create());
        await handler.HandleAsync(context);
        return context.HasSucceeded;
    }
}
