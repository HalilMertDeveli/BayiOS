using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using BayiOS.Modules.Catalog.Domain;
using Npgsql;
using NpgsqlTypes;

namespace BayiOS.Modules.Catalog.Application;

/// <summary>
/// One <c>attr.&lt;key&gt;</c> query parameter (api-strategy §5.2). <see cref="Operator"/> is <c>eq</c>
/// (<c>attr.key=v</c>), <c>min</c> (<c>attr.key.min=v</c>) or <c>max</c> (<c>attr.key.max=v</c>).
/// </summary>
internal sealed record AttributeFilter(string Key, string Operator, string Value)
{
    public const string Prefix = "attr.";

    public static bool TryParse(string parameter, string value, out AttributeFilter filter)
    {
        filter = new AttributeFilter(string.Empty, "eq", value);
        if (!parameter.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var name = parameter[Prefix.Length..];
        var op = "eq";
        if (name.EndsWith(".min", StringComparison.Ordinal) || name.EndsWith(".max", StringComparison.Ordinal))
        {
            op = name[^3..];
            name = name[..^4];
        }

        filter = new AttributeFilter(name, op, value);
        return name.Length > 0;
    }
}

/// <summary>
/// Turns <c>attr.*</c> filters into one parameterised SQL predicate over <c>catalog.products</c>.
/// Equality filters (text, enum, multi_enum, boolean, integer, number, date) are merged into a single
/// <c>attributes @&gt; {...}</c> containment, served by the GIN <c>jsonb_path_ops</c> index. Numeric bounds
/// (<c>.min</c>/<c>.max</c> on number/integer) and the "value inside" test on range fields are
/// <c>jsonb_path_exists</c> predicates evaluated on the rows the other (indexed) conditions leave. Variant fields match
/// products that have a non-archived variant satisfying all variant conditions. Keys are validated against the
/// tenant's filterable definitions (regex-restricted); values are always bind parameters.
/// </summary>
internal static class AttributeFilterSql
{
    private sealed class Conditions
    {
        public JsonObject Containment { get; } = [];

        public List<(string Path, JsonObject Vars)> Paths { get; } = [];

        public bool IsEmpty => Containment.Count == 0 && Paths.Count == 0;
    }

    public static AppResult<(string Sql, NpgsqlParameter[] Parameters)> Build(FieldSet fields, IReadOnlyList<AttributeFilter> filters)
    {
        var product = new Conditions();
        var variant = new Conditions();
        foreach (var filter in filters)
        {
            var definition = fields.Find(filter.Key);
            if (definition is null || definition.ArchivedAt is not null || !definition.Filterable)
            {
                return AppError.Malformed($"attr.{filter.Key} is not a filterable custom field");
            }

            var target = definition.EntityType == CustomFieldEntityTypes.Variant ? variant : product;
            if (Add(target, definition, filter) is { } problem)
            {
                return AppError.Malformed($"attr.{filter.Key}{(filter.Operator == "eq" ? string.Empty : "." + filter.Operator)}: {problem}");
            }
        }

        var parameters = new List<NpgsqlParameter>();
        var sql = new StringBuilder("SELECT p.* FROM catalog.products AS p WHERE TRUE");
        AppendConditions(sql, parameters, "p", product);
        if (!variant.IsEmpty)
        {
            sql.Append(" AND EXISTS (SELECT 1 FROM catalog.product_variants AS v WHERE v.tenant_id = p.tenant_id AND v.product_id = p.id AND v.status <> 'archived'");
            AppendConditions(sql, parameters, "v", variant);
            sql.Append(')');
        }

        return (sql.ToString(), parameters.ToArray());
    }

    private static string? Add(Conditions target, CustomFieldDefinition definition, AttributeFilter filter)
    {
        var key = definition.Key;
        var value = filter.Value;
        var numeric = definition.DataType is CustomFieldDataTypes.Number or CustomFieldDataTypes.Integer;
        if (filter.Operator != "eq")
        {
            if (!numeric)
            {
                return "min/max is only supported on number and integer fields";
            }

            if (!TryNumber(value, out var bound))
            {
                return "must be a number";
            }

            var comparison = filter.Operator == "min" ? ">=" : "<=";
            target.Paths.Add(($"$.\"{key}\" ? (@ {comparison} $v)", new JsonObject { ["v"] = bound }));
            return null;
        }

        switch (definition.DataType)
        {
            case CustomFieldDataTypes.Text:
            case CustomFieldDataTypes.Enum:
                target.Containment[key] = value;
                return null;
            case CustomFieldDataTypes.Date:
                if (!DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                {
                    return "must be a date (yyyy-MM-dd)";
                }

                target.Containment[key] = value;
                return null;
            case CustomFieldDataTypes.MultiEnum:
                target.Containment[key] = new JsonArray(JsonValue.Create(value));
                return null;
            case CustomFieldDataTypes.Boolean:
                if (!bool.TryParse(value, out var flag))
                {
                    return "must be true or false";
                }

                target.Containment[key] = flag;
                return null;
            case CustomFieldDataTypes.Number:
            case CustomFieldDataTypes.Integer:
                if (!TryNumber(value, out var number))
                {
                    return "must be a number";
                }

                target.Containment[key] = number;
                return null;
            case CustomFieldDataTypes.Range:
                if (!TryNumber(value, out var point))
                {
                    return "must be a number";
                }

                target.Paths.Add(($"$.\"{key}\" ? (@.min <= $v && @.max >= $v)", new JsonObject { ["v"] = point }));
                return null;
            default:
                return "unsupported field type";
        }
    }

    private static void AppendConditions(StringBuilder sql, List<NpgsqlParameter> parameters, string alias, Conditions conditions)
    {
        if (conditions.Containment.Count > 0)
        {
            var name = "attr_c" + parameters.Count.ToString(CultureInfo.InvariantCulture);
            parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Jsonb) { Value = conditions.Containment.ToJsonString() });
            sql.Append(CultureInfo.InvariantCulture, $" AND {alias}.attributes @> @{name}");
        }

        foreach (var (path, vars) in conditions.Paths)
        {
            var pathName = "attr_p" + parameters.Count.ToString(CultureInfo.InvariantCulture);
            parameters.Add(new NpgsqlParameter(pathName, NpgsqlDbType.Text) { Value = path });
            var varsName = "attr_v" + parameters.Count.ToString(CultureInfo.InvariantCulture);
            parameters.Add(new NpgsqlParameter(varsName, NpgsqlDbType.Jsonb) { Value = vars.ToJsonString() });
            sql.Append(CultureInfo.InvariantCulture, $" AND jsonb_path_exists({alias}.attributes, CAST(@{pathName} AS jsonpath), @{varsName})");
        }
    }

    private static bool TryNumber(string value, out decimal number) =>
        decimal.TryParse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out number);
}
