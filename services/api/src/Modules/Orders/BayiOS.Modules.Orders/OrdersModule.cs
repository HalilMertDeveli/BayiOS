using BayiOS.Modules.Orders.Api;
using BayiOS.Modules.Orders.Application;
using BayiOS.Modules.Orders.Contracts;
using BayiOS.Modules.Orders.Infrastructure.Persistence;
using BayiOS.SharedKernel.Persistence;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BayiOS.Modules.Orders;

public static class OrdersModule
{
    /// <summary>
    /// Registers orders. The host must also provide <see cref="IOrderVariantDirectory"/> (Catalog adapter) and the
    /// Inventory / Pricing modules with their ports (Orders uses their contracts and Pricing's customer port).
    /// </summary>
    public static IServiceCollection AddOrdersModule(this IServiceCollection services)
    {
        // The context runs on the request's shared connection; the UnitOfWork owns its lifetime and transaction.
        services.AddDbContext<OrdersDbContext>((sp, options) =>
            OrdersDbContext.Configure(options, sp.GetRequiredService<UnitOfWork>().Connection));
        services.AddScoped<OrderQueries>();
        services.AddScoped<OrderService>();
        services.TryAddSingleton(TimeProvider.System);
        return services;
    }

    public static IEndpointRouteBuilder MapOrdersModule(this IEndpointRouteBuilder app) => app.MapOrdersEndpoints();
}
