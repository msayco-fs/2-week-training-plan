namespace InvoiceReporting.Api.Security;

/// <summary>Per-caller request limit, bound from <c>RateLimiting:PerCaller</c>.</summary>
public sealed class PerCallerRateLimitOptions
{
    public const string SectionName = "RateLimiting:PerCaller";

    public int PermitLimit { get; set; } = 60;

    public TimeSpan Window { get; set; } = TimeSpan.FromMinutes(1);
}
