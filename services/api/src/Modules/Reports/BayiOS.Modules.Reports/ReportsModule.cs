using BayiOS.Modules.Reports.Api;
using BayiOS.Modules.Reports.Application;
using BayiOS.Modules.Reports.Contracts;
using BayiOS.Modules.Reports.Infrastructure.Persistence;
using BayiOS.SharedKernel.Persistence;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BayiOS.Modules.Reports;

public static class ReportsModule
{
    /// <summary>
    /// Registers the read-only reports and their CSV/XLSX export (PRD module 09). The host must provide the ports
    /// <see cref="IReportCatalogSource"/>, <see cref="IReportInventorySource"/>, <see cref="IReportOrderSource"/>,
    /// <see cref="IReportWarehouseSource"/> and <see cref="IReportAiUsageSource"/>.
    /// </summary>
    public static IServiceCollection AddReportsModule(this IServiceCollection services)
    {
        // The context runs on the request's shared connection; the UnitOfWork owns its lifetime and transaction.
        services.AddDbContext<ReportsDbContext>((sp, options) =>
            ReportsDbContext.Configure(options, sp.GetRequiredService<UnitOfWork>().Connection));
        services.AddOptions<ReportsOptions>().BindConfiguration(ReportsOptions.Section)
            .Validate(o => o.ExportLimit >= 0, "Reports:ExportLimit must not be negative.")
            .Validate(o => o.ExportWindow > TimeSpan.Zero, "Reports:ExportWindow must be positive.");
        services.AddScoped<ReportService>();
        services.AddScoped<ReportExportService>();
        services.TryAddSingleton(TimeProvider.System);
        return services;
    }

    public static IEndpointRouteBuilder MapReportsModule(this IEndpointRouteBuilder app) => app.MapReportsEndpoints();
}
