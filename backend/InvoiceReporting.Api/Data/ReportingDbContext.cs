using InvoiceReporting.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace InvoiceReporting.Api.Data;

public sealed class ReportingDbContext(DbContextOptions<ReportingDbContext> options) : DbContext(options)
{
    public const int TenantKeyMaxLength = 32;

    public DbSet<Tenant> Tenants => Set<Tenant>();

    public DbSet<Invoice> Invoices => Set<Invoice>();

    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    public DbSet<User> Users => Set<User>();

    public DbSet<UserTenantAssignment> UserTenantAssignments => Set<UserTenantAssignment>();

    public DbSet<QuarterlyReportRecipient> QuarterlyReportRecipients => Set<QuarterlyReportRecipient>();

    public DbSet<QuarterlyReportEmailDelivery> QuarterlyReportEmailDeliveries => Set<QuarterlyReportEmailDelivery>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Tenant>(tenant =>
        {
            tenant.Property(t => t.Key).HasMaxLength(TenantKeyMaxLength);
            tenant.HasIndex(t => t.Key).IsUnique();
            tenant.Property(t => t.DisplayName).HasMaxLength(200);
        });

        modelBuilder.Entity<Invoice>(invoice =>
        {
            invoice.Property(i => i.CustomerName).HasMaxLength(200);
            invoice.Property(i => i.Amount).HasPrecision(18, 2);
            invoice.HasOne(i => i.Tenant)
                .WithMany(t => t.Invoices)
                .HasForeignKey(i => i.TenantId)
                .OnDelete(DeleteBehavior.Restrict);

            // Supports the tenant + date-range filter used by the monthly and
            // quarterly reports. CustomerName and Amount are included so SQL
            // Server answers the report query from this index alone (an index
            // seek, already in date order). Without them it scans and sorts the
            // whole table for every tenant.
            invoice.HasIndex(i => new { i.TenantId, i.IssuedDate })
                .IncludeProperties(i => new { i.CustomerName, i.Amount });
        });

        modelBuilder.Entity<AuditLog>(audit =>
        {
            audit.Property(a => a.Action).HasMaxLength(64);
            audit.Property(a => a.Detail).HasMaxLength(64);
            audit.Property(a => a.ActorSubject).HasMaxLength(128);

            // At most one "generated" entry per tenant and quarter, so two
            // report runs at once cannot both record the quarter. Other
            // actions may repeat. Plain quotes: the filter is stored in the
            // migration as written, so changing it means a new migration.
            audit.HasIndex(a => new { a.TenantId, a.Action, a.Detail })
                .IsUnique()
                .HasFilter($"[Action] = '{AuditActions.GenerateQuarterlyInvoiceReport}'")
                .HasDatabaseName("IX_AuditLogs_QuarterlyReportGenerated");

            // Kept on purpose: the index above is filtered, so it cannot serve
            // the tenant foreign key, but EF would otherwise drop this one.
            audit.HasIndex(a => a.TenantId);
            audit.HasOne(a => a.Tenant)
                .WithMany()
                .HasForeignKey(a => a.TenantId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<User>(user =>
        {
            user.Property(u => u.Subject).HasMaxLength(128);
            user.HasIndex(u => u.Subject).IsUnique();
            user.Property(u => u.Email).HasMaxLength(254);
            user.HasOne(u => u.Tenant)
                .WithMany()
                .HasForeignKey(u => u.TenantId)
                .OnDelete(DeleteBehavior.Restrict);

            // A standard user always belongs to exactly one tenant.
            user.ToTable(t => t.HasCheckConstraint(
                "CK_Users_TenantRequiredForStandardUsers",
                "IsPlatformAdmin = 1 OR TenantId IS NOT NULL"));
        });

        modelBuilder.Entity<UserTenantAssignment>(assignment =>
        {
            assignment.HasKey(a => new { a.UserId, a.TenantId });
            assignment.HasOne(a => a.User)
                .WithMany(u => u.TenantAssignments)
                .HasForeignKey(a => a.UserId)
                .OnDelete(DeleteBehavior.Cascade);
            assignment.HasOne(a => a.Tenant)
                .WithMany()
                .HasForeignKey(a => a.TenantId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<QuarterlyReportRecipient>(recipient =>
        {
            recipient.HasKey(r => r.UserId);
            recipient.HasOne(r => r.User)
                .WithOne(u => u.QuarterlyReportRecipient)
                .HasForeignKey<QuarterlyReportRecipient>(r => r.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<QuarterlyReportEmailDelivery>(delivery =>
        {
            delivery.Property(d => d.Quarter).HasMaxLength(16);
            delivery.Property(d => d.Status).HasConversion<string>().HasMaxLength(16);

            // The idempotency key: at most one email per tenant, user and quarter.
            delivery.HasIndex(d => new { d.TenantId, d.UserId, d.Quarter }).IsUnique();

            delivery.HasOne(d => d.Tenant)
                .WithMany()
                .HasForeignKey(d => d.TenantId)
                .OnDelete(DeleteBehavior.Restrict);
            delivery.HasOne(d => d.User)
                .WithMany()
                .HasForeignKey(d => d.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
