using InvoiceReporting.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace InvoiceReporting.Api.Data;

/// <summary>
/// Inserts fictitious tenants and invoices into an empty local development
/// database. Only called from the Development environment.
/// </summary>
public static class DevelopmentDataSeeder
{
    public static async Task SeedAsync(ReportingDbContext db, CancellationToken cancellationToken = default)
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
