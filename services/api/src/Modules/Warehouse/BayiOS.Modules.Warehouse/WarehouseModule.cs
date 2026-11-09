using BayiOS.Modules.Inventory.Contracts;
using BayiOS.Modules.Orders.Contracts;
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

        // Fulfilment (JOB 10). The host provides IFulfillmentCatalog, ITaskAssigneeDirectory, ITaskOrderDirectory and
        // IFulfillmentSettings (Catalog/Identity/Orders adapters); Orders provides IOrderFulfillment and receives this
        // module's IOrderFulfillmentListener.
        services.AddScoped<FulfillmentAccess>();
        services.AddScoped<FulfillmentLocks>();
        services.AddScoped<FulfillmentQueries>();
        services.AddScoped<PickingTaskBuilder>();
        services.AddScoped<PickingService>();
        services.AddScoped<ShipmentService>();
        services.AddScoped<IOrderFulfillmentListener, FulfillmentListener>();

        // Re-allocation (#98): Inventory moves reservations, this module keeps the pick task lines in step.
        services.AddScoped<IReservationHolders, ReservationHolders>();
        services.AddScoped<PickingReallocator>();
        services.AddScoped<TaskVisibility>();
        services.AddScoped<IOrderTaskVisibility>(sp => sp.GetRequiredService<TaskVisibility>());
        services.AddScoped<ITaskContextVariants>(sp => sp.GetRequiredService<TaskVisibility>());
        services.TryAddSingleton(TimeProvider.System);

        // Two-stage warehouse transfers and stock counts (#168); stock moves only through IInventoryService.
        services.AddScoped<StockTransferQueries>();
        services.AddScoped<ITransferNumbers, TransferNumbers>();
        services.AddScoped<StockTransferService>();
        services.AddScoped<StockCountQueries>();
        services.AddScoped<StockCountService>();
        return services;
    }

    public static IEndpointRouteBuilder MapWarehouseModule(this IEndpointRouteBuilder app)
    {
        app.MapWarehouseEndpoints();
        app.MapFulfillmentEndpoints();
        app.MapStockDocumentEndpoints();
        return app;
    }
}
