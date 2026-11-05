using System.Globalization;

namespace InvoiceReporting.Api.Reporting;

/// <summary>A calendar quarter (Q1 = Jan–Mar), as a half-open date range.</summary>
public readonly record struct ReportingQuarter
{
    public ReportingQuarter(int year, int number)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(number, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(number, 4);
        Year = year;
        Number = number;
    }

    public int Year { get; }

    /// <summary>Quarter number, 1 to 4.</summary>
    public int Number { get; }

    public DateOnly FirstDay => new(Year, (Number - 1) * 3 + 1, 1);

    public DateOnly FirstDayOfNextQuarter => FirstDay.AddMonths(3);

    public ReportingQuarter Previous => Number == 1 ? new(Year - 1, 4) : new(Year, Number - 1);

    public static ReportingQuarter Containing(DateOnly date) => new(date.Year, (date.Month - 1) / 3 + 1);

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Year}-Q{Number}");
}
