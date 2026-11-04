using System.Text.Json.Nodes;
using BayiOS.Api.Platform.Tenancy;
using BayiOS.SharedKernel.Web;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace BayiOS.Api.Platform;

/// <summary>Adds the Bearer scheme, the required <c>X-Tenant-Id</c> header and <c>x-permission</c> to the contract.</summary>
internal static class OpenApiSecurity
{
    private const string SchemeName = "Bearer";

    public static OpenApiOptions AddBayiOsSecurity(this OpenApiOptions options)
    {
        options.AddDocumentTransformer((document, _, _) =>
        {
            document.Components ??= new OpenApiComponents();
            document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
            document.Components.SecuritySchemes[SchemeName] = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.Http,
                Scheme = "bearer",
                BearerFormat = "JWT",
                Description = "Supabase Auth access token",
            };
            return Task.CompletedTask;
        });

        options.AddOperationTransformer((operation, context, _) =>
        {
            var metadata = context.Description.ActionDescriptor.EndpointMetadata;

            if (metadata.OfType<IAuthorizeData>().Any())
            {
                operation.Security ??= [];
                operation.Security.Add(new OpenApiSecurityRequirement
                {
                    [new OpenApiSecuritySchemeReference(SchemeName, context.Document)] = [],
                });
            }

            if (metadata.OfType<TenantScopedMetadata>().Any())
            {
                operation.Parameters ??= [];
                operation.Parameters.Add(new OpenApiParameter
                {
                    Name = TenantContextMiddleware.TenantHeader,
                    In = ParameterLocation.Header,
                    Required = true,
                    Description = "Active tenant; must be one of the caller's active memberships.",
                    Schema = new OpenApiSchema { Type = JsonSchemaType.String, Format = "uuid" },
                });
            }

            if (metadata.OfType<PermissionMetadata>().FirstOrDefault() is { } permission)
            {
                operation.Extensions ??= new Dictionary<string, IOpenApiExtension>();
                operation.Extensions["x-permission"] = new JsonNodeExtension(JsonValue.Create(permission.Permission));
            }

            return Task.CompletedTask;
        });

        return options;
    }
}
