using InvoiceReporting.Api.Tenancy;
using InvoiceReporting.Api.Tests.Infrastructure;

namespace InvoiceReporting.Api.Tests.Tenancy;

public sealed class TenantResolverTests(InvoiceReportApiFactory factory) : IClassFixture<InvoiceReportApiFactory>
{
    private Task<TenantResolutionResult> ResolveAsync(string? key) =>
        factory.WithDbAsync(db => new TenantResolver(db).ResolveAsync(key, CancellationToken.None));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Resolve_MissingKey_ReturnsMissing(string? key)
    {
        var result = await ResolveAsync(key);

        Assert.Equal(TenantResolutionStatus.Missing, result.Status);
        Assert.Null(result.Tenant);
    }

    [Theory]
    [InlineData("dealer alpha")]
    [InlineData("-dealer")]
    [InlineData("dealer_alpha")]
    [InlineData("dealer-alpha;Database=master")]
    [InlineData("abcdefghijklmnopqrstuvwxyz0123456")] // 33 characters
    public async Task Resolve_MalformedKey_ReturnsMalformed(string key)
    {
        var result = await ResolveAsync(key);

        Assert.Equal(TenantResolutionStatus.Malformed, result.Status);
        Assert.Null(result.Tenant);
    }

    [Fact]
    public async Task Resolve_UnknownKey_ReturnsNotFound()
    {
        var result = await ResolveAsync("dealer-unknown");

        Assert.Equal(TenantResolutionStatus.NotFound, result.Status);
        Assert.Null(result.Tenant);
    }

    [Theory]
    [InlineData(TestData.TenantAKey)]
    [InlineData("  DEALER-ALPHA ")]
    public async Task Resolve_KnownKey_ReturnsNormalizedTenant(string key)
    {
        var result = await ResolveAsync(key);

        Assert.Equal(TenantResolutionStatus.Resolved, result.Status);
        Assert.NotNull(result.Tenant);
        Assert.Equal(TestData.TenantAKey, result.Tenant.Key);
    }
}
