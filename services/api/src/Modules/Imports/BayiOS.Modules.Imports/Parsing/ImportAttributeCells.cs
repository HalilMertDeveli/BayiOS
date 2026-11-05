using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace BayiOS.Modules.Imports.Parsing;

/// <summary>
/// Reads a spreadsheet cell as the JSON shape of a custom field's data type, before Catalog's custom-field validator
/// checks and normalises it (options, length, integer, min ≤ max). Only the cell syntax is decided here:
/// numbers in Turkish or invariant notation, yes/no words for booleans, <c>gg.aa.yyyy</c> or ISO dates, multi-select
/// values separated by <c>;</c> <c>,</c> or <c>|</c>, ranges as <c>min-max</c> (also <c>–</c> or <c>..</c>).
/// A cell that cannot be read is passed on as text, so the validator's message names the expected shape.
/// </summary>
internal static partial class ImportAttributeCells
{
    private static readonly string[] DateFormats = ["yyyy-MM-dd", "dd.MM.yyyy", "d.M.yyyy", "dd/MM/yyyy", "d/M/yyyy"];

    public static JsonNode? ToJson(string dataType, string cell)
    {
        var text = cell.Trim();
        switch (dataType)
        {
            case "number":
            case "integer":
                return ImportValues.TryParseDecimal(text, out var number) ? JsonValue.Create(number) : JsonValue.Create(text);

            case "boolean":
                return ImportValues.Fold(text) switch
                {
                    "evet" or "e" or "var" or "true" or "1" or "yes" or "y" or "x" => JsonValue.Create(true),
                    "hayir" or "h" or "yok" or "false" or "0" or "no" or "n" => JsonValue.Create(false),
                    _ => JsonValue.Create(text),
                };

            case "date":
                return DateOnly.TryParseExact(text, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                    ? JsonValue.Create(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
                    : JsonValue.Create(text);

            case "multi_enum":
                return new JsonArray([.. text.Split([';', ',', '|'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                    .Select(v => (JsonNode?)JsonValue.Create(v))]);

            case "range":
                var match = Range().Match(text);
                return match.Success
                    && ImportValues.TryParseDecimal(match.Groups["min"].Value, out var min)
                    && ImportValues.TryParseDecimal(match.Groups["max"].Value, out var max)
                        ? new JsonObject { ["min"] = min, ["max"] = max }
                        : JsonValue.Create(text);

            default:
                return JsonValue.Create(cell);
        }
    }

    [GeneratedRegex(@"^(?<min>-?[0-9][0-9.,]*)\s*(?:-|–|—|\.\.)\s*(?<max>-?[0-9][0-9.,]*)$", RegexOptions.CultureInvariant)]
    private static partial Regex Range();
}
