using BayiOS.Modules.Pricing.Api;
using BayiOS.Modules.Pricing.Application;
using BayiOS.Modules.Pricing.Contracts;
using BayiOS.Modules.Pricing.Infrastructure.Persistence;
using BayiOS.SharedKernel.Persistence;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BayiOS.Modules.Pricing;

public static class PricingModule
{
    /// <summary>
    /// Registers price lists and the <see cref="IPriceResolver"/>. The host must also provide the ports
    /// <see cref="ICustomerPricingDirectory"/> (Customers module), <see cref="IPricingVariantDirectory"/> (Catalog) and
    /// <see cref="ITenantCurrencyProvider"/> (tenant settings).
    /// </summary>
    public static IServiceCollection AddPricingModule(this IServiceCollection services)
    {
        // The context runs on the request's shared connection; the UnitOfWork owns its lifetime and transaction.
        services.AddDbContext<PricingDbContext>((sp, options) =>
            PricingDbContext.Configure(options, sp.GetRequiredService<UnitOfWork>().Connection));
        services.AddScoped<PriceListService>();
        services.AddScoped<IPriceResolver, PriceResolver>();
        services.AddScoped<IPriceListDirectory, PriceListDirectory>();
        services.TryAddSingleton(TimeProvider.System);
        return services;
    }

    public static IEndpointRouteBuilder MapPricingModule(this IEndpointRouteBuilder app) => app.MapPricingEndpoints();
}
