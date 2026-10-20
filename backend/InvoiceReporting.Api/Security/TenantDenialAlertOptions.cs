namespace InvoiceReporting.Api.Security;

/// <summary>
/// When one subject is denied tenant access <see cref="Threshold"/> times within
/// <see cref="Window"/>, a probe alert is logged. Bound from
/// <c>Security:TenantDenialAlert</c>.
/// </summary>
public sealed class TenantDenialAlertOptions
{
    public const string SectionName = "Security:TenantDenialAlert";

    public int Threshold { get; set; } = 5;

    public TimeSpan Window { get; set; } = TimeSpan.FromMinutes(5);
}
