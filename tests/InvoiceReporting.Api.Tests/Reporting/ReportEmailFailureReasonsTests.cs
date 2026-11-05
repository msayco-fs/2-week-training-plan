using InvoiceReporting.Api.Reporting;

namespace InvoiceReporting.Api.Tests.Reporting;

public sealed class ReportEmailFailureReasonsTests
{
    private static readonly ReportingQuarter Quarter = new(2026, 3);

    [Fact]
    public void Detail_HasTheQuarterUserAndReason()
    {
        var detail = ReportEmailFailureReasons.Detail(Quarter, 345, ReportEmailFailureReasons.Rejected("http-400:mj-0013"));

        Assert.Equal("2026-Q3:user-345:rejected:http-400:mj-0013", detail);
    }

    /// <summary>The longest real reason with the largest user id must fit the audit column uncut.</summary>
    [Fact]
    public void Detail_ForTheLongestRealReason_FitsWithoutCutting()
    {
        var reason = ReportEmailFailureReasons.RetriesExhausted(ReportEmailFailureReasons.OutcomeUnknown);

        var detail = ReportEmailFailureReasons.Detail(Quarter, int.MaxValue, reason);

        Assert.EndsWith(reason, detail);
        Assert.True(detail.Length <= ReportEmailFailureReasons.MaxDetailLength);
    }

    [Fact]
    public void Detail_WithAnUnexpectedlyLongReason_IsCutToTheColumnSize()
    {
        var detail = ReportEmailFailureReasons.Detail(Quarter, 1, new string('x', 100));

        Assert.Equal(ReportEmailFailureReasons.MaxDetailLength, detail.Length);
    }
}
