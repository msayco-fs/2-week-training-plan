using System.Security.Claims;
using InvoiceReporting.Api.Data.Repositories;
using InvoiceReporting.Api.Domain;
using InvoiceReporting.Api.Reporting;
using InvoiceReporting.Api.Security;
using InvoiceReporting.Api.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace InvoiceReporting.Api.Controllers;

/// <summary>
/// Lets a platform administrator run one tenant's quarterly report now,
/// instead of waiting for the recurring Hangfire job.
/// </summary>
/// <remarks>
/// Two checks, both required: the <see cref="AppRoles.PlatformAdmin"/> role
/// (standard users get 403, even for their own tenant), then the unchanged
/// <see cref="AuthorizationPolicies.TenantAccess"/> policy for the requested tenant.
/// </remarks>
[ApiController]
[Route("api/admin/quarterly-reports")]
[Authorize(Roles = AppRoles.PlatformAdmin)]
public sealed class QuarterlyReportAdminController(
    ITenantResolver tenantResolver,
    IAuthorizationService authorizationService,
    IAuditLogRepository auditLogs,
    QuarterlyReportJob job,
    TimeProvider timeProvider) : ControllerBase
{
    // Wide enough for any real report, narrow enough that DateOnly never overflows.
    private const int MinYear = 2000;
    private const int MaxYear = 2100;

    /// <summary>
    /// Builds the tenant's report for the quarter and queues its emails. Runs
    /// inside the request: if the caller disconnects, the run is cancelled.
    /// A quarter already reported is not built again; only its emails are re-queued.
    /// </summary>
    /// <param name="year">Report year.</param>
    /// <param name="quarter">Quarter number, 1 to 4.</param>
    /// <param name="dealerId">Key of the tenant to report on. Required.</param>
    /// <param name="cancellationToken">Request cancellation (<c>HttpContext.RequestAborted</c>).</param>
    [HttpPost("{year:int}/{quarter:int}")]
    [ProducesResponseType<QuarterlyReportTriggerResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<QuarterlyReportTriggerResponse>> TriggerQuarterly(
        int year,
        int quarter,
        [FromHeader(Name = TenantResolver.TenantHeaderName)] string? dealerId,
        CancellationToken cancellationToken)
    {
        if (year is < MinYear or > MaxYear || quarter is < 1 or > 4)
        {
            return Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Invalid reporting quarter.",
                detail: $"Use a year from {MinYear} to {MaxYear} and a quarter from 1 to 4.");
        }

        var resolution = await tenantResolver.ResolveAsync(dealerId, cancellationToken);
        if (resolution.TenantKey is null)
        {
            return Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: $"A valid {TenantResolver.TenantHeaderName} header is required.");
        }

        // Same order as the monthly report: authorize before revealing whether
        // the tenant exists.
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

        var reportingQuarter = new ReportingQuarter(year, quarter);

        // Records who asked, before the run, so a cancelled or failed run is
        // still traceable to the administrator.
        await auditLogs.AddAsync(new AuditLog
        {
            TenantId = resolution.Tenant.Id,
            Action = AuditActions.TriggerQuarterlyInvoiceReport,
            Detail = reportingQuarter.ToString(),
            ActorSubject = User.FindFirstValue(ClaimTypes.NameIdentifier),
            OccurredAtUtc = timeProvider.GetUtcNow(),
        }, cancellationToken);

        var result = await job.RunAsync(resolution.Tenant.Id, year, quarter, cancellationToken);

        return Ok(new QuarterlyReportTriggerResponse(resolution.Tenant.Key, reportingQuarter.ToString(), result));
    }
}

/// <param name="Result"><c>Generated</c>, or <c>AlreadyGenerated</c> when the quarter was reported before.</param>
public sealed record QuarterlyReportTriggerResponse(string TenantKey, string Quarter, QuarterlyReportRunResult Result);
