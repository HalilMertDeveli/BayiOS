using BayiOS.Api.Platform;
using BayiOS.Api.Platform.Auth;
using BayiOS.Api.Platform.Jobs;
using BayiOS.Api.Platform.Observability;
using BayiOS.Api.Platform.Persistence;
using BayiOS.Api.Platform.RateLimiting;
using BayiOS.Api.Platform.Tenancy;
using BayiOS.Modules.Billing;
using BayiOS.Modules.Catalog;
using BayiOS.Modules.Customers;
using BayiOS.Modules.Identity;
using BayiOS.Modules.Imports;
using BayiOS.Modules.Integrations;
using BayiOS.Modules.Inventory;
using BayiOS.Modules.Notifications;
using BayiOS.Modules.Orders;
using BayiOS.Modules.Pricing;
using BayiOS.Modules.Reports;
using BayiOS.Modules.Warehouse;

var builder = WebApplication.CreateBuilder(args);
// Before the observability error reporter: binding failures are client errors (coded 400), not crashes.
builder.Services.AddBadRequestHandling();
builder.AddBayiOsObservability();

builder.Services.AddOpenApi("v1", options => options.AddBayiOsSecurity().AddBayiOsContract());
builder.Services.AddProblemDetails(options =>
{
    options.CustomizeProblemDetails = context =>
    {
        context.ProblemDetails.Extensions["correlationId"] = context.HttpContext.TraceIdentifier;
        // Framework-generated client errors (e.g. minimal API's bare 415 for a wrong Content-Type, rendered by the
        // status-code pages) get the same stable `code` as ours (security audit 2026-11 L-4).
        if (!context.ProblemDetails.Extensions.ContainsKey("code") && BadRequestHandling.CodeFor(context.ProblemDetails.Status) is { } code)
        {
            context.ProblemDetails.Extensions["code"] = code;
            context.ProblemDetails.Type = BayiOS.SharedKernel.Web.ApiProblems.TypeBase + code;
        }
    };
});

var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(options =>
    options.AddDefaultPolicy(policy => policy
        .WithOrigins(allowedOrigins)
        .AllowAnyHeader()
        .AllowAnyMethod()
        .WithExposedHeaders(CorrelationId.HeaderName, "ETag", "Location", "Idempotent-Replayed", "Retry-After", "Content-Disposition")));

builder.Services.AddDatabase();
builder.Services.AddBayiOsAuthentication();
builder.Services.AddPermissionAuthorization();
builder.Services.AddBayiOsRateLimiting();
builder.Services.AddApiSecurityHeaders();
builder.Services.AddIdentityModule();
builder.Services.AddCatalogModule();
builder.Services.AddInventoryModule();
builder.Services.AddWarehouseModule();
builder.Services.AddInventoryPorts();
builder.Services.AddPlatformRuntime();
builder.Services.AddPricingModule();
builder.Services.AddCustomersModule();
builder.Services.AddPricingPorts();
builder.Services.AddImportsModule();
builder.Services.AddImportsPorts();
builder.Services.AddOrdersModule();
builder.Services.AddOrdersPorts();
builder.Services.AddFulfillmentPorts();
builder.Services.AddIntegrationsModule();
builder.Services.AddIntegrationsPorts();
builder.Services.AddReportsModule();
builder.Services.AddReportsPorts();
builder.Services.AddBillingModule();
builder.Services.AddBillingPorts();
builder.Services.AddNotificationsModule();

var app = builder.Build();

app.UseApiSecurityHeaders();
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseBayiOsRequestLogging();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseRouting();
app.UseCors();
app.UseAuthentication();
// Before TenantContextMiddleware opens the request transaction: a flood is rejected without a database round trip
// (threat-model §6.5; buckets per user + tenant header, plus a per-user cap over all tenant headers).
app.UseRateLimiter();
app.UseMiddleware<TenantContextMiddleware>();
app.UseAuthorization();

if (!app.Environment.IsProduction())
{
    app.MapOpenApi();
}

app.MapPlatformEndpoints();
app.MapReadiness();
app.MapIdentityModule();
app.MapCatalogModule();
app.MapInventoryModule();
app.MapWarehouseModule();
app.MapPlatformRuntime();
app.MapPricingModule();
app.MapCustomersModule();
app.MapImportsModule();
app.MapOrdersModule();
app.MapIntegrationsModule();
app.MapReportsModule();
app.MapBillingModule();
app.MapNotificationsModule();

app.Run();

/// <summary>Entry point marker for WebApplicationFactory in integration tests.</summary>
public partial class Program;
