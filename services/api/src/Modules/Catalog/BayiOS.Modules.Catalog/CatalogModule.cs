using BayiOS.Modules.Catalog.Api;
using BayiOS.Modules.Catalog.Application;
using BayiOS.Modules.Catalog.Infrastructure.Persistence;
using BayiOS.SharedKernel.Persistence;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BayiOS.Modules.Catalog;

public static class CatalogModule
{
    public static IServiceCollection AddCatalogModule(this IServiceCollection services)
    {
        // The context runs on the request's shared connection; the UnitOfWork owns its lifetime and transaction.
        services.AddDbContext<CatalogDbContext>((sp, options) =>
            CatalogDbContext.Configure(options, sp.GetRequiredService<UnitOfWork>().Connection));
        services.AddScoped<ReferenceDataService>();
        services.AddScoped<ProductService>();
        services.AddScoped<CustomFieldService>();
        services.TryAddSingleton(TimeProvider.System);
        return services;
    }

    public static IEndpointRouteBuilder MapCatalogModule(this IEndpointRouteBuilder app) => app.MapCatalogEndpoints();
}
