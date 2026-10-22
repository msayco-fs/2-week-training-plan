using System.Text.RegularExpressions;
using InvoiceReporting.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace InvoiceReporting.Api.Tenancy;

public sealed partial class TenantResolver(ReportingDbContext db) : ITenantResolver
{
    /// <summary>Header carrying the requested tenant key.</summary>
    public const string TenantHeaderName = "X-Dealer-Id";

    public async Task<TenantResolutionResult> ResolveAsync(string? requestedTenantKey, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(requestedTenantKey))
        {
            return TenantResolutionResult.Missing;
        }

        // Keys are stored lower-case so lookups behave the same on every
        // database collation (SQL Server is case-insensitive, SQLite is not).
        var normalizedKey = requestedTenantKey.Trim().ToLowerInvariant();
        if (!TenantKeyPattern().IsMatch(normalizedKey))
        {
            return TenantResolutionResult.Malformed;
        }

        var tenant = await db.Tenants
            .AsNoTracking()
            .Where(t => t.Key == normalizedKey)
            .Select(t => new ResolvedTenant(t.Id, t.Key))
            .SingleOrDefaultAsync(cancellationToken);

        return tenant is null ? TenantResolutionResult.NotFound(normalizedKey) : TenantResolutionResult.Success(tenant);
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,31}$", RegexOptions.CultureInvariant)]
    private static partial Regex TenantKeyPattern();
}
