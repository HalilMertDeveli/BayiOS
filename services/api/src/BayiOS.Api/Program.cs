using BayiOS.Api.Platform;
using BayiOS.Api.Platform.Auth;
using BayiOS.Api.Platform.Persistence;
using BayiOS.Api.Platform.Tenancy;
using BayiOS.Modules.Catalog;
using BayiOS.Modules.Identity;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi("v1", options => options.AddBayiOsSecurity());
builder.Services.AddProblemDetails(options =>
{
    options.CustomizeProblemDetails = context =>
        context.ProblemDetails.Extensions["correlationId"] = context.HttpContext.TraceIdentifier;
});

var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(options =>
    options.AddDefaultPolicy(policy => policy
        .WithOrigins(allowedOrigins)
        .AllowAnyHeader()
        .AllowAnyMethod()
        .WithExposedHeaders(CorrelationId.HeaderName, "ETag", "Location")));

builder.Services.AddDatabase();
builder.Services.AddBayiOsAuthentication();
builder.Services.AddPermissionAuthorization();
builder.Services.AddIdentityModule();
builder.Services.AddCatalogModule();

var app = builder.Build();

app.UseMiddleware<CorrelationIdMiddleware>();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseRouting();
app.UseCors();
app.UseAuthentication();
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

app.Run();

/// <summary>Entry point marker for WebApplicationFactory in integration tests.</summary>
public partial class Program;
