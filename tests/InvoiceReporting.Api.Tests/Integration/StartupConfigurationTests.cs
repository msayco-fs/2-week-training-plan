using Microsoft.AspNetCore.Hosting;
using InvoiceReporting.Api.Reporting;
using InvoiceReporting.Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace InvoiceReporting.Api.Tests.Integration;

/// <summary>Startup checks that stop the API from running with unsafe settings.</summary>
public sealed class StartupConfigurationTests
{
    [Fact]
    public void OutsideDevelopment_WithoutJwtAuthorityAndAudience_FailsAtStartup()
    {
        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder
                .UseEnvironment("Production")
                .ConfigureTestServices(services => services.UseInMemoryHangfire()));

        var exception = Record.Exception(() => factory.CreateClient());

        var validation = FindInChain<OptionsValidationException>(exception);
        Assert.NotNull(validation);
        Assert.Contains("Authority", validation.Message);
    }

    [Fact]
    public void InvalidKnownProxyAddress_FailsAtStartup()
    {
        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder
                .UseEnvironment("Production")
                .UseSetting("Authentication:Schemes:Bearer:Authority", "https://identity.test.invalid/")
                .UseSetting("Authentication:Schemes:Bearer:ValidAudiences:0", "invoice-reporting-tests")
                .UseSetting("ForwardedHeaders:KnownProxies:0", "not-an-ip")
                .ConfigureTestServices(services => services.UseInMemoryHangfire()));

        var exception = Record.Exception(() => factory.CreateClient());

        Assert.NotNull(FindInChain<FormatException>(exception));
    }

    [Fact]
    public void MailjetProvider_WithoutApiKeys_FailsAtStartup()
    {
        using var factory = CreateProductionFactory(
            ("Reporting:Email:Enabled", "true"),
            ("Reporting:Email:Provider", "MailjetApi"),
            ("Reporting:Email:FromAddress", "reports@example.test"));

        var exception = Record.Exception(() => factory.CreateClient());

        var validation = FindInChain<OptionsValidationException>(exception);
        Assert.NotNull(validation);
        Assert.Contains("Mailjet:ApiKey", validation.Message);
    }

    [Fact]
    public void MailjetProvider_WithAnHttpBaseUrl_FailsAtStartup()
    {
        using var factory = CreateProductionFactory(
            ("Reporting:Email:Enabled", "true"),
            ("Reporting:Email:Provider", "MailjetApi"),
            ("Reporting:Email:FromAddress", "reports@example.test"),
            ("Reporting:Email:Mailjet:ApiKey", "test-api-key"),
            ("Reporting:Email:Mailjet:SecretKey", "test-secret-key"),
            ("Reporting:Email:Mailjet:BaseUrl", "http://api.mailjet.com"));

        var exception = Record.Exception(() => factory.CreateClient());

        var validation = FindInChain<OptionsValidationException>(exception);
        Assert.NotNull(validation);
        Assert.Contains("https Mailjet:BaseUrl", validation.Message);
    }

    [Fact]
    public void MailjetProvider_WithAllSettings_StartsAndUsesTheMailjetSender()
    {
        using var factory = CreateProductionFactory(
            ("Reporting:Email:Enabled", "true"),
            ("Reporting:Email:Provider", "MailjetApi"),
            ("Reporting:Email:FromAddress", "reports@example.test"),
            ("Reporting:Email:Mailjet:ApiKey", "test-api-key"),
            ("Reporting:Email:Mailjet:SecretKey", "test-secret-key"));

        var exception = Record.Exception(() => factory.CreateClient());

        Assert.Null(exception);
        Assert.IsType<MailjetReportEmailSender>(factory.Services.GetRequiredService<IReportEmailSender>());
    }

    [Fact]
    public void DefaultProvider_UsesTheSmtpSender()
    {
        using var factory = CreateProductionFactory();

        factory.CreateClient();

        Assert.IsType<SmtpReportEmailSender>(factory.Services.GetRequiredService<IReportEmailSender>());
    }

    [Theory]
    [InlineData("0")]
    [InlineData("10485761")] // 10 MB + 1 byte.
    public void MaxAttachmentBytes_OutsideTheAllowedRange_FailsAtStartup(string maxAttachmentBytes)
    {
        using var factory = CreateProductionFactory(
            ("Reporting:Email:MaxAttachmentBytes", maxAttachmentBytes));

        var exception = Record.Exception(() => factory.CreateClient());

        var validation = FindInChain<OptionsValidationException>(exception);
        Assert.NotNull(validation);
        Assert.Contains("MaxAttachmentBytes", validation.Message);
    }

    [Theory]
    [InlineData("00:00:00")]
    [InlineData("00:59:59")]
    public void StaleTempFileAge_UnderOneHour_FailsAtStartup(string staleTempFileAge)
    {
        using var factory = CreateProductionFactory(
            ("Reporting:QuarterlyOutput:StaleTempFileAge", staleTempFileAge));

        var exception = Record.Exception(() => factory.CreateClient());

        var validation = FindInChain<OptionsValidationException>(exception);
        Assert.NotNull(validation);
        Assert.Contains("StaleTempFileAge", validation.Message);
    }

    /// <summary>A Production host with valid JWT settings, plus the given extra settings.</summary>
    private static WebApplicationFactory<Program> CreateProductionFactory(params (string Key, string Value)[] settings) =>
        new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder
                    .UseEnvironment("Production")
                    .UseSetting("Authentication:Schemes:Bearer:Authority", "https://identity.test.invalid/")
                    .UseSetting("Authentication:Schemes:Bearer:ValidAudiences:0", "invoice-reporting-tests")
                    .ConfigureTestServices(services => services.UseInMemoryHangfire());

                foreach (var (key, value) in settings)
                {
                    builder.UseSetting(key, value);
                }
            });

    private static T? FindInChain<T>(Exception? exception) where T : Exception
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is T match)
            {
                return match;
            }

            if (current is AggregateException aggregate)
            {
                return aggregate.InnerExceptions.Select(FindInChain<T>).FirstOrDefault(e => e is not null);
            }
        }

        return null;
    }
}
