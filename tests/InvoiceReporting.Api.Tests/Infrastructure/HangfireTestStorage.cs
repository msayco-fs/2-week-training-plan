using Hangfire;
using Hangfire.InMemory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace InvoiceReporting.Api.Tests.Infrastructure;

/// <summary>
/// Swaps Hangfire's SQL Server storage for in-memory storage, so tests start
/// without a "Hangfire" connection string or a database.
/// </summary>
public static class HangfireTestStorage
{
    public static IServiceCollection UseInMemoryHangfire(this IServiceCollection services)
    {
        // The app's IGlobalConfiguration registration runs UseSqlServerStorage,
        // which opens a SQL connection at once. Replace it so that never runs.
        services.RemoveAll<IGlobalConfiguration>();
        services.AddSingleton<IGlobalConfiguration>(_ => GlobalConfiguration.Configuration);

        services.RemoveAll<JobStorage>();
        services.AddSingleton<JobStorage>(_ => new InMemoryStorage());
        return services;
    }
}
