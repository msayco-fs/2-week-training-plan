using System.Security.Claims;
using InvoiceReporting.Api.Security;
using Microsoft.AspNetCore.Authorization;

namespace InvoiceReporting.Api.Tests.Authorization;

/// <summary>
/// Direct tests of <see cref="TenantAccessHandler"/> for claim values that the
/// HTTP test harness cannot send, such as non-ASCII characters.
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

    private static async Task<bool> AuthorizeAsync(string claim)
    {
        var identity = new ClaimsIdentity([new Claim(TenantClaimTypes.TenantId, claim)], authenticationType: "Test");
        var requirement = new TenantAccessRequirement();
        var context = new AuthorizationHandlerContext([requirement], new ClaimsPrincipal(identity), new TenantScope(TenantKey));

        await new TenantAccessHandler().HandleAsync(context);
        return context.HasSucceeded;
    }
}
