using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using BayiOS.IntegrationTests.Catalog;
using BayiOS.IntegrationTests.Infrastructure;
using ExcelDataReader;
using Npgsql;

namespace BayiOS.IntegrationTests.Reports;

/// <summary>Report calls, superuser truth queries and export parsing shared by the report tests. Synthetic data only.</summary>
internal static class ReportsKit
{
    public static readonly TimeSpan Istanbul = TimeSpan.FromHours(3);

    // ExcelDataReader's configuration asks for code page 1252 even for xlsx.
    static ReportsKit() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Today in Europe/Istanbul.</summary>
    public static DateOnly Today => DateOnly.FromDateTime(DateTimeOffset.UtcNow.ToOffset(Istanbul).DateTime);

    public static string Day(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>A range around today that survives a test running across midnight.</summary>
    public static string Range => $"from={Day(Today.AddDays(-2))}&to={Day(Today.AddDays(1))}";

    public static async Task<T> OkAsync<T>(CatalogClient client, string path)
    {
        var response = await client.GetAsync(path);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"GET {path}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(Ct)}");
        return (await response.Content.ReadFromJsonAsync<T>(Ct))!;
    }

    /// <summary>A priced variant in a fraction-allowing unit (kg; once per tenant), so quantities like 7.25 are accepted.</summary>
    public static async Task<Guid> KilogramVariantAsync(Orders.OrdersKit kit, string sku, decimal price)
    {
        var unit = await kit.Owner.CreatedAsync<BayiOS.Modules.Catalog.Contracts.UnitResponse>(
            "/api/v1/units", new { code = "kg", name = "Kilogram", allowsFraction = true });
        var product = await kit.Owner.CreateSimpleProductAsync(unit.Id, "Sentetik dökme ürün " + sku, sku);
        var variant = Assert.Single(product.Variants).Id;
        await kit.PriceAsync(kit.PriceListId, variant, price);
        return variant;
    }

    public static Task<HttpResponseMessage> ExportAsync(CatalogClient client, string report, object? body = null, string? key = null) =>
        client.SendAsync(HttpMethod.Post, $"/api/v1/reports/{report}/export", body ?? new { }, idempotencyKey: key ?? Guid.NewGuid().ToString());

    public static async Task<byte[]> ExportOkAsync(CatalogClient client, string report, object? body = null)
    {
        var response = await ExportAsync(client, report, body);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"export {report}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(Ct)}");
        return await response.Content.ReadAsByteArrayAsync(Ct);
    }

    /// <summary>Rows of a <c>;</c>-delimited CSV (RFC 4180 quotes), after checking the UTF-8 BOM.</summary>
    public static List<string[]> ParseCsv(byte[] bytes)
    {
        Assert.True(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "CSV must start with the UTF-8 BOM");
        var text = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        var rows = new List<string[]>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    field.Append(c);
                }
            }
            else if (c == '"')
            {
                quoted = true;
            }
            else if (c == ';')
            {
                row.Add(field.ToString());
                field.Clear();
            }
            else if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
            {
                row.Add(field.ToString());
                field.Clear();
                rows.Add([.. row]);
                row.Clear();
                i++;
            }
            else
            {
                field.Append(c);
            }
        }

        Assert.True(field.Length == 0 && row.Count == 0, "CSV must end with CRLF");
        return rows;
    }

    /// <summary>The first sheet of an XLSX as cell values (ExcelDataReader, the reader the imports use).</summary>
    public static List<object?[]> ParseXlsx(byte[] bytes)
    {
        Assert.True(bytes.Length > 4 && bytes[0] == 0x50 && bytes[1] == 0x4B && bytes[2] == 0x03 && bytes[3] == 0x04, "XLSX must be a ZIP (PK\\x03\\x04)");
        using var stream = new MemoryStream(bytes);
        using var reader = ExcelReaderFactory.CreateOpenXmlReader(stream);
        var rows = new List<object?[]>();
        while (reader.Read())
        {
            var cells = new object?[reader.FieldCount];
            for (var i = 0; i < reader.FieldCount; i++)
            {
                cells[i] = reader.GetValue(i);
            }

            rows.Add(cells);
        }

        return rows;
    }

    public static async Task<List<object?[]>> RowsAsync(ApiFixture fixture, string sql, params object[] parameters)
    {
        await using var connection = new NpgsqlConnection(fixture.SuperuserConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var parameter in parameters)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = parameter });
        }

        await using var reader = await command.ExecuteReaderAsync(Ct);
        var rows = new List<object?[]>();
        while (await reader.ReadAsync(Ct))
        {
            var row = new object?[reader.FieldCount];
            for (var i = 0; i < reader.FieldCount; i++)
            {
                row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }

            rows.Add(row);
        }

        return rows;
    }

    public static async Task<int> ExecuteAsync(ApiFixture fixture, string sql, params object[] parameters)
    {
        await using var connection = new NpgsqlConnection(fixture.SuperuserConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var parameter in parameters)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = parameter });
        }

        return await command.ExecuteNonQueryAsync(Ct);
    }

    /// <summary>The Istanbul day of an instant read from PostgreSQL.</summary>
    public static DateOnly DayOf(object instant) => instant switch
    {
        DateTime utc => DateOnly.FromDateTime(new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToOffset(Istanbul).DateTime),
        DateTimeOffset value => DateOnly.FromDateTime(value.ToOffset(Istanbul).DateTime),
        _ => throw new ArgumentException(instant.GetType().Name),
    };
}
