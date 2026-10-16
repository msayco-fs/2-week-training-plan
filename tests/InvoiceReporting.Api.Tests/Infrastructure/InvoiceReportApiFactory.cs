using InvoiceReporting.Api.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace InvoiceReporting.Api.Tests.Infrastructure;

/// <summary>
/// Hosts the real application pipeline with two substitutions:
/// <list type="bullet">
/// <item>SQL Server is replaced by a private SQLite in-memory database, created and seeded per factory instance.</item>
/// <item>JWT bearer is replaced by <see cref="TestAuthHandler"/> as the default scheme.</item>
/// </list>
/// SQLite is relational (foreign keys, unique indexes, real SQL translation) but does
/// not validate SQL Server-specific behaviour such as collation or decimal precision.
/// </summary>
public sealed class InvoiceReportApiFactory : WebApplicationFactory<Program>
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<DbContextOptions<ReportingDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<ReportingDbContext>>();
            services.AddDbContext<ReportingDbContext>(options => options.UseSqlite(_connection));

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

    protected override void ConfigureClient(HttpClient client)
    {
        base.ConfigureClient(client);
        EnsureDatabase();
    }

    private bool _databaseCreated;
    private readonly Lock _databaseLock = new();

    private void EnsureDatabase()
    {
        lock (_databaseLock)
        {
            if (_databaseCreated)
            {
                return;
            }

            _connection.Open();
            using var scope = Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ReportingDbContext>();
            db.Database.EnsureCreated();
            TestData.Seed(db);
            _databaseCreated = true;
        }
    }

    /// <summary>Runs <paramref name="action"/> against the test database in a fresh scope.</summary>
    public async Task<T> WithDbAsync<T>(Func<ReportingDbContext, Task<T>> action)
    {
        EnsureDatabase();
        using var scope = Services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<ReportingDbContext>());
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            _connection.Dispose();
        }
    }
}
