using InvoiceReporting.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace InvoiceReporting.Api.Data;

/// <summary>
/// Inserts fictitious tenants, invoices and report recipients into an empty
/// local development database. Only called from the Development environment.
/// </summary>
public static class DevelopmentDataSeeder
{
    public static async Task SeedAsync(ReportingDbContext db, CancellationToken cancellationToken = default)
    {
        await SeedTenantsAsync(db, cancellationToken);

        // Separate check, so databases created before users existed get them too.
        await SeedUsersAsync(db, cancellationToken);
    }

    private static async Task SeedUsersAsync(ReportingDbContext db, CancellationToken cancellationToken)
    {
        if (await db.Users.AnyAsync(cancellationToken))
        {
            return;
        }

        var alpha = await db.Tenants.SingleAsync(t => t.Key == "dealer-alpha", cancellationToken);
        var bravo = await db.Tenants.SingleAsync(t => t.Key == "dealer-bravo", cancellationToken);

        var admin = new User { Subject = "dev-admin", Email = "admin@example.com", IsPlatformAdmin = true };
        admin.TenantAssignments.Add(new UserTenantAssignment { Tenant = alpha });

        var users = new (User User, bool WillReceiveReport)[]
        {
            (new User { Subject = "dev-alpha-user", Email = "alpha.user@example.com", Tenant = alpha }, true),
            (new User { Subject = "dev-alpha-optout", Email = "alpha.optout@example.com", Tenant = alpha }, false),
            (new User { Subject = "dev-bravo-user", Email = "bravo.user@example.com", Tenant = bravo }, true),
            (admin, true),
        };

        foreach (var (user, willReceiveReport) in users)
        {
            user.QuarterlyReportRecipient = new QuarterlyReportRecipient { WillReceiveReport = willReceiveReport };
            db.Users.Add(user);
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private static async Task SeedTenantsAsync(ReportingDbContext db, CancellationToken cancellationToken)
    {
        if (await db.Tenants.AnyAsync(cancellationToken))
        {
            return;
        }

        var alpha = new Tenant { Key = "dealer-alpha", DisplayName = "Alpha Motors (fictitious)" };
        var bravo = new Tenant { Key = "dealer-bravo", DisplayName = "Bravo Auto Group (fictitious)" };

        alpha.Invoices.Add(new Invoice { CustomerName = "Sample Customer 001", Amount = 1200.00m, IssuedDate = new DateOnly(2026, 9, 3) });
        alpha.Invoices.Add(new Invoice { CustomerName = "Sample Customer 002", Amount = 349.99m, IssuedDate = new DateOnly(2026, 9, 17) });
        alpha.Invoices.Add(new Invoice { CustomerName = "Sample Customer 003", Amount = 80.50m, IssuedDate = new DateOnly(2026, 10, 2) });
        bravo.Invoices.Add(new Invoice { CustomerName = "Sample Customer 101", Amount = 5000.00m, IssuedDate = new DateOnly(2026, 9, 9) });
        bravo.Invoices.Add(new Invoice { CustomerName = "Sample Customer 102", Amount = 725.25m, IssuedDate = new DateOnly(2026, 9, 28) });

        db.Tenants.AddRange(alpha, bravo);
        await db.SaveChangesAsync(cancellationToken);
    }
}
