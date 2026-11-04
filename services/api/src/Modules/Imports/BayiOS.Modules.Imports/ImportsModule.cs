using BayiOS.Modules.Imports.Api;
using BayiOS.Modules.Imports.Application;
using BayiOS.Modules.Imports.Contracts;
using BayiOS.Modules.Imports.Infrastructure.Files;
using BayiOS.Modules.Imports.Infrastructure.Persistence;
using BayiOS.SharedKernel.Persistence;
using BayiOS.SharedKernel.Platform.Jobs;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BayiOS.Modules.Imports;

public static class ImportsModule
{
    /// <summary>
    /// Registers bulk import (uploads, mapping, dry run, chunked execution jobs). The host must provide the ports
    /// <see cref="IImportCatalogGateway"/>, <see cref="IImportPriceGateway"/> and <see cref="IImportActorDirectory"/>.
    /// </summary>
    public static IServiceCollection AddImportsModule(this IServiceCollection services)
    {
        // The context runs on the request's shared connection; the UnitOfWork owns its lifetime and transaction.
        services.AddDbContext<ImportsDbContext>((sp, options) =>
            ImportsDbContext.Configure(options, sp.GetRequiredService<UnitOfWork>().Connection));
        services.AddOptions<ImportsOptions>().BindConfiguration(ImportsOptions.Section);
        services.AddScoped<IImportFileStore, DatabaseImportFileStore>();
        services.AddScoped<ImportService>();
        services.AddScoped<ImportRowValidator>();
        services.AddScoped<ImportChunkProcessor>();
        services.TryAddScoped<IColumnSuggestionProvider, HeaderSynonymSuggestionProvider>();
        services.AddJobHandler<ValidateImportJob>(ValidateImportJob.Type);
        services.AddJobHandler<ExecuteImportJob>(ExecuteImportJob.Type);
        services.TryAddSingleton(TimeProvider.System);
        return services;
    }

    public static IEndpointRouteBuilder MapImportsModule(this IEndpointRouteBuilder app) => app.MapImportsEndpoints();
}
