using InvoiceReporting.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace InvoiceReporting.Api.Data.Repositories;

public sealed class ReportRecipientRepository(ReportingDbContext db) : IReportRecipientRepository
{
    public async Task<IReadOnlyList<int>> GetRecipientUserIdsAsync(int tenantId, CancellationToken cancellationToken) =>
        await Recipients(tenantId)
            .OrderBy(u => u.Id)
            .Select(u => u.Id)
            .ToListAsync(cancellationToken);

    public Task<string?> GetRecipientEmailAsync(int tenantId, int userId, CancellationToken cancellationToken) =>
        Recipients(tenantId)
            .Where(u => u.Id == userId)
            .Select(u => u.Email)
            .SingleOrDefaultAsync(cancellationToken);

    // One rule for both queries, so the user list and the send-time check
    // cannot disagree.
    private IQueryable<User> Recipients(int tenantId) =>
        db.Users
            .AsNoTracking()
            .Where(u => u.QuarterlyReportRecipient != null && u.QuarterlyReportRecipient.WillReceiveReport)
            .Where(u => u.IsPlatformAdmin
                ? u.TenantAssignments.Any(a => a.TenantId == tenantId)
                : u.TenantId == tenantId);
}
