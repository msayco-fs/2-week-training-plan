namespace InvoiceReporting.Api.Reporting;

public sealed record InvoiceSummary(int Id, string CustomerName, decimal Amount, DateOnly IssuedDate);

public sealed record MonthlyInvoiceReport(string Month, IReadOnlyList<InvoiceSummary> Invoices, decimal Total);
