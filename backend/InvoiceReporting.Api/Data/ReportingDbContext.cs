using InvoiceReporting.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace InvoiceReporting.Api.Data;

public sealed class ReportingDbContext(DbContextOptions<ReportingDbContext> options) : DbContext(options)
{
    public const int TenantKeyMaxLength = 32;

    public DbSet<Tenant> Tenants => Set<Tenant>();

    public DbSet<Invoice> Invoices => Set<Invoice>();

    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

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

            // Supports the tenant + date-range filter used by the monthly report.
            invoice.HasIndex(i => new { i.TenantId, i.IssuedDate });
        });

        modelBuilder.Entity<AuditLog>(audit =>
        {
            audit.Property(a => a.Action).HasMaxLength(64);
            audit.Property(a => a.Detail).HasMaxLength(64);
            audit.Property(a => a.ActorSubject).HasMaxLength(128);
            audit.HasOne(a => a.Tenant)
                .WithMany()
                .HasForeignKey(a => a.TenantId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }
}
