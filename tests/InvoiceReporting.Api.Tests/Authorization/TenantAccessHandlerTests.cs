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

    [Fact]
    public async Task DeniedRequest_LogsOneWarning_WithSubjectRequestedTenantAndReason()
    {
        var logger = new CapturingLogger<TenantAccessHandler>();

        Assert.False(await AuthorizeAsync("dealer-alpha", logger, subject: "user-1"));

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal("TenantAccessDenied", entry.EventId.Name);
        Assert.Equal("user-1", entry.State["Subject"]);
        Assert.Equal(TenantKey, entry.State["RequestedTenant"]);
        Assert.Equal("TenantMismatch", entry.State["Reason"]);
    }

    [Fact]
    public async Task AllowedRequest_LogsNothing()
    {
        var logger = new CapturingLogger<TenantAccessHandler>();

        Assert.True(await AuthorizeAsync(TenantKey, logger));
        Assert.Empty(logger.Entries);
    }

    private static async Task<bool> AuthorizeAsync(
        string claim, CapturingLogger<TenantAccessHandler>? logger = null, string subject = "test-user")
    {
        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, subject), new Claim(TenantClaimTypes.TenantId, claim)],
            authenticationType: "Test");
        var requirement = new TenantAccessRequirement();
        var context = new AuthorizationHandlerContext([requirement], new ClaimsPrincipal(identity), new TenantScope(TenantKey));

        await new TenantAccessHandler(logger ?? new CapturingLogger<TenantAccessHandler>()).HandleAsync(context);
        return context.HasSucceeded;
    }
}
