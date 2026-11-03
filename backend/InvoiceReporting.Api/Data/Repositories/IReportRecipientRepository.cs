namespace InvoiceReporting.Api.Data.Repositories;

/// <summary>
/// Finds who receives a tenant's quarterly report. A user qualifies when they
/// opted in (<c>WillReceiveReport</c>) and either:
/// <list type="bullet">
/// <item>they are a standard user whose own tenant is this tenant, or</item>
/// <item>they are a platform administrator assigned to this tenant.</item>
/// </list>
/// </summary>
public interface IReportRecipientRepository
{
    /// <summary>Ids of every user who should receive this tenant's report, in id order.</summary>
    Task<IReadOnlyList<int>> GetRecipientUserIdsAsync(int tenantId, CancellationToken cancellationToken);

    /// <summary>
    /// The user's email address, or null when the user no longer qualifies
    /// for this tenant's report (for example they opted out after the job was queued).
    /// </summary>
    Task<string?> GetRecipientEmailAsync(int tenantId, int userId, CancellationToken cancellationToken);
}
