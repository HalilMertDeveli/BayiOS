using System.Globalization;
using System.Text.Json;

namespace BayiOS.Modules.Integrations.Parsing;

/// <summary>
/// Server-side enforcement of order-parse.schema.json (#47). The model output is untrusted data: structured outputs
/// make it schema-shaped, but this validator checks it again — exact property sets (no extra field such as a price or an
/// "approve" flag is accepted), types, enums — plus the bounds the API cannot express (string lengths, quantity range,
/// confidence 0–1, list sizes). Any violation rejects the whole output; nothing is repaired or partially used.
/// </summary>
public static class ParsedOrderValidator
{
    public const int MaxLines = 100;
    public const int MaxRawText = 500;
    public const int MaxProductText = 200;
    public const int MaxProductCode = 64;
    public const int MaxBrandModel = 100;
    public const int MaxNotes = 300;
    public const int MaxFragments = 50;
    public const int MaxFragment = 500;
    public const int MaxWarnings = 20;

    /// <summary>numeric(18,3) order quantities; anything larger is not a plausible WhatsApp order line.</summary>
    public const decimal MaxQuantity = 1_000_000m;

    private static readonly string[] RootProperties = ["lines", "unparsedFragments", "warnings"];
    private static readonly string[] LineProperties = ["rawText", "productCode", "productText", "quantity", "unit", "brandModel", "confidence", "notes"];

    private static readonly Lazy<string> SchemaText = new(() => ReadResource("BayiOS.Integrations.order-parse.schema.json"));

    /// <summary>The schema sent as <c>output_config.format.schema</c> (embedded resource, byte-stable for caching).</summary>
    public static string Schema => SchemaText.Value;

    /// <returns>The parse, or null with the first violation in <paramref name="error"/>.</returns>
    public static ParsedOrder? TryParse(string? json, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            error = "empty output";
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
            return Read(document.RootElement, out error);
        }
        catch (JsonException)
        {
            error = "output is not valid JSON";
            return null;
        }
    }

    private static ParsedOrder? Read(JsonElement root, out string? error)
    {
        if (!ExactObject(root, RootProperties, "$", out error))
        {
            return null;
        }

        var linesElement = root.GetProperty("lines");
        if (linesElement.ValueKind != JsonValueKind.Array || linesElement.GetArrayLength() > MaxLines)
        {
            error = $"$.lines must be an array of at most {MaxLines}";
            return null;
        }

        var lines = new List<ParsedLine>();
        var index = 0;
        foreach (var item in linesElement.EnumerateArray())
        {
            var line = ReadLine(item, $"$.lines[{index++}]", out error);
            if (line is null)
            {
                return null;
            }

            lines.Add(line);
        }

        var fragments = StringArray(root.GetProperty("unparsedFragments"), "$.unparsedFragments", MaxFragments, MaxFragment, null, out error);
        if (fragments is null)
        {
            return null;
        }

        var warnings = StringArray(root.GetProperty("warnings"), "$.warnings", MaxWarnings, 64, ParseWarnings.ParserCodes, out error);
        return warnings is null ? null : new ParsedOrder(lines, fragments, [.. warnings.Distinct(StringComparer.Ordinal)]);
    }

    private static ParsedLine? ReadLine(JsonElement item, string path, out string? error)
    {
        if (!ExactObject(item, LineProperties, path, out error))
        {
            return null;
        }

        if (!RequiredString(item, "rawText", path, MaxRawText, out var rawText, out error)
            || !RequiredString(item, "productText", path, MaxProductText, out var productText, out error)
            || !OptionalString(item, "productCode", path, MaxProductCode, out var productCode, out error)
            || !OptionalString(item, "brandModel", path, MaxBrandModel, out var brandModel, out error)
            || !OptionalString(item, "notes", path, MaxNotes, out var notes, out error))
        {
            return null;
        }

        decimal? quantity = null;
        var quantityElement = item.GetProperty("quantity");
        if (quantityElement.ValueKind == JsonValueKind.Number)
        {
            if (!quantityElement.TryGetDecimal(out var value) || value <= 0 || value > MaxQuantity || decimal.Round(value, 3) != value)
            {
                error = $"{path}.quantity must be > 0, ≤ {MaxQuantity.ToString(CultureInfo.InvariantCulture)} with at most 3 decimals";
                return null;
            }

            quantity = value;
        }
        else if (quantityElement.ValueKind != JsonValueKind.Null)
        {
            error = $"{path}.quantity must be a number or null";
            return null;
        }

        string? unit = null;
        var unitElement = item.GetProperty("unit");
        if (unitElement.ValueKind == JsonValueKind.String)
        {
            unit = unitElement.GetString();
            if (unit is null || !ParseUnits.All.Contains(unit, StringComparer.Ordinal))
            {
                error = $"{path}.unit is not an allowed unit";
                return null;
            }
        }
        else if (unitElement.ValueKind != JsonValueKind.Null)
        {
            error = $"{path}.unit must be a string or null";
            return null;
        }

        var confidenceElement = item.GetProperty("confidence");
        if (confidenceElement.ValueKind != JsonValueKind.Number || !confidenceElement.TryGetDecimal(out var confidence) || confidence < 0 || confidence > 1)
        {
            error = $"{path}.confidence must be a number between 0 and 1";
            return null;
        }

        return new ParsedLine(rawText!, productCode, productText!, quantity, unit, brandModel, decimal.Round(confidence, 3), notes);
    }

    private static bool ExactObject(JsonElement element, string[] properties, string path, out string? error)
    {
        error = null;
        if (element.ValueKind != JsonValueKind.Object)
        {
            error = $"{path} must be an object";
            return false;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!properties.Contains(property.Name, StringComparer.Ordinal))
            {
                error = $"{path} has an unexpected property '{Truncate(property.Name)}'";
                return false;
            }

            if (!seen.Add(property.Name))
            {
                error = $"{path} repeats property '{property.Name}'";
                return false;
            }
        }

        var missing = properties.FirstOrDefault(p => !seen.Contains(p));
        if (missing is not null)
        {
            error = $"{path} is missing '{missing}'";
            return false;
        }

        return true;
    }

    private static bool RequiredString(JsonElement item, string name, string path, int maxLength, out string? value, out string? error)
    {
        error = null;
        value = null;
        var element = item.GetProperty(name);
        if (element.ValueKind != JsonValueKind.String || Clean(element.GetString()) is not { } text || text.Length > maxLength)
        {
            error = $"{path}.{name} must be a non-empty string of at most {maxLength} characters";
            return false;
        }

        value = text;
        return true;
    }

    private static bool OptionalString(JsonElement item, string name, string path, int maxLength, out string? value, out string? error)
    {
        error = null;
        value = null;
        var element = item.GetProperty(name);
        if (element.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            error = $"{path}.{name} must be a string or null";
            return false;
        }

        var text = Clean(element.GetString());
        if (text is { Length: > 0 } && text.Length > maxLength)
        {
            error = $"{path}.{name} must be at most {maxLength} characters";
            return false;
        }

        value = text;
        return true;
    }

    private static List<string>? StringArray(
        JsonElement element, string path, int maxItems, int maxLength, IReadOnlyList<string>? allowed, out string? error)
    {
        error = null;
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() > maxItems)
        {
            error = $"{path} must be an array of at most {maxItems}";
            return null;
        }

        var values = new List<string>();
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || Clean(item.GetString()) is not { } text || text.Length > maxLength)
            {
                error = $"{path} items must be non-empty strings of at most {maxLength} characters";
                return null;
            }

            if (allowed is not null && !allowed.Contains(text, StringComparer.Ordinal))
            {
                error = $"{path} contains an unknown code '{Truncate(text)}'";
                return null;
            }

            values.Add(text);
        }

        return values;
    }

    /// <summary>Trimmed text without control characters (line breaks become spaces); null when empty.</summary>
    internal static string? Clean(string? value)
    {
        if (value is null)
        {
            return null;
        }

        var cleaned = new string([.. value.Select(c => char.IsControl(c) ? ' ' : c)]).Trim();
        return cleaned.Length == 0 ? null : cleaned;
    }

    private static string Truncate(string value) => value.Length > 40 ? value[..40] + "…" : value;

    private static string ReadResource(string name)
    {
        using var stream = typeof(ParsedOrderValidator).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded resource {name} is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
