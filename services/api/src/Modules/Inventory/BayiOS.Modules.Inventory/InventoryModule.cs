using BayiOS.Modules.Inventory.Api;
using BayiOS.Modules.Inventory.Application;
using BayiOS.Modules.Inventory.Contracts;
using BayiOS.Modules.Inventory.Infrastructure.Persistence;
using BayiOS.SharedKernel.Persistence;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace BayiOS.Modules.Inventory;

public static class InventoryModule
{
    /// <summary>
    /// Registers the stock engine. The host must also provide the ports <see cref="IWarehouseDirectory"/> (Warehouse
    /// module), <see cref="IVariantDirectory"/> (Catalog) and <see cref="INegativeStockPolicyProvider"/> (tenant settings).
    /// </summary>
    public static IServiceCollection AddInventoryModule(this IServiceCollection services)
    {
        // The context runs on the request's shared connection; the UnitOfWork owns its lifetime and transaction.
        services.AddDbContext<InventoryDbContext>((sp, options) =>
            InventoryDbContext.Configure(options, sp.GetRequiredService<UnitOfWork>().Connection));
        services.AddScoped<StockStore>();
        services.AddScoped<IInventoryService, InventoryService>();
        services.AddScoped<InventoryQueries>();
        services.AddScoped<IStockQueries>(sp => sp.GetRequiredService<InventoryQueries>());
        services.AddScoped<ThresholdService>();
        services.AddScoped<IStockThresholdQueries>(sp => sp.GetRequiredService<ThresholdService>());
        return services;
    }

    public static IEndpointRouteBuilder MapInventoryModule(this IEndpointRouteBuilder app) => app.MapInventoryEndpoints();
}
