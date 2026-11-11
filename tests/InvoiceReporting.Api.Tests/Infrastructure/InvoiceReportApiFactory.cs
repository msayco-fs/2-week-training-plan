using InvoiceReporting.Api.Data;
using InvoiceReporting.Api.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace InvoiceReporting.Api.Tests.Infrastructure;

/// <summary>
/// Hosts the real application pipeline with two substitutions:
/// <list type="bullet">
/// <item>The SQL Server connection points at a private SQL Server database (Docker, see
/// <see cref="SqlServerTestDatabase"/>), created by the real migrations and seeded per factory instance.</item>
/// <item>Hangfire's SQL Server storage is replaced by in-memory storage.</item>
/// <item>JWT bearer is replaced by <see cref="TestAuthHandler"/> as the default scheme.
/// Placeholder JWT bearer settings are supplied only to pass the startup check.</item>
/// <item>The <see cref="TenantAccessHandler"/> logger is replaced by <see cref="TenantAccessLogger"/>,
/// so tests can check the denial events a real request writes.</item>
/// </list>
/// </summary>
public sealed class InvoiceReportApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private string _connectionString = null!;

    /// <summary>Creates this factory's own database before any test in the class runs.</summary>
    public async Task InitializeAsync() => _connectionString = await SqlServerTestDatabase.CreateAsync();

    // Nothing extra: the host is disposed by WebApplicationFactory, the database with the container.
    Task IAsyncLifetime.DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// Receives every log entry of the app's <see cref="TenantAccessHandler"/>.
    /// Shared by all tests that use this factory, so filter entries by subject.
    /// </summary>
    public CapturingLogger<TenantAccessHandler> TenantAccessLogger { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        // Satisfies the startup check on JWT bearer settings outside Development.
        // JWT bearer never runs in tests (TestAuthHandler is the default scheme),
        // so this authority is never contacted.
        builder.UseSetting("Authentication:Schemes:Bearer:Authority", "https://identity.test.invalid/");
        builder.UseSetting("Authentication:Schemes:Bearer:ValidAudiences:0", "invoice-reporting-tests");

        // Keeps the failed-email log folder out of the backend project folder.
        builder.UseSetting(
            "Reporting:Email:FailedLogDirectory",
            Path.Combine(Path.GetTempPath(), "invoice-reporting-tests", "logs"));

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<DbContextOptions<ReportingDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<ReportingDbContext>>();
            services.AddDbContext<ReportingDbContext>(options => options.UseSqlServer(_connectionString));
            services.UseInMemoryHangfire();

            // The handler is a singleton, so it takes this logger once at startup.
            services.AddSingleton<ILogger<TenantAccessHandler>>(TenantAccessLogger);

            services
                .AddAuthentication(TestAuthHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });
            services.PostConfigure<AuthenticationOptions>(options =>
            {
                options.DefaultScheme = TestAuthHandler.SchemeName;
                options.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                options.DefaultChallengeScheme = TestAuthHandler.SchemeName;
                options.DefaultForbidScheme = TestAuthHandler.SchemeName;
            });
        });
    }

    /// <summary>Runs <paramref name="action"/> against the test database in a fresh scope.</summary>
    public async Task<T> WithDbAsync<T>(Func<ReportingDbContext, Task<T>> action)
    {
        using var scope = Services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<ReportingDbContext>());
    }
}
