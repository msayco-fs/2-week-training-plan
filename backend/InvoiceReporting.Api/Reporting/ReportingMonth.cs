using System.Globalization;

namespace InvoiceReporting.Api.Reporting;

/// <summary>A calendar month in <c>yyyy-MM</c> form, as a half-open date range.</summary>
public readonly record struct ReportingMonth(DateOnly FirstDay)
{
    public const string Format = "yyyy-MM";

    public DateOnly FirstDayOfNextMonth => FirstDay.AddMonths(1);

    public static bool TryParse(string? value, out ReportingMonth month)
    {
        if (DateOnly.TryParseExact(value, Format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var firstDay))
        {
            month = new ReportingMonth(firstDay);
            return true;
        }

        month = default;
        return false;
    }

    public override string ToString() => FirstDay.ToString(Format, CultureInfo.InvariantCulture);
}
