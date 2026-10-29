using InvoiceReporting.Api.Reporting;

namespace InvoiceReporting.Api.Tests.Reporting;

public sealed class ReportingQuarterTests
{
    [Theory]
    [InlineData(1, 1)]
    [InlineData(3, 1)]
    [InlineData(4, 2)]
    [InlineData(9, 3)]
    [InlineData(10, 4)]
    [InlineData(12, 4)]
    public void Containing_ReturnsQuarterForMonth(int month, int expectedQuarter)
    {
        var quarter = ReportingQuarter.Containing(new DateOnly(2026, month, 15));

        Assert.Equal(2026, quarter.Year);
        Assert.Equal(expectedQuarter, quarter.Number);
    }

    [Fact]
    public void Range_IsHalfOpen()
    {
        var quarter = new ReportingQuarter(2026, 4);

        Assert.Equal(new DateOnly(2026, 10, 1), quarter.FirstDay);
        Assert.Equal(new DateOnly(2027, 1, 1), quarter.FirstDayOfNextQuarter);
        Assert.Equal("2026-Q4", quarter.ToString());
    }

    [Fact]
    public void Previous_FromQ1_IsQ4OfLastYear()
    {
        Assert.Equal(new ReportingQuarter(2025, 4), new ReportingQuarter(2026, 1).Previous);
        Assert.Equal(new ReportingQuarter(2026, 2), new ReportingQuarter(2026, 3).Previous);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public void Constructor_InvalidNumber_Throws(int number)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReportingQuarter(2026, number));
    }
}
