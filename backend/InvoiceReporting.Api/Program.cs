using InvoiceReporting.Api.Data;
using InvoiceReporting.Api.Reporting;
using InvoiceReporting.Api.Security;
using InvoiceReporting.Api.Tenancy;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

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
app.UseAuthorization();

app.MapControllers();

app.Run();

public partial class Program;
