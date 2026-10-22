using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Options;

namespace InvoiceReporting.Api.Tests.Integration;

/// <summary>Startup checks that stop the API from running with unsafe settings.</summary>
public sealed class StartupConfigurationTests
{
    [Fact]
    public void OutsideDevelopment_WithoutJwtAuthorityAndAudience_FailsAtStartup()
    {
        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.UseEnvironment("Production"));

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
                .UseSetting("ForwardedHeaders:KnownProxies:0", "not-an-ip"));

        var exception = Record.Exception(() => factory.CreateClient());

        Assert.NotNull(FindInChain<FormatException>(exception));
    }

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
