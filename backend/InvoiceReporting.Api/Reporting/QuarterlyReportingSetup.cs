using Hangfire;
using InvoiceReporting.Api.Data.Repositories;
using Microsoft.Extensions.Options;

namespace InvoiceReporting.Api.Reporting;

/// <summary>
/// Startup wiring for quarterly reports: the CSV job, the email job, their
/// settings and the recurring schedule. Keeps this out of Program.cs.
/// Hangfire itself is set up in Program.cs.
/// </summary>
public static class QuarterlyReportingSetup
{
    /// <summary>Registers the quarterly report jobs, the email sender and their validated settings.</summary>
    public static IServiceCollection AddQuarterlyReporting(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<QuarterlyReportScheduler>();
        services.AddScoped<QuarterlyReportJob>();
        services.Configure<QuarterlyReportOutputOptions>(
            configuration.GetSection(QuarterlyReportOutputOptions.SectionName));
        services.AddSingleton<QuarterlyReportCsvWriter>();

        services.AddScoped<QuarterlyReportEmailJob>();
        services.AddSingleton<FailedReportEmailLog>();
        AddEmailSender(services);
        services.AddScoped<IReportRecipientRepository, ReportRecipientRepository>();
        services.AddScoped<IEmailDeliveryRepository, EmailDeliveryRepository>();

        AddEmailOptions(services, configuration);

        return services;
    }

    /// <summary>Adds or updates the Hangfire recurring job that queues every tenant's quarterly report.</summary>
    public static WebApplication ScheduleQuarterlyReports(this WebApplication app)
    {
        var recurring = app.Services.GetRequiredService<IRecurringJobManager>();

        recurring.AddOrUpdate<QuarterlyReportScheduler>(
            QuarterlyReportScheduler.RecurringJobId,
            job => job.QueueAllTenantsAsync(CancellationToken.None),
            QuarterlyReportScheduler.CronExpression,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        return app;
    }

    /// <summary>
    /// Registers both senders and resolves <see cref="IReportEmailSender"/> to the
    /// one picked by <see cref="QuarterlyReportEmailOptions.Provider"/>.
    /// </summary>
    private static void AddEmailSender(IServiceCollection services)
    {
        services.AddSingleton<SmtpReportEmailSender>();
        services.AddHttpClient<MailjetReportEmailSender>((provider, client) =>
        {
            var mailjet = provider.GetRequiredService<IOptions<QuarterlyReportEmailOptions>>().Value.Mailjet;
            client.BaseAddress = new Uri(mailjet.BaseUrl.TrimEnd('/') + "/");
            client.Timeout = mailjet.Timeout;
        });

        services.AddTransient<IReportEmailSender>(provider =>
            provider.GetRequiredService<IOptions<QuarterlyReportEmailOptions>>().Value.Provider switch
            {
                EmailProvider.MailjetApi => provider.GetRequiredService<MailjetReportEmailSender>(),
                _ => provider.GetRequiredService<SmtpReportEmailSender>(),
            });
    }

    // Fail at startup on bad email settings instead of failing later inside a
    // background job.
    private static void AddEmailOptions(IServiceCollection services, IConfiguration configuration) =>
        services.AddOptions<QuarterlyReportEmailOptions>()
            .Bind(configuration.GetSection(QuarterlyReportEmailOptions.SectionName))
            .Validate(
                options => options.MaxAttachmentBytes is > 0 and <= QuarterlyReportEmailOptions.MaxAllowedAttachmentBytes,
                "Reporting:Email:MaxAttachmentBytes must be greater than 0 and at most 10 MB.")
            .Validate(
                options => !string.IsNullOrWhiteSpace(options.FailedLogDirectory) && options.FailedLogRetentionDays > 0,
                "Reporting:Email must set FailedLogDirectory and a FailedLogRetentionDays greater than 0.")
            .Validate(
                options => !options.Enabled
                    || options.Provider != EmailProvider.Smtp
                    || (!string.IsNullOrWhiteSpace(options.Host)
                        && options.Port is > 0 and <= 65535
                        && !string.IsNullOrWhiteSpace(options.FromAddress)),
                "Reporting:Email must set Host, Port and FromAddress when Enabled is true and Provider is Smtp.")
            .Validate(
                options => !options.Enabled
                    || options.Provider != EmailProvider.MailjetApi
                    || (!string.IsNullOrWhiteSpace(options.Mailjet.ApiKey)
                        && !string.IsNullOrWhiteSpace(options.Mailjet.SecretKey)
                        && !string.IsNullOrWhiteSpace(options.FromAddress)
                        && Uri.TryCreate(options.Mailjet.BaseUrl, UriKind.Absolute, out var baseUrl)
                        && baseUrl.Scheme == Uri.UriSchemeHttps
                        && options.Mailjet.Timeout > TimeSpan.Zero),
                "Reporting:Email must set Mailjet:ApiKey, Mailjet:SecretKey, FromAddress, an https Mailjet:BaseUrl "
                    + "and a positive Mailjet:Timeout when Enabled is true and Provider is MailjetApi.")
            .ValidateOnStart();
}
