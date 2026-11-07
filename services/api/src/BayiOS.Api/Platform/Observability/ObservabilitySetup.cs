using System.Reflection;
using BayiOS.SharedKernel.Observability;
using Microsoft.Extensions.Logging.Console;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace BayiOS.Api.Platform.Observability;

/// <summary>
/// Logging, OpenTelemetry and the error-tracking hook (#55, docs/operations/observability.md).
/// <list type="bullet">
/// <item>Non-Development: console only, one redacted JSON object per line (<see cref="RedactingJsonConsoleFormatter"/>);
/// framework categories that print URLs or SQL are raised to Warning.</item>
/// <item>OpenTelemetry traces + metrics are registered <b>only</b> when <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> is set, and
/// then export via OTLP only — nothing leaves the process by default. Standard <c>OTEL_*</c> variables apply
/// (<c>OTEL_EXPORTER_OTLP_PROTOCOL</c>, <c>OTEL_EXPORTER_OTLP_HEADERS</c>, <c>OTEL_SERVICE_NAME</c>, …).</item>
/// </list>
/// </summary>
internal static class ObservabilitySetup
{
    public const string OtlpEndpointVariable = "OTEL_EXPORTER_OTLP_ENDPOINT";
    public const string DefaultServiceName = "bayios-api";

    /// <summary>Meters exported besides <see cref="BayiOsMetrics.MeterName"/> (ASP.NET Core / HttpClient come from the instrumentation).</summary>
    public static readonly IReadOnlyList<string> AdditionalMeters = ["Npgsql", "System.Runtime"];

    public static WebApplicationBuilder AddBayiOsObservability(this WebApplicationBuilder builder)
    {
        if (!builder.Environment.IsDevelopment())
        {
            builder.Logging.ClearProviders();
            builder.Logging.AddConsole(options => options.FormatterName = RedactingJsonConsoleFormatter.FormatterName);
            builder.Logging.AddConsoleFormatter<RedactingJsonConsoleFormatter, ConsoleFormatterOptions>();

            // These categories log full URLs (query strings) or SQL text at Information.
            builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
            builder.Logging.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Warning);
            builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);
        }

        builder.Services.AddOptions<ErrorTrackingOptions>().BindConfiguration(ErrorTrackingOptions.Section)
            .Validate(
                o => ErrorTrackingOptions.Providers.All.Contains(o.Provider, StringComparer.OrdinalIgnoreCase),
                $"ErrorTracking:Provider must be one of: {string.Join(", ", ErrorTrackingOptions.Providers.All)}.")
            .ValidateOnStart();
        var provider = builder.Configuration[$"{ErrorTrackingOptions.Section}:Provider"];
        if (string.Equals(provider, ErrorTrackingOptions.Providers.OpenTelemetry, StringComparison.OrdinalIgnoreCase))
        {
            builder.Services.AddSingleton<IErrorReporter, OpenTelemetryErrorReporter>();
        }
        else
        {
            builder.Services.AddSingleton<IErrorReporter, CountingErrorReporter>();
        }

        builder.Services.AddExceptionHandler<ReportingExceptionHandler>();

        if (IsOtlpEnabled(builder.Configuration))
        {
            AddOpenTelemetry(builder);
        }

        return builder;
    }

    /// <summary>Correlation id first (log scope), then one log line per request around everything else.</summary>
    public static IApplicationBuilder UseBayiOsRequestLogging(this IApplicationBuilder app) =>
        app.UseMiddleware<RequestLoggingMiddleware>();

    public static bool IsOtlpEnabled(IConfiguration configuration) =>
        !string.IsNullOrWhiteSpace(configuration[OtlpEndpointVariable]);

    private static void AddOpenTelemetry(WebApplicationBuilder builder)
    {
        var version = typeof(ObservabilitySetup).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            .Split('+')[0] ?? "0.0.0";
        builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource
                .AddService(builder.Configuration["OTEL_SERVICE_NAME"] ?? DefaultServiceName, serviceVersion: version)
                .AddAttributes([new KeyValuePair<string, object>("deployment.environment.name", builder.Environment.EnvironmentName)]))
            .WithTracing(tracing => tracing
                .AddSource(BayiOsMetrics.ActivitySourceName)
                .AddSource("Npgsql")
                .AddAspNetCoreInstrumentation(options =>
                {
                    // Probes are noise; exceptions are recorded (redacted) by the error-tracking hook, not raw here.
                    options.Filter = context => !context.Request.Path.StartsWithSegments("/health", StringComparison.Ordinal);
                    options.RecordException = false;
                })
                .AddHttpClientInstrumentation(options => options.RecordException = false)
                .AddOtlpExporter())
            .WithMetrics(metrics => metrics
                .AddMeter(BayiOsMetrics.MeterName)
                .AddMeter([.. AdditionalMeters])
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddOtlpExporter());
    }
}
