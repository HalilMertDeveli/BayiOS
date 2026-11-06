using System.Net.Http.Headers;
using System.Security.Cryptography;
using BayiOS.IntegrationTests.Infrastructure;
using BayiOS.Modules.Billing.Infrastructure.Persistence;
using BayiOS.Modules.Catalog.Infrastructure.Persistence;
using BayiOS.Modules.Customers.Infrastructure.Persistence;
using BayiOS.Modules.Identity.Infrastructure.Persistence;
using BayiOS.Modules.Imports.Infrastructure.Persistence;
using BayiOS.Modules.Integrations.Infrastructure.Persistence;
using BayiOS.Modules.Inventory.Infrastructure.Persistence;
using BayiOS.Modules.Orders.Infrastructure.Persistence;
using BayiOS.Modules.Platform.Persistence;
using BayiOS.Modules.Pricing.Infrastructure.Persistence;
using BayiOS.Modules.Reports.Infrastructure.Persistence;
using BayiOS.Modules.Warehouse.Infrastructure.Persistence;
using BayiOS.SharedKernel.Tenancy;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;

[assembly: AssemblyFixture(typeof(ApiFixture))]

namespace BayiOS.IntegrationTests.Infrastructure;

/// <summary>
/// One PostgreSQL 17 container (Testcontainers) per test run, bootstrapped exactly like local compose
/// (infra/docker/initdb/01-roles.sql) and migrated as bayios_migrator. The API connects as bayios_app and
/// validates tokens against an in-memory test JWKS — no real Supabase keys are involved.
/// </summary>
public sealed class ApiFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();

    public string SuperuserConnectionString { get; private set; } = string.Empty;

    public string AppConnectionString { get; private set; } = string.Empty;

    public string MigratorConnectionString { get; private set; } = string.Empty;

    public async ValueTask InitializeAsync()
    {
        await _postgres.StartAsync();
        SuperuserConnectionString = _postgres.GetConnectionString();

        var appPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var migratorPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        AppConnectionString = WithUser(SuperuserConnectionString, "bayios_app", appPassword);
        MigratorConnectionString = WithUser(SuperuserConnectionString, "bayios_migrator", migratorPassword);

        var bootstrap = await File.ReadAllTextAsync(Path.Combine(RepoPaths.Root, "infra", "docker", "initdb", "01-roles.sql"));
        await using (var connection = new NpgsqlConnection(SuperuserConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            // Hex-only generated passwords: safe to inline (ALTER ROLE takes no bind parameters).
            command.CommandText = bootstrap
                + $"\nALTER ROLE bayios_app PASSWORD '{appPassword}';"
                + $"\nALTER ROLE bayios_migrator PASSWORD '{migratorPassword}';";
            await command.ExecuteNonQueryAsync();
        }

        var options = new DbContextOptionsBuilder<IdentityDbContext>();
        IdentityDbContext.Configure(options, MigratorConnectionString);
        await using (var db = new IdentityDbContext(options.Options, new RequestContext()))
        {
            await db.Database.MigrateAsync();
        }

        var platformOptions = new DbContextOptionsBuilder<PlatformDbContext>();
        PlatformDbContext.Configure(platformOptions, MigratorConnectionString);
        await using (var platform = new PlatformDbContext(platformOptions.Options))
        {
            await platform.Database.MigrateAsync();
        }

        var catalogOptions = new DbContextOptionsBuilder<CatalogDbContext>();
        CatalogDbContext.Configure(catalogOptions, MigratorConnectionString);
        await using (var catalog = new CatalogDbContext(catalogOptions.Options, new RequestContext()))
        {
            await catalog.Database.MigrateAsync();
        }

        var warehouseOptions = new DbContextOptionsBuilder<WarehouseDbContext>();
        WarehouseDbContext.Configure(warehouseOptions, MigratorConnectionString);
        await using (var warehouse = new WarehouseDbContext(warehouseOptions.Options, new RequestContext()))
        {
            await warehouse.Database.MigrateAsync();
        }

        var inventoryOptions = new DbContextOptionsBuilder<InventoryDbContext>();
        InventoryDbContext.Configure(inventoryOptions, MigratorConnectionString);
        await using (var inventory = new InventoryDbContext(inventoryOptions.Options, new RequestContext()))
        {
            await inventory.Database.MigrateAsync();
        }

        var pricingOptions = new DbContextOptionsBuilder<PricingDbContext>();
        PricingDbContext.Configure(pricingOptions, MigratorConnectionString);
        await using (var pricing = new PricingDbContext(pricingOptions.Options, new RequestContext()))
        {
            await pricing.Database.MigrateAsync();
        }

        var customersOptions = new DbContextOptionsBuilder<CustomersDbContext>();
        CustomersDbContext.Configure(customersOptions, MigratorConnectionString);
        await using (var customers = new CustomersDbContext(customersOptions.Options, new RequestContext()))
        {
            await customers.Database.MigrateAsync();
        }

        var importsOptions = new DbContextOptionsBuilder<ImportsDbContext>();
        ImportsDbContext.Configure(importsOptions, MigratorConnectionString);
        await using (var imports = new ImportsDbContext(importsOptions.Options, new RequestContext()))
        {
            await imports.Database.MigrateAsync();
        }

        var ordersOptions = new DbContextOptionsBuilder<OrdersDbContext>();
        OrdersDbContext.Configure(ordersOptions, MigratorConnectionString);
        await using (var orders = new OrdersDbContext(ordersOptions.Options, new RequestContext()))
        {
            await orders.Database.MigrateAsync();
        }

        var billingOptions = new DbContextOptionsBuilder<BillingDbContext>();
        BillingDbContext.Configure(billingOptions, MigratorConnectionString);
        await using (var billing = new BillingDbContext(billingOptions.Options, new RequestContext()))
        {
            await billing.Database.MigrateAsync();
        }

        var integrationsOptions = new DbContextOptionsBuilder<IntegrationsDbContext>();
        IntegrationsDbContext.Configure(integrationsOptions, MigratorConnectionString);
        await using (var integrations = new IntegrationsDbContext(integrationsOptions.Options, new RequestContext()))
        {
            await integrations.Database.MigrateAsync();
        }

        var reportsOptions = new DbContextOptionsBuilder<ReportsDbContext>();
        ReportsDbContext.Configure(reportsOptions, MigratorConnectionString);
        await using (var reports = new ReportsDbContext(reportsOptions.Options, new RequestContext()))
        {
            await reports.Database.MigrateAsync();
        }
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    /// <summary>
    /// Runs a repository SQL script with the real <c>psql</c> inside the database container, as <paramref name="user"/>
    /// (operator tools under <c>infra/ops</c>, #129). Variables are passed with <c>-v name=value</c>.
    /// </summary>
    public async Task<(long ExitCode, string Stdout, string Stderr)> PsqlAsync(
        string repoRelativeScript, string user, IReadOnlyDictionary<string, string> variables, CancellationToken cancellationToken)
    {
        var target = $"/tmp/{Guid.NewGuid():N}.sql";
        await _postgres.CopyAsync(await File.ReadAllBytesAsync(Path.Combine(RepoPaths.Root, repoRelativeScript), cancellationToken), target, ct: cancellationToken);
        var connection = new NpgsqlConnectionStringBuilder(SuperuserConnectionString);
        var password = user switch
        {
            "bayios_app" => new NpgsqlConnectionStringBuilder(AppConnectionString).Password,
            "bayios_migrator" => new NpgsqlConnectionStringBuilder(MigratorConnectionString).Password,
            _ => connection.Password,
        };
        var command = new List<string> { "env", $"PGPASSWORD={password}", "psql", "-h", "127.0.0.1", "-U", user, "-d", connection.Database!, "-v", "ON_ERROR_STOP=1" };
        foreach (var (name, value) in variables)
        {
            command.AddRange(["-v", $"{name}={value}"]);
        }

        command.AddRange(["-f", target]);
        var result = await _postgres.ExecAsync(command, cancellationToken);
        return (result.ExitCode ?? -1, result.Stdout, result.Stderr);
    }

    public HttpClient CreateClientFor(Guid userId, string? email = null)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Create(userId, email));
        return client;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Default", AppConnectionString);
        builder.UseSetting("Auth:Authority", TestTokens.Issuer);
        builder.UseSetting("Auth:Audience", TestTokens.Audience);
        // Background worker off in the shared host; job/outbox tests drive the worker explicitly (Platform/JobsTests).
        builder.UseSetting("Jobs:Enabled", "false");
        builder.ConfigureTestServices(services =>
            services.Configure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
                options.BackchannelHttpHandler = new TestJwksHandler()));
    }

    private static string WithUser(string connectionString, string user, string password) =>
        new NpgsqlConnectionStringBuilder(connectionString) { Username = user, Password = password }.ConnectionString;
}

internal static class RepoPaths
{
    public static string Root { get; } = Find();

    private static string Find()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "services", "api")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Repository root not found.");
    }
}
