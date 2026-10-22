namespace InvoiceReporting.Api.Tenancy;

/// <summary>The tenant a request asked to operate on, after validation and lookup.</summary>
public sealed record ResolvedTenant(int Id, string Key);
