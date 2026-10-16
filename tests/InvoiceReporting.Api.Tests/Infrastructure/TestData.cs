using InvoiceReporting.Api.Data;
using InvoiceReporting.Api.Domain;

namespace InvoiceReporting.Api.Tests.Infrastructure;

/// <summary>Fictitious, fixed test data. Amounts are chosen so totals are easy to check.</summary>
public static class TestData
{
    public const string TenantAKey = "dealer-alpha";
    public const string TenantBKey = "dealer-bravo";

    public const string ReportMonth = "2026-09";

    /// <summary>Tenant A, September 2026: 100.00 + 250.50 + 49.50.</summary>
    public const decimal TenantASeptemberTotal = 400.00m;

    /// <summary>Tenant B, September 2026: 1000.00 + 2000.00.</summary>
    public const decimal TenantBSeptemberTotal = 3000.00m;

    public static void Seed(ReportingDbContext db)
    {
        var tenantA = new Tenant { Key = TenantAKey, DisplayName = "Tenant A (test)" };
        var tenantB = new Tenant { Key = TenantBKey, DisplayName = "Tenant B (test)" };

        tenantA.Invoices.Add(new Invoice { CustomerName = "A-Customer-1", Amount = 100.00m, IssuedDate = new DateOnly(2026, 9, 1) });
        tenantA.Invoices.Add(new Invoice { CustomerName = "A-Customer-2", Amount = 250.50m, IssuedDate = new DateOnly(2026, 9, 15) });
        tenantA.Invoices.Add(new Invoice { CustomerName = "A-Customer-3", Amount = 49.50m, IssuedDate = new DateOnly(2026, 9, 30) });
        // Boundary rows outside September must be excluded from the September report.
        tenantA.Invoices.Add(new Invoice { CustomerName = "A-Customer-Aug", Amount = 999.99m, IssuedDate = new DateOnly(2026, 8, 31) });
        tenantA.Invoices.Add(new Invoice { CustomerName = "A-Customer-Oct", Amount = 888.88m, IssuedDate = new DateOnly(2026, 10, 1) });

        tenantB.Invoices.Add(new Invoice { CustomerName = "B-Customer-1", Amount = 1000.00m, IssuedDate = new DateOnly(2026, 9, 5) });
        tenantB.Invoices.Add(new Invoice { CustomerName = "B-Customer-2", Amount = 2000.00m, IssuedDate = new DateOnly(2026, 9, 20) });

        db.Tenants.AddRange(tenantA, tenantB);
        db.SaveChanges();
    }
}
