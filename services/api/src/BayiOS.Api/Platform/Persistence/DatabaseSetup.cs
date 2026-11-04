using System.Text.Json;
using BayiOS.SharedKernel.Persistence;
using BayiOS.SharedKernel.Tenancy;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Npgsql;

namespace BayiOS.Api.Platform.Persistence;

internal sealed class DatabaseOptions
{
    /// <summary><c>ConnectionStrings:Default</c> — must use the runtime role <c>bayios_app</c>.</summary>
    public string ConnectionString { get; set; } = string.Empty;
}

internal static class DatabaseSetup
{
    public static IServiceCollection AddDatabase(this IServiceCollection services)
    {
        services.AddOptions<DatabaseOptions>()
            .Configure<IConfiguration>((options, configuration) =>
                options.ConnectionString = configuration.GetConnectionString("Default") ?? string.Empty)
            .Validate(o => !string.IsNullOrWhiteSpace(o.ConnectionString), "ConnectionStrings:Default is required.")
            .ValidateOnStart();

        services.AddSingleton(sp => NpgsqlDataSource.Create(sp.GetRequiredService<IOptions<DatabaseOptions>>().Value.ConnectionString));
        services.AddScoped<RequestContext>();
        services.AddScoped<IRequestContext>(sp => sp.GetRequiredService<RequestContext>());
        services.AddScoped<UnitOfWork>();

        services.AddHealthChecks()
            .AddCheck<DatabaseHealthCheck>("database", tags: ["ready"], timeout: TimeSpan.FromSeconds(5));
        return services;
    }

    /// <summary><c>/health/ready</c>: 200 when the database answers, 503 otherwise. <c>/health</c> stays dependency-free.</summary>
    public static IEndpointRouteBuilder MapReadiness(this IEndpointRouteBuilder app)
    {
        app.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains("ready"),
            ResponseWriter = (context, report) =>
            {
                context.Response.ContentType = "application/json";
                var status = report.Status == HealthStatus.Healthy ? "ready" : "unavailable";
                return context.Response.WriteAsync(JsonSerializer.Serialize(new { status }));
            },
        });
        return app;
    }
}

internal sealed class DatabaseHealthCheck(NpgsqlDataSource dataSource) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var command = dataSource.CreateCommand("SELECT 1");
            await command.ExecuteScalarAsync(cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException or OperationCanceledException)
        {
            // Details stay in the server log (health check publisher), never in the anonymous response.
            return HealthCheckResult.Unhealthy("Database unavailable", ex);
        }
    }
}
