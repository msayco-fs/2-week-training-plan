namespace InvoiceReporting.Api.Reporting;

/// <summary>
/// Where quarterly report CSV files are written. Bound from
/// <c>Reporting:QuarterlyOutput</c>. A relative <see cref="Directory"/> is
/// resolved against the application content root.
/// </summary>
public sealed class QuarterlyReportOutputOptions
{
    public const string SectionName = "Reporting:QuarterlyOutput";

    /// <summary>The shortest allowed <see cref="StaleTempFileAge"/>.</summary>
    public static readonly TimeSpan MinStaleTempFileAge = TimeSpan.FromHours(1);

    public string Directory { get; set; } = "reports/quarterly";

    /// <summary>
    /// How long a temp file must go unwritten before the cleanup deletes it.
    /// A report write takes seconds (see docs/day-07-capacity-calc.md), so the
    /// default of 24 hours leaves a wide margin.
    /// </summary>
    public TimeSpan StaleTempFileAge { get; set; } = TimeSpan.FromHours(24);
}
