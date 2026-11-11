using InvoiceReporting.Api.Data;
using InvoiceReporting.Api.Data.Repositories;
using InvoiceReporting.Api.Domain;
using InvoiceReporting.Api.Tests.Infrastructure;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace InvoiceReporting.Api.Tests.Integration;

/// <summary>
/// <see cref="AuditLogRepository.TryAddUniqueAsync"/> must report "already
/// saved" only for the quarterly report unique index, never for other errors.
/// Uses its own database, so other tests' audit entries never interfere.
/// </summary>
public sealed class AuditLogRepositoryTests : IAsyncLifetime
{
    private string _connectionString = null!;
    private int _tenantId;

    public async Task InitializeAsync()
    {
        _connectionString = await SqlServerTestDatabase.CreateAsync();
        await using var db = CreateDb();
        _tenantId = await db.Tenants.Where(t => t.Key == TestData.TenantAKey).Select(t => t.Id).SingleAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// The save fails for another reason (here a timeout wrapped by EF) just
    /// after another run saved the same quarter. The real error must surface,
    /// not be reported as "already saved".
    /// </summary>
    [Fact]
    public async Task TryAddUniqueAsync_WhenTheSaveFailsForAnotherReason_Rethrows_EvenIfAMatchingEntryExists()
    {
        await using (var db = CreateDb())
        {
            Assert.True(await Repository(db).TryAddUniqueAsync(Generated("2018-Q2"), CancellationToken.None));
        }

        await using var failing = CreateDb(new FailingSaveInterceptor());

        var error = await Assert.ThrowsAsync<DbUpdateException>(() =>
            Repository(failing).TryAddUniqueAsync(Generated("2018-Q2"), CancellationToken.None));
        Assert.IsType<TimeoutException>(error.InnerException);
    }

    [Fact]
    public async Task TryAddUniqueAsync_WhenTheUniqueIndexIsHit_ReturnsFalse_AndLogsTheErrorAtDebug()
    {
        var logger = new CapturingLogger<AuditLogRepository>();
        await using var db = CreateDb();
        var repository = Repository(db, logger);

        Assert.True(await repository.TryAddUniqueAsync(Generated("2018-Q3"), CancellationToken.None));
        Assert.False(await repository.TryAddUniqueAsync(Generated("2018-Q3"), CancellationToken.None));

        var entry = Assert.Single(logger.Entries, e => e.Level == LogLevel.Debug);
        var error = Assert.IsType<DbUpdateException>(entry.Exception);
        Assert.Equal(2601, Assert.IsType<SqlException>(error.InnerException).Number);
    }

    [Fact]
    public async Task TryAddUniqueAsync_WhenTheTenantDoesNotExist_RethrowsTheForeignKeyError_WithoutTheUniqueIndexLog()
    {
        var logger = new CapturingLogger<AuditLogRepository>();
        await using var db = CreateDb();
        var entry = Generated("2018-Q4");
        entry.TenantId = 999_999;

        var error = await Assert.ThrowsAsync<DbUpdateException>(() =>
            Repository(db, logger).TryAddUniqueAsync(entry, CancellationToken.None));

        Assert.Equal(547, Assert.IsType<SqlException>(error.InnerException).Number);
        Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Debug);
    }

    private ReportingDbContext CreateDb(params IInterceptor[] interceptors) =>
        new(new DbContextOptionsBuilder<ReportingDbContext>()
            .UseSqlServer(_connectionString)
            .AddInterceptors(interceptors)
            .Options);

    private static AuditLogRepository Repository(ReportingDbContext db, ILogger<AuditLogRepository>? logger = null) =>
        new(db, logger ?? NullLogger<AuditLogRepository>.Instance);

    private AuditLog Generated(string quarter) => new()
    {
        TenantId = _tenantId,
        Action = AuditActions.GenerateQuarterlyInvoiceReport,
        Detail = quarter,
        OccurredAtUtc = DateTimeOffset.UtcNow,
    };

    /// <summary>Makes every save fail like a timeout wrapped by EF, before anything reaches the database.</summary>
    private sealed class FailingSaveInterceptor : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default) =>
            throw new DbUpdateException("Simulated timeout", new TimeoutException());
    }
}
