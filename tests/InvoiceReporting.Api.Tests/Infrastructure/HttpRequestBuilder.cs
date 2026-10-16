using InvoiceReporting.Api.Tenancy;

namespace InvoiceReporting.Api.Tests.Infrastructure;

public static class HttpRequestBuilder
{
    public const string MonthlyReportPath = "/api/invoice-reports/monthly";

    /// <summary>
    /// Builds a monthly report request. A null <paramref name="subject"/> sends no
    /// identity; a null <paramref name="dealerId"/> omits the tenant header.
    /// </summary>
    public static HttpRequestMessage MonthlyReport(
        string? month,
        string? dealerId,
        string? subject,
        string? tenantClaim = null,
        params string[] roles)
    {
        var uri = month is null ? MonthlyReportPath : $"{MonthlyReportPath}?month={Uri.EscapeDataString(month)}";
        var request = new HttpRequestMessage(HttpMethod.Get, uri);

        if (dealerId is not null)
        {
            request.Headers.Add(TenantResolver.TenantHeaderName, dealerId);
        }

        if (subject is not null)
        {
            request.Headers.Add(TestAuthHandler.SubjectHeader, subject);
            if (tenantClaim is not null)
            {
                request.Headers.Add(TestAuthHandler.TenantHeader, tenantClaim);
            }

            if (roles.Length > 0)
            {
                request.Headers.Add(TestAuthHandler.RolesHeader, string.Join(',', roles));
            }
        }

        return request;
    }

    /// <summary>Unique subject per test so audit-log assertions never depend on test order.</summary>
    public static string NewSubject() => $"test-user-{Guid.NewGuid():N}";
}
