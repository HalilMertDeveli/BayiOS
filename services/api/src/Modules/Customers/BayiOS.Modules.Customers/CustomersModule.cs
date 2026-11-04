using BayiOS.Modules.Customers.Api;
using BayiOS.Modules.Customers.Application;
using BayiOS.Modules.Customers.Infrastructure.Persistence;
using BayiOS.Modules.Pricing.Contracts;
using BayiOS.SharedKernel.Persistence;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BayiOS.Modules.Customers;

public static class CustomersModule
{
    /// <summary>Registers customers and dealer groups, and implements Pricing's <see cref="ICustomerPricingDirectory"/> port.</summary>
    public static IServiceCollection AddCustomersModule(this IServiceCollection services)
    {
        // The context runs on the request's shared connection; the UnitOfWork owns its lifetime and transaction.
        services.AddDbContext<CustomersDbContext>((sp, options) =>
            CustomersDbContext.Configure(options, sp.GetRequiredService<UnitOfWork>().Connection));
        services.AddScoped<CustomerService>();
        services.AddScoped<DealerGroupService>();
        services.AddScoped<ICustomerPricingDirectory, CustomerPricingDirectory>();
        services.TryAddSingleton(TimeProvider.System);
        return services;
    }

    public static IEndpointRouteBuilder MapCustomersModule(this IEndpointRouteBuilder app) => app.MapCustomersEndpoints();
}
