namespace InvoiceReporting.Api.Domain;

/// <summary>
/// A dealer organisation whose invoices are reported on. All tenants share one
/// database; rows are partitioned by <see cref="Invoice.TenantId"/>.
/// </summary>
public sealed class Tenant
{
    public int Id { get; set; }

    /// <summary>
    /// Stable, lower-case public identifier (for example <c>dealer-alpha</c>).
    /// Callers send this value in the <c>X-Dealer-Id</c> header, and the
    /// <c>tenant_id</c> claim carries it for authenticated users.
    /// </summary>
    public required string Key { get; set; }

    public required string DisplayName { get; set; }

    public ICollection<Invoice> Invoices { get; } = new List<Invoice>();
}
