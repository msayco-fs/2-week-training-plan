using System.Security.Claims;
using InvoiceReporting.Api.Reporting;
using InvoiceReporting.Api.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace InvoiceReporting.Api.Controllers;

[ApiController]
[Route("api/invoice-reports")]
[Authorize]
public sealed class InvoiceReportController(
    ITenantResolver tenantResolver,
    IInvoiceReportService reportService) : ControllerBase
{
    /// <summary>Returns the invoices issued in <paramref name="month"/> for the requested tenant.</summary>
    /// <param name="month">Reporting month in <c>yyyy-MM</c> form.</param>
    /// <param name="dealerId">Key of the tenant whose report is requested.</param>
    /// <param name="cancellationToken">Request cancellation.</param>
    [HttpGet("monthly")]
    [ProducesResponseType<MonthlyInvoiceReport>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<MonthlyInvoiceReport>> GetMonthly(
        [FromQuery] string? month,
        [FromHeader(Name = TenantResolver.TenantHeaderName)] string? dealerId,
        CancellationToken cancellationToken)
    {
        if (!ReportingMonth.TryParse(month, out var reportingMonth))
        {
            return Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Invalid reporting month.",
                detail: $"Use the {ReportingMonth.Format} format.");
        }

        var resolution = await tenantResolver.ResolveAsync(dealerId, cancellationToken);
        switch (resolution.Status)
        {
            case TenantResolutionStatus.Missing:
            case TenantResolutionStatus.Malformed:
                return Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: $"A valid {TenantResolver.TenantHeaderName} header is required.");
            case TenantResolutionStatus.NotFound:
                return Problem(statusCode: StatusCodes.Status404NotFound, title: "Tenant not found.");
        }

        var tenant = resolution.Tenant!;

        // ---------------------------------------------------------------------
        // TRAINING VULNERABILITY (intentional, corrected in Phase 2)
        // Broken object-level authorization between tenants (OWASP API1:2023).
        //
        // The caller is authenticated, but nothing checks that the tenant they
        // asked for in the X-Dealer-Id header matches the tenant_id claim in
        // their identity. Any authenticated user of any dealer can read any
        // other dealer's invoices by changing the header value.
        //
        // Phase 2 fix: authorize the resolved tenant against the caller's
        // tenant_id claim (with a PlatformAdmin role bypass) BEFORE this call.
        // See docs/training/phase-1-vulnerability.md.
        // ---------------------------------------------------------------------
        var report = await reportService.GetMonthlyReportAsync(
            tenant,
            reportingMonth,
            User.FindFirstValue(ClaimTypes.NameIdentifier),
            cancellationToken);

        return Ok(report);
    }
}
