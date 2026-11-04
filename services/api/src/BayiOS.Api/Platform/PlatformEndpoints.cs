using System.Reflection;

namespace BayiOS.Api.Platform;

internal static class PlatformEndpoints
{
    public static IEndpointRouteBuilder MapPlatformEndpoints(this IEndpointRouteBuilder app)
    {
        // Liveness only; database readiness is added with the persistence layer (#10).
        app.MapGet("/health", () => TypedResults.Ok(new HealthResponse("ok")))
            .WithName("GetHealth")
            .WithTags("Platform")
            .ExcludeFromDescription();

        app.MapGet("/api/v1/meta", (IHostEnvironment env) => TypedResults.Ok(new MetaResponse(
                Name: "BayiOS API",
                Version: ApiVersion,
                Environment: env.EnvironmentName)))
            .WithName("GetMeta")
            .WithTags("Platform")
            .WithSummary("API adı, sürümü ve ortamı");

        return app;
    }

    private static readonly string ApiVersion =
        typeof(PlatformEndpoints).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            .Split('+')[0] ?? "0.0.0";
}

internal sealed record HealthResponse(string Status);

internal sealed record MetaResponse(string Name, string Version, string Environment);
