using BayiOS.Modules.Inventory.Contracts;
using BayiOS.Modules.Warehouse.Api;
using BayiOS.Modules.Warehouse.Application;
using BayiOS.Modules.Warehouse.Infrastructure.Persistence;
using BayiOS.SharedKernel.Persistence;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BayiOS.Modules.Warehouse;

public static class WarehouseModule
{
    public static IServiceCollection AddWarehouseModule(this IServiceCollection services)
    {
        // The context runs on the request's shared connection; the UnitOfWork owns its lifetime and transaction.
        services.AddDbContext<WarehouseDbContext>((sp, options) =>
            WarehouseDbContext.Configure(options, sp.GetRequiredService<UnitOfWork>().Connection));
        services.AddScoped<WarehouseService>();
        services.AddScoped<IWarehouseDirectory, WarehouseDirectory>();
        services.TryAddSingleton(TimeProvider.System);
        return services;
    }

    public static IEndpointRouteBuilder MapWarehouseModule(this IEndpointRouteBuilder app) => app.MapWarehouseEndpoints();
}
