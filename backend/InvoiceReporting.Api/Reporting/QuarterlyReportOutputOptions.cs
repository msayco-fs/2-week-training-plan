namespace InvoiceReporting.Api.Reporting;

/// <summary>
/// Where quarterly report CSV files are written. Bound from
/// <c>Reporting:QuarterlyOutput</c>. A relative <see cref="Directory"/> is
/// resolved against the application content root.
/// </summary>
public sealed class QuarterlyReportOutputOptions
{
    public const string SectionName = "Reporting:QuarterlyOutput";

    public string Directory { get; set; } = "reports/quarterly";
}
