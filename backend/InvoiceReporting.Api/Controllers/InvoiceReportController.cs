using System.Security.Claims;
using InvoiceReporting.Api.Reporting;
using InvoiceReporting.Api.Security;
using InvoiceReporting.Api.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace InvoiceReporting.Api.Controllers;

[ApiController]
[Route("api/invoice-reports")]
[Authorize]
public sealed class InvoiceReportController(
    ITenantResolver tenantResolver,
    IAuthorizationService authorizationService,
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
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
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
        if (resolution.TenantKey is null)
        {
            return Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: $"A valid {TenantResolver.TenantHeaderName} header is required.");
        }

        // Authorize before revealing whether the tenant exists, so a caller
        // cannot use 404 versus 403 to discover other tenants' keys.
        var authorization = await authorizationService.AuthorizeAsync(
            User, new TenantScope(resolution.TenantKey), AuthorizationPolicies.TenantAccess);
        if (!authorization.Succeeded)
        {
            return Forbid();
        }

        if (resolution.Tenant is null)
        {
            return Problem(statusCode: StatusCodes.Status404NotFound, title: "Tenant not found.");
        }

        var report = await reportService.GetMonthlyReportAsync(
            resolution.Tenant,
            reportingMonth,
            User.FindFirstValue(ClaimTypes.NameIdentifier),
            cancellationToken);

        return Ok(report);
    }
}
