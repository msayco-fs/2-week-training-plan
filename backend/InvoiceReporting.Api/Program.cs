using System.Globalization;
using System.Security.Claims;
using System.Threading.RateLimiting;
using InvoiceReporting.Api.Data;
using InvoiceReporting.Api.Reporting;
using InvoiceReporting.Api.Security;
using InvoiceReporting.Api.Tenancy;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
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
