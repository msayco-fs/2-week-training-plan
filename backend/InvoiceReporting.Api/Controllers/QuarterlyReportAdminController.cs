using System.Security.Claims;
using Hangfire;
using InvoiceReporting.Api.Data.Repositories;
using InvoiceReporting.Api.Domain;
using InvoiceReporting.Api.Reporting;
using InvoiceReporting.Api.Security;
using InvoiceReporting.Api.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace InvoiceReporting.Api.Controllers;

/// <summary>
/// Lets a platform administrator queue one tenant's quarterly report now,
/// instead of waiting for the recurring Hangfire job, and check on it.
/// </summary>
/// <remarks>
/// Two checks on every action, both required: the <see cref="AppRoles.PlatformAdmin"/>
/// role (standard users get 403, even for their own tenant), then the unchanged
/// <see cref="AuthorizationPolicies.TenantAccess"/> policy for the requested tenant.
/// </remarks>
[ApiController]
[Route("api/admin/quarterly-reports")]
[Authorize(Roles = AppRoles.PlatformAdmin)]
public sealed class QuarterlyReportAdminController(
    ITenantResolver tenantResolver,
    IAuthorizationService authorizationService,
    IAuditLogRepository auditLogs,
    IBackgroundJobClient jobs,
    IQuarterlyReportActivity reportActivity,
    TimeProvider timeProvider) : ControllerBase
{
    // Wide enough for any real report, narrow enough that DateOnly never overflows.
    private const int MinYear = 2000;
    private const int MaxYear = 2100;

    /// <summary>
    /// Queues the tenant's report for the quarter as a Hangfire job and returns
    /// at once. The job builds the CSV and queues its emails, with the same
    /// retries as a scheduled run. A quarter already reported is not built
    /// again; only its emails are re-queued. Poll the <c>Location</c> URL for progress.
    /// </summary>
    /// <param name="year">Report year.</param>
    /// <param name="quarter">Quarter number, 1 to 4.</param>
    /// <param name="dealerId">Key of the tenant to report on. Required.</param>
    /// <param name="cancellationToken">Request cancellation; it does not stop a job already queued.</param>
    [HttpPost("{year:int}/{quarter:int}")]
    [ProducesResponseType<QuarterlyReportTriggerResponse>(StatusCodes.Status202Accepted)]
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

        var (tenant, denied) = await AuthorizeTenantAsync(dealerId, cancellationToken);
        if (denied is not null)
        {
            return denied;
        }

        var reportingQuarter = new ReportingQuarter(year, quarter);

        // Records who asked, before queuing, so a run that later fails is
        // still traceable to the administrator.
        await auditLogs.AddAsync(new AuditLog
        {
            TenantId = tenant!.Id,
            Action = AuditActions.TriggerQuarterlyInvoiceReport,
            Detail = reportingQuarter.ToString(),
            ActorSubject = User.FindFirstValue(ClaimTypes.NameIdentifier),
            OccurredAtUtc = timeProvider.GetUtcNow(),
        }, cancellationToken);

        // Not cancelled from here on: the trigger is audited, so the run must be
        // queued. CancellationToken.None is a placeholder: Hangfire passes its
        // shutdown token when the job runs.
        var jobId = jobs.Enqueue<QuarterlyReportJob>(job =>
            job.RunAsync(tenant.Id, year, quarter, CancellationToken.None));

        return AcceptedAtAction(
            nameof(GetRun),
            new { jobId },
            new QuarterlyReportTriggerResponse(tenant.Key, reportingQuarter.ToString(), jobId));
    }

    /// <summary>
    /// The state of a run queued by <see cref="TriggerQuarterly"/>. Returns 404
    /// for an unknown job, a job that is not a quarterly report run, or a run
    /// for another tenant than the one in the header.
    /// </summary>
    /// <param name="jobId">The <c>jobId</c> returned when the run was queued.</param>
    /// <param name="dealerId">Key of the tenant the run belongs to. Required.</param>
    /// <param name="cancellationToken">Request cancellation.</param>
    [HttpGet("runs/{jobId}")]
    [ProducesResponseType<QuarterlyReportRunStatusResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<QuarterlyReportRunStatusResponse>> GetRun(
        string jobId,
        [FromHeader(Name = TenantResolver.TenantHeaderName)] string? dealerId,
        CancellationToken cancellationToken)
    {
        var (tenant, denied) = await AuthorizeTenantAsync(dealerId, cancellationToken);
        if (denied is not null)
        {
            return denied;
        }

        // Hangfire's monitoring API is synchronous; this is one read.
        var run = reportActivity.FindRun(jobId);
        if (run is null || run.TenantId != tenant!.Id)
        {
            return Problem(statusCode: StatusCodes.Status404NotFound, title: "Quarterly report run not found.");
        }

        return Ok(new QuarterlyReportRunStatusResponse(run.JobId, tenant.Key, run.Quarter.ToString(), run.State, run.Result));
    }

    /// <summary>
    /// Resolves the tenant header and applies <see cref="AuthorizationPolicies.TenantAccess"/>.
    /// Same order as the monthly report: authorize before revealing whether the
    /// tenant exists. Returns the tenant, or the response to send instead.
    /// </summary>
    private async Task<(ResolvedTenant? Tenant, ActionResult? Denied)> AuthorizeTenantAsync(
        string? dealerId, CancellationToken cancellationToken)
    {
        var resolution = await tenantResolver.ResolveAsync(dealerId, cancellationToken);
        if (resolution.TenantKey is null)
        {
            return (null, Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: $"A valid {TenantResolver.TenantHeaderName} header is required."));
        }

        var authorization = await authorizationService.AuthorizeAsync(
            User, new TenantScope(resolution.TenantKey), AuthorizationPolicies.TenantAccess);
        if (!authorization.Succeeded)
        {
            return (null, Forbid());
        }

        if (resolution.Tenant is null)
        {
            return (null, Problem(statusCode: StatusCodes.Status404NotFound, title: "Tenant not found."));
        }

        return (resolution.Tenant, null);
    }
}

/// <param name="JobId">Hangfire job id; use it with <c>GET runs/{jobId}</c>.</param>
public sealed record QuarterlyReportTriggerResponse(string TenantKey, string Quarter, string JobId);

/// <param name="State">Hangfire's state name: Enqueued, Scheduled (waiting for a retry), Processing, Succeeded, Failed or Deleted.</param>
/// <param name="Result"><c>Generated</c> or <c>AlreadyGenerated</c> once the run has succeeded; otherwise null.</param>
public sealed record QuarterlyReportRunStatusResponse(
    string JobId, string TenantKey, string Quarter, string? State, QuarterlyReportRunResult? Result);
