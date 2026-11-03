using InvoiceReporting.Api.Data.Repositories;
using InvoiceReporting.Api.Domain;
using InvoiceReporting.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace InvoiceReporting.Api.Tests.Integration;

/// <summary>
/// Tests share one database, and claims are unique per tenant, user and
/// quarter, so each test uses its own quarter.
/// </summary>
public sealed class EmailDeliveryRepositoryTests(InvoiceReportApiFactory factory)
    : IClassFixture<InvoiceReportApiFactory>
{
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);

    [Fact]
    public async Task TryClaimAsync_WithNoEarlierClaim_ClaimsWithoutTakeover()
    {
        var (tenantId, userId) = await TenantAAndUserAsync();

        var claim = await ClaimAsync(tenantId, userId, "2023-Q1");

        Assert.Equal(EmailDeliveryClaimResult.Claimed, claim.Result);
        Assert.False(claim.TookOverExpiredClaim);
    }

    [Fact]
    public async Task TryClaimAsync_OverAnExpiredClaim_ReportsTheTakeover()
    {
        var (tenantId, userId) = await TenantAAndUserAsync();
        await InsertDeliveryAsync(tenantId, userId, "2023-Q2", EmailDeliveryStatus.Sending, claimedAgo: Lease + TimeSpan.FromMinutes(1));

        var claim = await ClaimAsync(tenantId, userId, "2023-Q2");

        Assert.Equal(EmailDeliveryClaimResult.Claimed, claim.Result);
        Assert.True(claim.TookOverExpiredClaim);
    }

    [Fact]
    public async Task TryClaimAsync_OverAFreshClaim_IsInProgress()
    {
        var (tenantId, userId) = await TenantAAndUserAsync();
        await InsertDeliveryAsync(tenantId, userId, "2023-Q3", EmailDeliveryStatus.Sending, claimedAgo: TimeSpan.FromSeconds(10));

        var claim = await ClaimAsync(tenantId, userId, "2023-Q3");

        Assert.Equal(EmailDeliveryClaimResult.InProgress, claim.Result);
        Assert.False(claim.TookOverExpiredClaim);
    }

    [Fact]
    public async Task TryClaimAsync_WhenAlreadySent_IsAlreadySent()
    {
        var (tenantId, userId) = await TenantAAndUserAsync();
        await InsertDeliveryAsync(tenantId, userId, "2023-Q4", EmailDeliveryStatus.Sent, claimedAgo: Lease + TimeSpan.FromMinutes(1));

        var claim = await ClaimAsync(tenantId, userId, "2023-Q4");

        Assert.Equal(EmailDeliveryClaimResult.AlreadySent, claim.Result);
        Assert.False(claim.TookOverExpiredClaim);
    }

    private Task<EmailDeliveryClaim> ClaimAsync(int tenantId, int userId, string quarter) =>
        factory.WithDbAsync(db => new EmailDeliveryRepository(db)
            .TryClaimAsync(tenantId, userId, quarter, DateTime.UtcNow, Lease, CancellationToken.None));

    private Task InsertDeliveryAsync(int tenantId, int userId, string quarter, EmailDeliveryStatus status, TimeSpan claimedAgo) =>
        factory.WithDbAsync(async db =>
        {
            db.QuarterlyReportEmailDeliveries.Add(new QuarterlyReportEmailDelivery
            {
                TenantId = tenantId,
                UserId = userId,
                Quarter = quarter,
                Status = status,
                ClaimToken = Guid.NewGuid(),
                ClaimedAtUtc = DateTime.UtcNow - claimedAgo,
                SentAtUtc = status == EmailDeliveryStatus.Sent ? DateTime.UtcNow : null,
            });
            return await db.SaveChangesAsync();
        });

    private Task<(int TenantId, int UserId)> TenantAAndUserAsync() =>
        factory.WithDbAsync(async db => (
            await db.Tenants.Where(t => t.Key == TestData.TenantAKey).Select(t => t.Id).SingleAsync(),
            await db.Users.Where(u => u.Subject == TestData.AlphaUserSubject).Select(u => u.Id).SingleAsync()));
}
