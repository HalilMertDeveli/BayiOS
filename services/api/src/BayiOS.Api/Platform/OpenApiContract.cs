using System.Text.Json.Nodes;
using BayiOS.Api.Platform.Tenancy;
using BayiOS.Modules.Catalog.Contracts;
using BayiOS.Modules.Catalog.Domain;
using BayiOS.Modules.Billing.Contracts;
using BayiOS.Modules.Identity.Contracts;
using BayiOS.SharedKernel.Entitlements;
using BayiOS.SharedKernel.Web;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace BayiOS.Api.Platform;

/// <summary>
/// Contract details the endpoint metadata cannot express on its own (#86): string enums, problem extensions
/// (<c>shortages</c>, <c>confirmationRequired</c>, <c>conflict</c>), <c>Retry-After</c> on transient 409s, the optional
/// <c>X-Tenant-Id</c> of <c>GET /me</c> and the dynamic <c>attr.*</c> product filters.
/// </summary>
internal static class OpenApiContract
{
    private const string ProblemJson = "application/problem+json";
    private const string StockProblem = "StockProblemDetails";
    private const string ConflictProblem = "CodeConflictProblemDetails";
    private const string EntitlementProblem = "EntitlementProblemDetails";
    private static readonly string[] StockStatuses = ["409", "422"];

    /// <summary>String properties that only take a fixed set of values: (type, JSON property) → values.</summary>
    private static readonly Dictionary<Type, Dictionary<string, IReadOnlyList<string>>> Enums = new()
    {
        [typeof(CustomFieldResponse)] = new()
        {
            ["entityType"] = CustomFieldEntityTypes.All,
            ["dataType"] = CustomFieldDataTypes.All,
        },
        [typeof(PermissionGrantResponse)] = new() { ["scope"] = ["all", "conditional", "own"] },
        [typeof(TenantEntitlementsResponse)] = new() { ["status"] = BayiOS.Modules.Billing.Domain.SubscriptionStatuses.All },
        [typeof(SubscriptionResponse)] = new() { ["status"] = BayiOS.Modules.Billing.Domain.SubscriptionStatuses.All },
        [typeof(UsageResponse)] = new() { ["status"] = BayiOS.Modules.Billing.Domain.SubscriptionStatuses.All },
        [typeof(TenantModuleResponse)] = new()
        {
            ["status"] =
            [
                BayiOS.Modules.Billing.Application.BillingService.StatusIncludedInTrial,
                .. BayiOS.Modules.Billing.Domain.ModuleStatuses.All,
                BayiOS.Modules.Billing.Application.BillingService.StatusAvailable,
                BayiOS.Modules.Billing.Application.BillingService.StatusUnavailable,
            ],
        },
    };

    public static OpenApiOptions AddBayiOsContract(this OpenApiOptions options)
    {
        options.AddSchemaTransformer((schema, context, _) =>
        {
            if (Enums.TryGetValue(context.JsonTypeInfo.Type, out var properties) && schema.Properties is { } members)
            {
                foreach (var (name, values) in properties)
                {
                    if (members.TryGetValue(name, out var member) && member is OpenApiSchema property)
                    {
                        property.Enum = [.. values.Select(v => (JsonNode)JsonValue.Create(v))];
                    }
                }
            }

            return Task.CompletedTask;
        });

        options.AddDocumentTransformer((document, _, _) =>
        {
            document.Components ??= new OpenApiComponents();
            document.Components.Schemas ??= new Dictionary<string, IOpenApiSchema>();
            var schemas = document.Components.Schemas;
            schemas["StockShortage"] = Object(
                ("variantId", Uuid()),
                ("sku", String()),
                ("warehouseId", Uuid()),
                ("locationId", Nullable(Uuid())),
                ("requested", Number()),
                ("available", Number()));
            schemas[StockProblem] = Problem(document,
                "Stock problem: shortages per line (insufficient stock) and confirmationRequired (negative-stock policy 'warn' — retry with the confirmation flag).",
                ("code", String()),
                ("shortages", new OpenApiSchema { Type = JsonSchemaType.Array, Items = new OpenApiSchemaReference("StockShortage", document) }),
                ("confirmationRequired", new OpenApiSchema { Type = JsonSchemaType.Boolean }));
            var field = String();
            field.Enum = [JsonValue.Create("sku"), JsonValue.Create("barcode")];
            schemas["CodeConflict"] = Object(
                ("field", field),
                ("value", String()),
                ("variantId", Uuid()),
                ("productId", Uuid()),
                ("sku", String()));
            schemas[ConflictProblem] = Problem(document,
                "409 catalog.sku_taken / catalog.barcode_taken: conflict names the existing variant holding the value (absent when only the database index caught a concurrent write).",
                ("code", String()),
                ("conflict", new OpenApiSchemaReference("CodeConflict", document)));
            schemas[EntitlementProblem] = Problem(document,
                "403: auth.forbidden / auth.disabled_by_tenant_rule (permission), or entitlement.required (feature names the missing "
                + "feature; suggestedPlan / suggestedModule what would grant it, null when nothing in the catalog does) and "
                + "entitlement.limit_exceeded (feature names the limit; limit and current are the numbers). Existing data stays readable.",
                ("code", String()),
                ("feature", String()),
                ("suggestedPlan", Nullable(String())),
                ("suggestedModule", Nullable(String())),
                ("limit", Nullable(Integer())),
                ("current", Nullable(Integer())));
            return Task.CompletedTask;
        });

        options.AddOperationTransformer((operation, context, _) =>
        {
            var metadata = context.Description.ActionDescriptor.EndpointMetadata;

            if (metadata.OfType<TenantOptionalMetadata>().Any())
            {
                operation.Parameters ??= [];
                operation.Parameters.Add(new OpenApiParameter
                {
                    Name = TenantContextMiddleware.TenantHeader,
                    In = ParameterLocation.Header,
                    Required = false,
                    Description = "Optional active tenant. When it names one of the caller's active memberships in an active tenant, "
                        + "the response includes that tenant's effective permissions; otherwise it is ignored.",
                    Schema = new OpenApiSchema { Type = JsonSchemaType.String, Format = "uuid" },
                });
            }

            if (metadata.OfType<StockProblemMetadata>().Any())
            {
                foreach (var status in StockStatuses)
                {
                    SetProblemSchema(operation, status, new OpenApiSchemaReference(StockProblem, context.Document));
                }
            }

            if (metadata.OfType<EntitlementMetadata>().Any() || metadata.OfType<EntitlementLimitMetadata>().Any())
            {
                SetProblemSchema(operation, "403", new OpenApiSchemaReference(EntitlementProblem, context.Document));
            }

            if (metadata.OfType<ConflictDetailMetadata>().Any())
            {
                SetProblemSchema(operation, "409", new OpenApiSchemaReference(ConflictProblem, context.Document));
            }

            if (metadata.OfType<RetryableConflictMetadata>().FirstOrDefault() is { } retryable
                && operation.Responses?.TryGetValue("409", out var conflict) == true && conflict is OpenApiResponse response)
            {
                response.Headers ??= new Dictionary<string, IOpenApiHeader>();
                response.Headers["Retry-After"] = new OpenApiHeader
                {
                    Description = $"Seconds to wait before retrying; sent with the transient lock conflicts {string.Join(", ", retryable.Codes)}.",
                    Schema = new OpenApiSchema { Type = JsonSchemaType.Integer, Minimum = "1" },
                };
                response.Description = $"Conflict. Transient lock conflicts ({string.Join(", ", retryable.Codes)}) come with Retry-After: retry the same request (same Idempotency-Key).";
            }

            if (metadata.OfType<QueryPatternMetadata>().ToList() is { Count: > 0 } patterns)
            {
                operation.Extensions ??= new Dictionary<string, IOpenApiExtension>();
                var array = new JsonArray();
                foreach (var pattern in patterns)
                {
                    array.Add(new JsonObject { ["pattern"] = pattern.Pattern, ["description"] = pattern.Description });
                }

                operation.Extensions["x-query-parameter-patterns"] = new JsonNodeExtension(array);
                operation.Description = string.Join(
                    "\n",
                    [operation.Description, .. patterns.Select(p => $"Query `{p.Pattern}`: {p.Description}")]).Trim();
            }

            return Task.CompletedTask;
        });

        return options;
    }

    private static void SetProblemSchema(OpenApiOperation operation, string status, IOpenApiSchema schema)
    {
        if (operation.Responses?.TryGetValue(status, out var found) == true && found is OpenApiResponse response
            && response.Content?.TryGetValue(ProblemJson, out var media) == true && media is OpenApiMediaType type)
        {
            type.Schema = schema;
        }
    }

    private static OpenApiSchema Problem(OpenApiDocument document, string description, params (string Name, IOpenApiSchema Schema)[] extensions) => new()
    {
        Description = description,
        AllOf = [new OpenApiSchemaReference("ProblemDetails", document), Object(extensions)],
    };

    private static OpenApiSchema Object(params (string Name, IOpenApiSchema Schema)[] properties) => new()
    {
        Type = JsonSchemaType.Object,
        Properties = properties.ToDictionary(p => p.Name, p => p.Schema),
    };

    private static OpenApiSchema String() => new() { Type = JsonSchemaType.String };

    private static OpenApiSchema Uuid() => new() { Type = JsonSchemaType.String, Format = "uuid" };

    private static OpenApiSchema Number() => new() { Type = JsonSchemaType.Number };

    private static OpenApiSchema Integer() => new() { Type = JsonSchemaType.Integer, Format = "int64" };

    private static OpenApiSchema Nullable(OpenApiSchema schema)
    {
        schema.Type |= JsonSchemaType.Null;
        return schema;
    }
}
