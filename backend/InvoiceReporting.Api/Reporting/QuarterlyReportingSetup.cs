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
        services.AddOptions<QuarterlyReportOutputOptions>()
            .Bind(configuration.GetSection(QuarterlyReportOutputOptions.SectionName))
            .Validate(
                options => options.StaleTempFileAge >= QuarterlyReportOutputOptions.MinStaleTempFileAge,
                "Reporting:QuarterlyOutput:StaleTempFileAge must be at least 1 hour, so a report still being written is never deleted.")
            .ValidateOnStart();
        services.AddScoped<QuarterlyReportTempFileCleanupJob>();
        services.AddSingleton<IQuarterlyReportActivity, HangfireQuarterlyReportActivity>();
        services.AddSingleton<QuarterlyReportCsvWriter>();

        services.AddScoped<QuarterlyReportEmailJob>();
        services.AddSingleton<FailedReportEmailLog>();
        AddEmailSender(services);
        services.AddScoped<IReportRecipientRepository, ReportRecipientRepository>();
        services.AddScoped<IEmailDeliveryRepository, EmailDeliveryRepository>();

        AddEmailOptions(services, configuration);

        return services;
    }

    /// <summary>
    /// Adds the daily failed-email log file (<see cref="FailedReportEmailLogFile"/>)
    /// as an extra logging provider. Runs before the app is built, so it reads
    /// the settings straight from configuration.
    /// </summary>
    public static WebApplicationBuilder AddFailedReportEmailLogFile(this WebApplicationBuilder builder)
    {
        var email = builder.Configuration.GetSection(QuarterlyReportEmailOptions.SectionName).Get<QuarterlyReportEmailOptions>()
            ?? new QuarterlyReportEmailOptions();

        // Bad values are reported by the options validation at startup; don't
        // fail here first with a less clear Serilog error.
        if (string.IsNullOrWhiteSpace(email.FailedLogDirectory) || email.FailedLogRetentionDays <= 0)
        {
            return builder;
        }

        builder.Logging.AddFailedReportEmailLogFile(
            FailedReportEmailLogFile.ResolveDirectory(builder.Environment.ContentRootPath, email.FailedLogDirectory),
            email.FailedLogRetentionDays);
        return builder;
    }

    /// <summary>
    /// Creates the failed-email log folder and logs a clear error naming the
    /// path when it can't be written. Does not stop the app: the audit log
    /// still records failures.
    /// </summary>
    public static WebApplication CheckFailedReportEmailLogFolder(this WebApplication app)
    {
        var email = app.Services.GetRequiredService<IOptions<QuarterlyReportEmailOptions>>().Value;
        var directory = FailedReportEmailLogFile.ResolveDirectory(app.Environment.ContentRootPath, email.FailedLogDirectory);

        var error = FailedReportEmailLogFile.CheckDirectory(directory);
        if (error is not null)
        {
            app.Services.GetRequiredService<ILoggerFactory>()
                .CreateLogger(typeof(QuarterlyReportingSetup))
                .LogError(error, "Failed report emails can't be written to {Directory}; check the folder or volume permissions", directory);
        }

        return app;
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
