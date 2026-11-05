using Anthropic;
using BayiOS.Modules.Integrations.Api;
using BayiOS.Modules.Integrations.Application;
using BayiOS.Modules.Integrations.Contracts;
using BayiOS.Modules.Integrations.Infrastructure.Persistence;
using BayiOS.Modules.Integrations.Parsing;
using BayiOS.SharedKernel.Persistence;
using BayiOS.SharedKernel.Platform.Jobs;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace BayiOS.Modules.Integrations;

public static class IntegrationsModule
{
    /// <summary>The named HttpClient the Claude SDK uses (tests replace its primary handler).</summary>
    public const string AnthropicHttpClient = "anthropic";

    /// <summary>Tenant-less maintenance job clearing AI parse texts past retention (scheduled by the platform worker).</summary>
    public const string TextRetentionJobType = "integrations.ai_text_retention";

    /// <summary>
    /// Registers the WhatsApp order assistant (ADR 0010). The host must provide <see cref="IAssistantCatalog"/>
    /// (Catalog adapter) and <see cref="IAssistantOrderDrafts"/> (Orders adapter), and schedule
    /// <see cref="ParseTextRetentionJob.Type"/> as a tenant-less maintenance job.
    /// </summary>
    public static IServiceCollection AddIntegrationsModule(this IServiceCollection services)
    {
        // The context runs on the request's shared connection; the UnitOfWork owns its lifetime and transaction.
        services.AddDbContext<IntegrationsDbContext>((sp, options) =>
            IntegrationsDbContext.Configure(options, sp.GetRequiredService<UnitOfWork>().Connection));
        services.AddOptions<AiOptions>().BindConfiguration(AiOptions.Section)
            .ValidateDataAnnotations()
            .Validate(o => IsKnownEffort(o.Effort), "Ai:Effort must be one of low, medium, high, xhigh, max.")
            .Validate(o => !string.IsNullOrWhiteSpace(o.Model), "Ai:Model is required.")
            .ValidateOnStart();

        services.AddHttpClient(AnthropicHttpClient);
        services.TryAddSingleton(sp =>
        {
            var settings = sp.GetRequiredService<IOptions<AiOptions>>().Value;
            // Server-side only (data-policy §9). Never logged; absent → the AI parser is never resolved.
            var apiKey = sp.GetRequiredService<IConfiguration>()[AiOptions.ApiKeyKey];
            var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient(AnthropicHttpClient);
            var timeout = TimeSpan.FromSeconds(settings.TimeoutSeconds);
            return string.IsNullOrWhiteSpace(settings.BaseUrl)
                ? new AnthropicClient { ApiKey = apiKey, HttpClient = http, Timeout = timeout, MaxRetries = settings.MaxRetries }
                : new AnthropicClient { ApiKey = apiKey, HttpClient = http, Timeout = timeout, MaxRetries = settings.MaxRetries, BaseUrl = settings.BaseUrl };
        });
        services.TryAddSingleton<AnthropicOrderParser>();
        services.TryAddSingleton<RuleBasedOrderParser>();
        services.AddScoped<AiParserAccessor>();
        services.AddSingleton<AiUsageMeter>();
        services.AddScoped<OrderParseService>();
        services.AddJobHandler<ParseTextRetentionJob>(ParseTextRetentionJob.Type);
        services.TryAddSingleton(TimeProvider.System);
        return services;
    }

    public static IEndpointRouteBuilder MapIntegrationsModule(this IEndpointRouteBuilder app) => app.MapAssistantEndpoints();

    private static bool IsKnownEffort(string? effort) =>
        effort?.Trim().ToLowerInvariant() is "low" or "medium" or "high" or "xhigh" or "max";
}
