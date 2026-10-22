using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Threading.RateLimiting;
using InvoiceReporting.Api.Data;
using InvoiceReporting.Api.Reporting;
using InvoiceReporting.Api.Security;
using InvoiceReporting.Api.Tenancy;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();

builder.Services.AddDbContext<ReportingDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("Reporting")));

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<ITenantResolver, TenantResolver>();
builder.Services.AddScoped<IInvoiceReportService, InvoiceReportService>();

// JWT bearer settings (authority, issuer, audiences) are bound from the
// "Authentication:Schemes:Bearer" configuration section. Local development
// uses `dotnet user-jwts`, which keeps signing keys in user secrets.
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer();

// Outside Development, tokens must be validated against a real identity
// provider. Fail at startup instead of running with no usable token settings.
if (!builder.Environment.IsDevelopment())
{
    builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
        .Validate(
            options => (!string.IsNullOrWhiteSpace(options.Authority) || !string.IsNullOrWhiteSpace(options.MetadataAddress))
                && (!string.IsNullOrWhiteSpace(options.Audience)
                    || !string.IsNullOrWhiteSpace(options.TokenValidationParameters.ValidAudience)
                    || options.TokenValidationParameters.ValidAudiences?.Any() == true),
            "Authentication:Schemes:Bearer must set Authority (or MetadataAddress) and at least one audience outside Development.")
        .ValidateOnStart();
}

// Only the listed proxies (plus loopback, the framework default) may set the
// client IP through X-Forwarded-For, so callers cannot spoof their address to
// get around the per-IP rate limit. Addresses are parsed here so a bad value
// fails at startup.
var knownProxies = (builder.Configuration.GetSection("ForwardedHeaders:KnownProxies").Get<string[]>() ?? [])
    .Select(IPAddress.Parse)
    .ToArray();
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    foreach (var proxy in knownProxies)
    {
        options.KnownProxies.Add(proxy);
    }
});

builder.Services.AddMemoryCache();
builder.Services.Configure<TenantDenialAlertOptions>(
    builder.Configuration.GetSection(TenantDenialAlertOptions.SectionName));
builder.Services.AddSingleton<TenantDenialMonitor>();
builder.Services.AddSingleton<IAuthorizationHandler, TenantAccessHandler>();
builder.Services.AddAuthorizationBuilder()
    .AddPolicy(AuthorizationPolicies.TenantAccess, policy => policy
        .RequireAuthenticatedUser()
        .AddRequirements(new TenantAccessRequirement()));

builder.Services.Configure<PerCallerRateLimitOptions>(
    builder.Configuration.GetSection(PerCallerRateLimitOptions.SectionName));
builder.Services.AddRateLimiter(options =>
{
    // The default rejection status is 503, which wrongly blames the server.
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = (context, _) =>
    {
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            context.HttpContext.Response.Headers.RetryAfter =
                ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
        }

        return ValueTask.CompletedTask;
    };
    options.AddPolicy(RateLimitPolicies.PerCaller, httpContext =>
    {
        var limits = httpContext.RequestServices.GetRequiredService<IOptions<PerCallerRateLimitOptions>>().Value;
        var subject = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);
        var partitionKey = subject is null ? $"ip:{httpContext.Connection.RemoteIpAddress}" : $"sub:{subject}";
        return RateLimitPartition.GetFixedWindowLimiter(partitionKey, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = limits.PermitLimit,
            Window = limits.Window,
            QueueLimit = 0,
        });
    });
});

var app = builder.Build();

// First, so every later middleware (HTTPS redirection, rate limiting) sees the
// real client address and scheme.
app.UseForwardedHeaders();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();

    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<ReportingDbContext>();
    await db.Database.MigrateAsync();
    await DevelopmentDataSeeder.SeedAsync(db);
}

app.UseHttpsRedirection();

app.UseAuthentication();
// After authentication so callers are partitioned by subject, and before
// authorization so anonymous floods are throttled too.
app.UseRateLimiter();
app.UseAuthorization();

// Applied to every controller, so new endpoints are limited by default.
app.MapControllers().RequireRateLimiting(RateLimitPolicies.PerCaller);

app.Run();

public partial class Program;
