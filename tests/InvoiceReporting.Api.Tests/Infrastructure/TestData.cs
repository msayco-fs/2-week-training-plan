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

    // Report recipients. Subjects are unique, so tests look users up by subject.

    /// <summary>Standard user of tenant A, opted in.</summary>
    public const string AlphaUserSubject = "alpha-user";

    /// <summary>Standard user of tenant A, opted out.</summary>
    public const string AlphaOptOutSubject = "alpha-optout";

    /// <summary>Standard user of tenant B, opted in.</summary>
    public const string BravoUserSubject = "bravo-user";

    /// <summary>
    /// Standard user of tenant B, opted in, with a stray assignment row for
    /// tenant A. Assignments only count for administrators, so this user must
    /// never receive tenant A's report.
    /// </summary>
    public const string BravoUserWithStrayAssignmentSubject = "bravo-stray";

    /// <summary>Platform administrator assigned to tenant A only, opted in.</summary>
    public const string AdminAssignedToASubject = "admin-a";

    /// <summary>Platform administrator with no assigned tenants, opted in.</summary>
    public const string AdminWithoutAssignmentsSubject = "admin-none";

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

        var bravoStray = new User { Subject = BravoUserWithStrayAssignmentSubject, Email = "bravo.stray@example.com", Tenant = tenantB };
        bravoStray.TenantAssignments.Add(new UserTenantAssignment { Tenant = tenantA });

        var adminA = new User { Subject = AdminAssignedToASubject, Email = "admin.a@example.com", IsPlatformAdmin = true };
        adminA.TenantAssignments.Add(new UserTenantAssignment { Tenant = tenantA });

        var users = new (User User, bool WillReceiveReport)[]
        {
            (new User { Subject = AlphaUserSubject, Email = "alpha.user@example.com", Tenant = tenantA }, true),
            (new User { Subject = AlphaOptOutSubject, Email = "alpha.optout@example.com", Tenant = tenantA }, false),
            (new User { Subject = BravoUserSubject, Email = "bravo.user@example.com", Tenant = tenantB }, true),
            (bravoStray, true),
            (adminA, true),
            (new User { Subject = AdminWithoutAssignmentsSubject, Email = "admin.none@example.com", IsPlatformAdmin = true }, true),
        };

        foreach (var (user, willReceiveReport) in users)
        {
            user.QuarterlyReportRecipient = new QuarterlyReportRecipient { WillReceiveReport = willReceiveReport };
            db.Users.Add(user);
        }

        db.SaveChanges();
    }
}
