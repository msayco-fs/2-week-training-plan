namespace InvoiceReporting.Api.Domain;

/// <summary>
/// Whether a user receives the quarterly invoice report by email. Which
/// tenants' reports they receive comes from <see cref="User"/>.
/// </summary>
public sealed class QuarterlyReportRecipient
{
    public int UserId { get; set; }

    public User User { get; set; } = null!;

    public bool WillReceiveReport { get; set; }
}
