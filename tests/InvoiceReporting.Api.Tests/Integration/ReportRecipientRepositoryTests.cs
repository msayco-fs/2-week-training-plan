using InvoiceReporting.Api.Data;
using InvoiceReporting.Api.Data.Repositories;
using InvoiceReporting.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace InvoiceReporting.Api.Tests.Integration;

public sealed class ReportRecipientRepositoryTests(InvoiceReportApiFactory factory)
    : IClassFixture<InvoiceReportApiFactory>
{
    [Fact]
    public async Task TenantA_GetsItsOptedInStandardUser_AndTheAdminAssignedToIt()
    {
        var subjects = await RecipientSubjectsAsync(TestData.TenantAKey);

        Assert.Equal(Sorted(TestData.AlphaUserSubject, TestData.AdminAssignedToASubject), subjects);
    }

    [Fact]
    public async Task TenantB_GetsOnlyItsOwnStandardUsers()
    {
        var subjects = await RecipientSubjectsAsync(TestData.TenantBKey);

        Assert.Equal(Sorted(TestData.BravoUserSubject, TestData.BravoUserWithStrayAssignmentSubject), subjects);
    }

    [Theory]
    [InlineData(TestData.AlphaOptOutSubject)] // Opted out.
    [InlineData(TestData.BravoUserSubject)] // Other tenant's user.
    [InlineData(TestData.BravoUserWithStrayAssignmentSubject)] // Assignment rows only count for admins.
    [InlineData(TestData.AdminWithoutAssignmentsSubject)] // Admin, but not assigned to tenant A.
    public async Task GetRecipientEmailAsync_ReturnsNull_ForUsersWhoMustNotGetTenantAsReport(string subject)
    {
        var email = await factory.WithDbAsync(async db =>
        {
            var tenantId = await TenantIdAsync(db, TestData.TenantAKey);
            var userId = await db.Users.Where(u => u.Subject == subject).Select(u => u.Id).SingleAsync();
            return await new ReportRecipientRepository(db).GetRecipientEmailAsync(tenantId, userId, CancellationToken.None);
        });

        Assert.Null(email);
    }

    [Fact]
    public async Task GetRecipientEmailAsync_ReturnsTheStoredEmail_ForARecipient()
    {
        var email = await factory.WithDbAsync(async db =>
        {
            var tenantId = await TenantIdAsync(db, TestData.TenantAKey);
            var userId = await db.Users.Where(u => u.Subject == TestData.AdminAssignedToASubject).Select(u => u.Id).SingleAsync();
            return await new ReportRecipientRepository(db).GetRecipientEmailAsync(tenantId, userId, CancellationToken.None);
        });

        Assert.Equal("admin.a@example.com", email);
    }

    private Task<List<string>> RecipientSubjectsAsync(string tenantKey) =>
        factory.WithDbAsync(async db =>
        {
            var tenantId = await TenantIdAsync(db, tenantKey);
            var userIds = await new ReportRecipientRepository(db).GetRecipientUserIdsAsync(tenantId, CancellationToken.None);
            var subjects = await db.Users
                .Where(u => userIds.Contains(u.Id))
                .Select(u => u.Subject)
                .ToListAsync();
            return Sorted([.. subjects]);
        });

    // Row ids depend on EF insert order, so compare subjects in a fixed order.
    private static List<string> Sorted(params string[] subjects) =>
        [.. subjects.Order(StringComparer.Ordinal)];

    private static Task<int> TenantIdAsync(ReportingDbContext db, string tenantKey) =>
        db.Tenants.Where(t => t.Key == tenantKey).Select(t => t.Id).SingleAsync();
}
