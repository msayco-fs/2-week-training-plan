namespace InvoiceReporting.Api.Domain;

public sealed class Invoice
{
    public int Id { get; set; }

    public int TenantId { get; set; }

    public Tenant Tenant { get; set; } = null!;

    public required string CustomerName { get; set; }

    public decimal Amount { get; set; }

    public DateOnly IssuedDate { get; set; }
}
