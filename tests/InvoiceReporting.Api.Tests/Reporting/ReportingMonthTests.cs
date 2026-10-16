using InvoiceReporting.Api.Reporting;

namespace InvoiceReporting.Api.Tests.Reporting;

public sealed class ReportingMonthTests
{
    [Fact]
    public void TryParse_ValidMonth_ReturnsHalfOpenRange()
    {
        Assert.True(ReportingMonth.TryParse("2026-12", out var month));
        Assert.Equal(new DateOnly(2026, 12, 1), month.FirstDay);
        Assert.Equal(new DateOnly(2027, 1, 1), month.FirstDayOfNextMonth);
        Assert.Equal("2026-12", month.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("2026-13")]
    [InlineData("2026-9")]
    [InlineData("09-2026")]
    [InlineData("2026-09-01")]
    [InlineData("2026-09'; DROP TABLE Invoices;--")]
    public void TryParse_InvalidMonth_ReturnsFalse(string? value)
    {
        Assert.False(ReportingMonth.TryParse(value, out _));
    }
}
