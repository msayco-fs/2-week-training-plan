using InvoiceReporting.Api.Security;
using InvoiceReporting.Api.Tests.Infrastructure;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace InvoiceReporting.Api.Tests.Authorization;

public sealed class TenantDenialMonitorTests
{
    [Fact]
    public void ReachingTheThreshold_LogsOneProbeAlert_NotOnePerLaterDenial()
    {
        var logger = new CapturingLogger<TenantDenialMonitor>();
        var monitor = Create(logger, threshold: 3);

        monitor.RecordDenial("user-1");
        monitor.RecordDenial("user-1");
        Assert.Empty(logger.Entries);

        monitor.RecordDenial("user-1");
        monitor.RecordDenial("user-1");

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Equal("TenantAccessProbeSuspected", entry.EventId.Name);
        Assert.Equal("user-1", entry.State["Subject"]);
        Assert.Equal(3, entry.State["DenialCount"]);
    }

    [Fact]
    public void DenialsAreCountedPerSubject()
    {
        var logger = new CapturingLogger<TenantDenialMonitor>();
        var monitor = Create(logger, threshold: 2);

        monitor.RecordDenial("user-1");
        monitor.RecordDenial("user-2");

        Assert.Empty(logger.Entries);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void DenialsWithoutASubject_AreIgnored(string? subject)
    {
        var logger = new CapturingLogger<TenantDenialMonitor>();
        var monitor = Create(logger, threshold: 1);

        monitor.RecordDenial(subject);

        Assert.Empty(logger.Entries);
    }

    internal static TenantDenialMonitor Create(CapturingLogger<TenantDenialMonitor>? logger = null, int threshold = 5) =>
        new(
            new MemoryCache(new MemoryCacheOptions()),
            Options.Create(new TenantDenialAlertOptions { Threshold = threshold, Window = TimeSpan.FromMinutes(5) }),
            logger ?? new CapturingLogger<TenantDenialMonitor>());
}
