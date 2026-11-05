using System.Text.Json.Nodes;
using BayiOS.Api.Platform.Tenancy;
using BayiOS.SharedKernel.Platform.Idempotency;
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

            if (metadata.OfType<IdempotencyMetadata>().Any())
            {
                operation.Parameters ??= [];
                operation.Parameters.Add(new OpenApiParameter
                {
                    Name = IdempotencyMetadata.HeaderName,
                    In = ParameterLocation.Header,
                    Required = true,
                    Description = "Client-generated key (UUID recommended). Retries with the same key and body replay the "
                        + "stored success response with Idempotent-Replayed: true for 24 hours.",
                    Schema = new OpenApiSchema { Type = JsonSchemaType.String, MinLength = 1, MaxLength = IdempotencyMetadata.MaxKeyLength },
                });
            }

            if (metadata.OfType<PermissionMetadata>().FirstOrDefault() is { } permission)
            {
                operation.Extensions ??= new Dictionary<string, IOpenApiExtension>();
                operation.Extensions["x-permission"] = new JsonNodeExtension(JsonValue.Create(permission.Permission));
                if (permission.AnyOf.Count > 1)
                {
                    // Any one of these suffices (x-permission is the first, kept for single-permission consumers).
                    operation.Extensions["x-permission-any-of"] = new JsonNodeExtension(new JsonArray([.. permission.AnyOf.Select(p => (JsonNode)JsonValue.Create(p))]));
                }
            }

            return Task.CompletedTask;
        });

        return options;
    }
}
