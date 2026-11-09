using System.Diagnostics;
using System.Net;
using System.Text.Json;
using BayiOS.IntegrationTests.Catalog;
using BayiOS.IntegrationTests.Infrastructure;
using Npgsql;

namespace BayiOS.IntegrationTests.Performance;

/// <summary>
/// Synthetic catalog generator (#20, test-strategy §3 "10k katalog fixture"): a tenant with a motorcycle-parts and an
/// LED category tree, brands and filterable custom fields created through the API, then <see cref="ProductCount"/>
/// products (half motor parts with one variant each, half LED items — every fourth in three colours) bulk-inserted
/// in one statement. Fully deterministic except the row ids; no real products, brands or prices.
/// <para>
/// The bulk insert runs as <c>bayios_app</c> inside the tenant's RLS context, not as the migrator: the migrator owns
/// the tables, but FORCE ROW LEVEL SECURITY with policies only for <c>bayios_app</c> denies it every row by design.
/// </para>
/// </summary>
internal sealed class SyntheticCatalog
{
    public const int ProductCount = 10_000;

    public static readonly string[] MotorParts =
        ["Fren Balatası", "Debriyaj Seti", "Zincir Dişli Seti", "Hava Filtresi", "Yağ Filtresi", "Buji", "Amortisör", "Gidon", "Ayna", "Egzoz Contası", "Karbüratör Kiti", "Rulman"];

    public static readonly string[] MotorModels = ["CG125", "CBF150", "YBR125", "NMAX155", "PCX160", "RS200", "DUKE250", "MT25", "CB650R", "XMAX300"];

    public static readonly string[] LedTypes = ["Şerit", "Ampul", "Panel", "Projektör", "Sürücü", "Lineer Armatür"];

    public static readonly string[] Voltages = ["12V", "24V", "220V"];

    public static readonly string[] Colours = ["Kırmızı", "Mavi", "Beyaz"];

    private static readonly string[] ColourCodes = ["KR", "MV", "BY"];

    private const string ProductsSql =
        """
                INSERT INTO catalog.products (id, name, description, category_id, brand_id, default_unit_id, tax_rate, status, attributes,
                                              archived_at, tenant_id, row_version, created_at, created_by)
                SELECT ($8::uuid[])[i],
                       CASE WHEN i % 2 = 1
                            THEN 'Sentetik ' || ($9::text[])[1 + (i / 2) % 12] || ' ' || ($10::text[])[1 + (i / 24) % 10]
                            ELSE 'LED ' || ($11::text[])[1 + (i / 2) % 6] || ' ' || (5 * (1 + (i / 2) % 20)) || 'W ' || ($12::text[])[1 + (i / 2) % 3]
                       END || ' #' || lpad(i::text, 5, '0'),
                       'Sentetik test ürünü',
                       CASE WHEN i % 2 = 1 THEN ($3::uuid[])[1 + i % 5] ELSE ($4::uuid[])[1 + i % 5] END,
                       ($5::uuid[])[1 + i % 8],
                       $2, 20,
                       s.status,
                       CASE WHEN i % 2 = 1 THEN jsonb_build_object('uyumlu_model', ($10::text[])[1 + (i / 24) % 10])
                            ELSE jsonb_build_object('voltaj', ($12::text[])[1 + (i / 2) % 3], 'guc_w', 5 * (1 + (i / 2) % 20)) END,
                       CASE WHEN s.status = 'archived' THEN s.created_at END,
                       $1, 1, s.created_at, $6
                FROM generate_series(1, $7) AS i
                CROSS JOIN LATERAL (SELECT CASE WHEN i % 97 = 0 THEN 'archived' WHEN i % 50 = 0 THEN 'inactive' ELSE 'active' END AS status,
                                           timestamptz '2026-01-01 00:00:00+00' + (i / 3) * interval '1 minute' AS created_at) AS s
        """;

    private const string VariantsSql =
        """
                INSERT INTO catalog.product_variants (id, product_id, sku, barcode, name, option_values, unit_id, status, attributes,
                                                      archived_at, tenant_id, row_version, created_at, created_by)
                SELECT gen_random_uuid(), p.id,
                       CASE WHEN i % 2 = 1 THEN 'MTR-' || lpad(i::text, 5, '0')
                            WHEN i % 4 = 0 THEN 'LED-' || lpad(i::text, 5, '0') || '-' || ($14::text[])[k]
                            ELSE 'LED-' || lpad(i::text, 5, '0') END,
                       '869' || lpad((i * 3 + k)::text, 10, '0'),
                       CASE WHEN i % 4 = 0 THEN ($13::text[])[k] END,
                       CASE WHEN i % 4 = 0 THEN jsonb_build_object('Renk', ($13::text[])[k]) ELSE '{}'::jsonb END,
                       $2,
                       CASE WHEN p.status = 'archived' THEN 'archived' ELSE 'active' END,
                       CASE WHEN i % 4 = 0 THEN jsonb_build_object('renk', ($13::text[])[k]) ELSE '{}'::jsonb END,
                       p.archived_at, $1, 1, p.created_at, $6
                FROM generate_series(1, $7) AS i
                CROSS JOIN LATERAL generate_series(1, CASE WHEN i % 4 = 0 THEN 3 ELSE 1 END) AS k
                JOIN catalog.products p ON p.tenant_id = $1 AND p.id = ($8::uuid[])[i]
        """;

    private SyntheticCatalog(CatalogClient owner, Guid motorRoot, Guid ledRoot, Guid[] motorCategories, Guid[] ledCategories)
    {
        Owner = owner;
        MotorRoot = motorRoot;
        LedRoot = ledRoot;
        MotorCategories = motorCategories;
        LedCategories = ledCategories;
    }

    public CatalogClient Owner { get; }

    public Guid TenantId => Owner.TenantId;

    public Guid MotorRoot { get; }

    public Guid LedRoot { get; }

    public IReadOnlyList<Guid> MotorCategories { get; }

    public IReadOnlyList<Guid> LedCategories { get; }

    public TimeSpan LoadTime { get; private set; }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>SKU of product <paramref name="i"/> (1-based) — the first variant's for LED items with colours.</summary>
    public static string Sku(int i) => i % 2 == 1 ? $"MTR-{i:D5}" : i % 4 == 0 ? $"LED-{i:D5}-KR" : $"LED-{i:D5}";

    /// <summary>Barcode of variant <paramref name="k"/> (1-based) of product <paramref name="i"/>.</summary>
    public static string Barcode(int i, int k = 1) => "869" + (i * 3 + k).ToString("D10", System.Globalization.CultureInfo.InvariantCulture);

    public static async Task<SyntheticCatalog> CreateAsync(ApiFixture fixture)
    {
        var owner = await CatalogClient.NewTenantOwnerAsync(fixture, "general");
        var unit = await owner.CreateUnitAsync("adet", "Adet");

        async Task<Guid> Category(string name, Guid? parentId) =>
            (await owner.CreatedAsync<JsonElement>("/api/v1/categories", new { name, parentId })).GetProperty("id").GetGuid();

        var motorRoot = await Category("Motor Yedek Parça", null);
        var ledRoot = await Category("LED Aydınlatma", null);
        var motorChildren = new[] { "Fren", "Motor", "Elektrik", "Şanzıman", "Süspansiyon" };
        var ledChildren = new[] { "Şerit LED", "Ampul", "Panel", "Projektör", "Sürücü" };
        var motorCategories = new Guid[motorChildren.Length];
        var ledCategories = new Guid[ledChildren.Length];
        for (var c = 0; c < motorChildren.Length; c++)
        {
            motorCategories[c] = await Category(motorChildren[c], motorRoot);
            ledCategories[c] = await Category(ledChildren[c], ledRoot);
        }

        var brands = new Guid[8];
        for (var b = 0; b < brands.Length; b++)
        {
            brands[b] = (await owner.CreatedAsync<JsonElement>("/api/v1/brands", new { name = $"Sentetik Marka {b + 1}" })).GetProperty("id").GetGuid();
        }

        await owner.CreatedAsync<JsonElement>("/api/v1/custom-fields", new { entityType = "product", key = "voltaj", label = "Voltaj", dataType = "enum", options = Voltages, filterable = true });
        await owner.CreatedAsync<JsonElement>("/api/v1/custom-fields", new { entityType = "product", key = "guc_w", label = "Güç", dataType = "integer", unit = "W", filterable = true });
        await owner.CreatedAsync<JsonElement>("/api/v1/custom-fields", new { entityType = "product", key = "uyumlu_model", label = "Uyumlu model", dataType = "text", filterable = true });
        await owner.CreatedAsync<JsonElement>("/api/v1/custom-fields", new { entityType = "variant", key = "renk", label = "Renk", dataType = "enum", options = Colours, filterable = true });

        var catalog = new SyntheticCatalog(owner, motorRoot, ledRoot, motorCategories, ledCategories);
        var watch = Stopwatch.StartNew();
        await BulkInsertAsync(fixture, owner, unit.Id, motorCategories, ledCategories, brands);
        catalog.LoadTime = watch.Elapsed;
        return catalog;
    }

    private static async Task BulkInsertAsync(ApiFixture fixture, CatalogClient owner, Guid unitId, Guid[] motorCategories, Guid[] ledCategories, Guid[] brands)
    {
        var productIds = Enumerable.Range(0, ProductCount).Select(_ => Guid.CreateVersion7()).ToArray();
        await using (var connection = new NpgsqlConnection(fixture.AppConnectionString))
        {
            await connection.OpenAsync(Ct);
            await using var transaction = await connection.BeginTransactionAsync(Ct);
            await using (var context = new NpgsqlCommand("SELECT set_config('app.tenant_id', $1, true), set_config('app.user_id', $2, true)", connection, transaction))
            {
                context.Parameters.Add(new NpgsqlParameter { Value = owner.TenantId.ToString() });
                context.Parameters.Add(new NpgsqlParameter { Value = owner.UserId.ToString() });
                await context.ExecuteNonQueryAsync(Ct);
            }

            foreach (var sql in new[] { ProductsSql, VariantsSql })
            {
                await using var insert = new NpgsqlCommand(sql, connection, transaction);
                insert.Parameters.Add(new NpgsqlParameter { Value = owner.TenantId });
                insert.Parameters.Add(new NpgsqlParameter { Value = unitId });
                insert.Parameters.Add(new NpgsqlParameter { Value = motorCategories });
                insert.Parameters.Add(new NpgsqlParameter { Value = ledCategories });
                insert.Parameters.Add(new NpgsqlParameter { Value = brands });
                insert.Parameters.Add(new NpgsqlParameter { Value = owner.UserId });
                insert.Parameters.Add(new NpgsqlParameter { Value = ProductCount });
                insert.Parameters.Add(new NpgsqlParameter { Value = productIds });
                insert.Parameters.Add(new NpgsqlParameter { Value = MotorParts });
                insert.Parameters.Add(new NpgsqlParameter { Value = MotorModels });
                insert.Parameters.Add(new NpgsqlParameter { Value = LedTypes });
                insert.Parameters.Add(new NpgsqlParameter { Value = Voltages });
                insert.Parameters.Add(new NpgsqlParameter { Value = Colours });
                insert.Parameters.Add(new NpgsqlParameter { Value = ColourCodes });
                insert.CommandTimeout = 300;
                await insert.ExecuteNonQueryAsync(Ct);
            }

            await transaction.CommitAsync(Ct);
        }

        // Fresh planner statistics, as autovacuum would provide after a large import (ANALYZE needs the table owner).
        await using var analyze = new NpgsqlConnection(fixture.MigratorConnectionString);
        await analyze.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("ANALYZE catalog.products; ANALYZE catalog.product_variants; ANALYZE catalog.categories;", analyze);
        await command.ExecuteNonQueryAsync(Ct);
    }

    /// <summary>Scalar over this tenant's rows as superuser (<c>$1</c> = tenant id) — the "truth" API results are compared with.</summary>
    public async Task<List<Guid>> IdsAsync(ApiFixture fixture, string sql, params object[] values)
    {
        await using var connection = new NpgsqlConnection(fixture.SuperuserConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = TenantId });
        foreach (var value in values)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = value });
        }

        await using var reader = await command.ExecuteReaderAsync(Ct);
        var ids = new List<Guid>();
        while (await reader.ReadAsync(Ct))
        {
            ids.Add(reader.GetGuid(0));
        }

        return ids;
    }

    /// <summary>Walks every page of a keyset-paged list, returning the item ids in order and each page's latency.</summary>
    public static async Task<(List<Guid> Ids, List<double> PageMs)> PageAllAsync(CatalogClient client, string path)
    {
        var ids = new List<Guid>();
        var latencies = new List<double>();
        string? cursor = null;
        for (var page = 0; page < 10_000; page++)
        {
            var separator = path.Contains('?') ? '&' : '?';
            var url = cursor is null ? path : $"{path}{separator}cursor={Uri.EscapeDataString(cursor)}";
            var watch = Stopwatch.StartNew();
            var response = await client.GetAsync(url);
            var text = await response.Content.ReadAsStringAsync(Ct);
            latencies.Add(watch.Elapsed.TotalMilliseconds);
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"GET {url}: {(int)response.StatusCode} {text}");
            using var document = JsonDocument.Parse(text);
            ids.AddRange(document.RootElement.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetGuid()));
            var info = document.RootElement.GetProperty("page");
            if (!info.GetProperty("hasMore").GetBoolean())
            {
                return (ids, latencies);
            }

            cursor = info.GetProperty("nextCursor").GetString();
        }

        throw new InvalidOperationException($"{path}: paging did not terminate");
    }
}
