using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using BayiOS.Modules.Catalog.Domain;
using BayiOS.Modules.Catalog.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BayiOS.Modules.Catalog.Application;

/// <summary>The tenant's custom field definitions (archived included), loaded once per request.</summary>
internal sealed class FieldSet
{
    private readonly Dictionary<string, CustomFieldDefinition> _byKey;

    private FieldSet(IEnumerable<CustomFieldDefinition> definitions) =>
        _byKey = definitions.ToDictionary(d => d.Key, StringComparer.Ordinal);

    public static async Task<FieldSet> LoadAsync(CatalogDbContext db, CancellationToken cancellationToken) =>
        new(await db.CustomFields.AsNoTracking().ToListAsync(cancellationToken));

    public CustomFieldDefinition? Find(string key) => _byKey.GetValueOrDefault(key);

    public IEnumerable<CustomFieldDefinition> RequiredFor(string entityType) =>
        _byKey.Values.Where(d => d.EntityType == entityType && d.Required && d.ArchivedAt is null);

    public static IReadOnlyList<string> ReadOptions(string json) => JsonSerializer.Deserialize<List<string>>(json) ?? [];
}

/// <summary>
/// Validates <c>attributes</c> against the tenant's custom field definitions (data-model §4 <c>CustomFieldValidator</c>):
/// unknown or archived keys, keys of the other record type, wrong value shapes, values outside the options and
/// missing required fields are reported per field as <c>{prefix}.{key}</c>. Returns the normalised JSON.
/// </summary>
internal static class CustomFieldValidator
{
    public const int MaxTextLength = 500;

    /// <summary>A complete attribute object (create). <c>null</c> members are treated as absent.</summary>
    public static string ValidateNew(
        FieldSet fields,
        string entityType,
        IReadOnlyDictionary<string, JsonElement>? values,
        string prefix,
        Dictionary<string, string[]> errors)
    {
        var result = new JsonObject();
        foreach (var (key, element) in values ?? new Dictionary<string, JsonElement>())
        {
            if (element.ValueKind == JsonValueKind.Null)
            {
                continue;
            }

            if (Validate(fields, entityType, key, JsonNode.Parse(element.GetRawText()), prefix, errors) is { } normalized)
            {
                result[key] = normalized;
            }
        }

        CheckRequired(fields, entityType, result, prefix, errors);
        return result.ToJsonString();
    }

    /// <summary>
    /// RFC 7396 merge of <paramref name="patch"/> into the stored object: a value sets the field, <c>null</c> removes
    /// it. Only patched keys are type-checked (stored values of later-archived fields are kept); required fields are
    /// checked on the merged result.
    /// </summary>
    public static string Merge(
        FieldSet fields,
        string entityType,
        string currentJson,
        JsonObject patch,
        string prefix,
        Dictionary<string, string[]> errors)
    {
        var result = JsonNode.Parse(currentJson) as JsonObject ?? [];
        foreach (var (key, node) in patch)
        {
            if (node is null)
            {
                result.Remove(key);
                continue;
            }

            if (Validate(fields, entityType, key, node, prefix, errors) is { } normalized)
            {
                result[key] = normalized;
            }
        }

        CheckRequired(fields, entityType, result, prefix, errors);
        return result.ToJsonString();
    }

    private static void CheckRequired(FieldSet fields, string entityType, JsonObject values, string prefix, Dictionary<string, string[]> errors)
    {
        foreach (var definition in fields.RequiredFor(entityType))
        {
            if (!values.ContainsKey(definition.Key))
            {
                errors.TryAdd($"{prefix}.{definition.Key}", ["Required."]);
            }
        }
    }

    private static JsonNode? Validate(FieldSet fields, string entityType, string key, JsonNode? node, string prefix, Dictionary<string, string[]> errors)
    {
        var errorKey = $"{prefix}.{key}";
        var definition = fields.Find(key);
        if (definition is null || definition.EntityType != entityType)
        {
            errors[errorKey] = [$"Unknown custom field for a {entityType}."];
            return null;
        }

        if (definition.ArchivedAt is not null)
        {
            errors[errorKey] = ["Custom field is archived."];
            return null;
        }

        var normalized = Normalize(definition, node, out var error);
        if (error is not null)
        {
            errors[errorKey] = [error];
        }

        return normalized;
    }

    /// <summary>Returns the stored form of a value, or null with an error message.</summary>
    public static JsonNode? Normalize(CustomFieldDefinition definition, JsonNode? node, out string? error)
    {
        error = null;
        switch (definition.DataType)
        {
            case CustomFieldDataTypes.Text:
                if (AsString(node) is { } text && CatalogText.Clean(text) is { Length: <= MaxTextLength } clean)
                {
                    return JsonValue.Create(clean);
                }

                error = $"Must be a string of 1–{MaxTextLength} characters.";
                return null;

            case CustomFieldDataTypes.Date:
                if (AsString(node) is { } date && DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                {
                    return JsonValue.Create(date);
                }

                error = "Must be a date (yyyy-MM-dd).";
                return null;

            case CustomFieldDataTypes.Boolean:
                if (node is JsonValue b && b.GetValueKind() is JsonValueKind.True or JsonValueKind.False)
                {
                    return JsonValue.Create(b.GetValue<bool>());
                }

                error = "Must be true or false.";
                return null;

            case CustomFieldDataTypes.Number:
                if (AsDecimal(node) is { } number)
                {
                    return JsonValue.Create(number);
                }

                error = "Must be a number.";
                return null;

            case CustomFieldDataTypes.Integer:
                if (AsDecimal(node) is { } integer && decimal.Truncate(integer) == integer && integer is >= long.MinValue and <= long.MaxValue)
                {
                    return JsonValue.Create((long)integer);
                }

                error = "Must be an integer.";
                return null;

            case CustomFieldDataTypes.Enum:
                var options = FieldSet.ReadOptions(definition.Options);
                if (AsString(node) is { } choice && options.Contains(choice, StringComparer.Ordinal))
                {
                    return JsonValue.Create(choice);
                }

                error = $"Must be one of: {string.Join(", ", options)}.";
                return null;

            case CustomFieldDataTypes.MultiEnum:
                var allowed = FieldSet.ReadOptions(definition.Options);
                if (node is JsonArray array && array.Count > 0)
                {
                    var chosen = array.Select(AsString).ToList();
                    if (chosen.All(c => c is not null && allowed.Contains(c, StringComparer.Ordinal))
                        && chosen.Distinct(StringComparer.Ordinal).Count() == chosen.Count)
                    {
                        return new JsonArray(chosen.Select(c => (JsonNode?)JsonValue.Create(c)).ToArray());
                    }
                }

                error = $"Must be a non-empty array of distinct values from: {string.Join(", ", allowed)}.";
                return null;

            case CustomFieldDataTypes.Range:
                if (node is JsonObject range && range.Count == 2
                    && AsDecimal(range["min"]) is { } min && AsDecimal(range["max"]) is { } max && min <= max)
                {
                    return new JsonObject { ["min"] = min, ["max"] = max };
                }

                error = "Must be an object {\"min\": number, \"max\": number} with min ≤ max.";
                return null;

            default:
                error = "Unsupported field type.";
                return null;
        }
    }

    private static string? AsString(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;

    private static decimal? AsDecimal(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.Number && value.TryGetValue<decimal>(out var number) ? number : null;
}
