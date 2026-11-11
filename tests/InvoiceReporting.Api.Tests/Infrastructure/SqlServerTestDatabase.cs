using InvoiceReporting.Api.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Testcontainers.MsSql;

namespace InvoiceReporting.Api.Tests.Infrastructure;

/// <summary>
/// One SQL Server container (Docker) for the whole test run, and one new
/// database in it per caller. Same image as docker-compose.yml. The container
/// starts on first use; Testcontainers removes it when the test process ends.
/// </summary>
public static class SqlServerTestDatabase
{
    private static readonly Lazy<Task<MsSqlContainer>> Container = new(async () =>
    {
        var container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();
        await container.StartAsync();
        return container;
    });

    /// <summary>
    /// Creates a database with a unique name, applies the real migrations and
    /// seeds the fixed test data. Returns its connection string.
    /// </summary>
    public static async Task<string> CreateAsync()
    {
        var container = await Container.Value;
        var connectionString = new SqlConnectionStringBuilder(container.GetConnectionString())
        {
            InitialCatalog = $"reporting_tests_{Guid.NewGuid():N}",
        }.ConnectionString;

        await using var db = new ReportingDbContext(
            new DbContextOptionsBuilder<ReportingDbContext>().UseSqlServer(connectionString).Options);
        await db.Database.MigrateAsync();
        TestData.Seed(db);
        return connectionString;
    }
}
