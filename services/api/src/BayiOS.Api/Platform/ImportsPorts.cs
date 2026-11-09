using System.Globalization;
using System.Linq.Expressions;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using BayiOS.Api.Platform.Jobs;
using BayiOS.Modules.Catalog.Application;
using BayiOS.Modules.Catalog.Contracts;
using BayiOS.Modules.Catalog.Domain;
using BayiOS.Modules.Catalog.Infrastructure.Persistence;
using BayiOS.Modules.Identity.Contracts;
using BayiOS.Modules.Imports.Contracts;
using BayiOS.Modules.Imports.Parsing;
using BayiOS.Modules.Pricing.Application;
using BayiOS.Modules.Pricing.Contracts;
using BayiOS.Modules.Pricing.Infrastructure.Persistence;
using BayiOS.SharedKernel.Entitlements;
using BayiOS.SharedKernel.Persistence;
using BayiOS.SharedKernel.Platform.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace BayiOS.Api.Platform;

/// <summary>
/// Composition-root adapters for the Imports ports. Reads go through the owning modules' DbContexts (tenant query
/// filters plus RLS); every write goes through the owning module's application command — Catalog's
/// <see cref="ProductService"/> and Pricing's <see cref="PriceListService"/> — with their validation, unique-key
/// handling and stamps, on the job's shared connection and transaction. Nothing here touches stock.
/// </summary>
internal static class ImportsPorts
{
    public static IServiceCollection AddImportsPorts(this IServiceCollection services)
    {
        services.AddScoped<IImportCatalogGateway, CatalogImportGateway>();
        services.AddScoped<IImportPriceGateway, PricingImportGateway>();
        services.AddScoped<IImportActorDirectory, MembershipImportActorDirectory>();
        services.AddScoped<IImportSystemJobs, ImportSystemJobs>();
        return services;
    }
}

internal sealed class CatalogImportGateway(CatalogDbContext catalog, ProductService products) : IImportCatalogGateway
{
    private const string Archived = "archived";

    /// <summary>Readable JSON for stored previous/applied values (the outer API serializer escapes again).</summary>
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private Dictionary<string, CustomFieldDefinition>? _fields;

    public async Task<IReadOnlyList<CatalogVariantSnapshot>> FindVariantsAsync(
        IReadOnlyCollection<string> skus, IReadOnlyCollection<string> barcodes, CancellationToken cancellationToken)
    {
        if (skus.Count == 0 && barcodes.Count == 0)
        {
            return [];
        }

        var skuList = skus.ToList();
        var barcodeList = barcodes.ToList();
        return await SnapshotsAsync(v => skuList.Contains(v.Sku) || (v.Barcode != null && barcodeList.Contains(v.Barcode)), cancellationToken);
    }

    public async Task<IReadOnlyList<CatalogVariantSnapshot>> FindVariantsByIdAsync(IReadOnlyCollection<Guid> variantIds, CancellationToken cancellationToken)
    {
        if (variantIds.Count == 0)
        {
            return [];
        }

        var ids = variantIds.Distinct().ToList();
        return await SnapshotsAsync(v => ids.Contains(v.Id), cancellationToken);
    }

    public async Task<IReadOnlyDictionary<string, Guid>> FindUnitsAsync(CancellationToken cancellationToken) =>
        await catalog.Units.AsNoTracking().Where(u => u.ArchivedAt == null).ToDictionaryAsync(u => u.Code, u => u.Id, StringComparer.Ordinal, cancellationToken);

    public async Task<IReadOnlyDictionary<string, Guid>> FindBrandsAsync(CancellationToken cancellationToken) =>
        (await catalog.Brands.AsNoTracking().Where(b => b.ArchivedAt == null).OrderBy(b => b.CreatedAt).Select(b => new { b.Id, b.Name }).ToListAsync(cancellationToken))
        .GroupBy(b => ImportValues.Fold(b.Name), StringComparer.Ordinal)
        .ToDictionary(g => g.Key, g => g.First().Id, StringComparer.Ordinal);

    public async Task<IReadOnlyDictionary<string, CatalogCategoryMatch>> FindCategoriesAsync(CancellationToken cancellationToken) =>
        (await catalog.Categories.AsNoTracking().Where(c => c.ArchivedAt == null).Select(c => new { c.Id, c.Name }).ToListAsync(cancellationToken))
        .GroupBy(c => ImportValues.Fold(c.Name), StringComparer.Ordinal)
        .ToDictionary(g => g.Key, g => new CatalogCategoryMatch(g.First().Id, g.Count() > 1), StringComparer.Ordinal);

    public async Task<IImportAttributeNormalizer> LoadAttributesAsync(CancellationToken cancellationToken) =>
        new AttributeNormalizer(await FieldsAsync(cancellationToken));

    public async Task<CatalogWriteResult> CreateProductAsync(ImportProductValues values, CancellationToken cancellationToken)
    {
        var (productAttributes, variantAttributes) = await SplitAttributesAsync(values.Attributes, cancellationToken);
        BayiOS.Modules.Catalog.Application.AppResult<ProductResponse> result;
        try
        {
            result = await products.CreateProductAsync(
                new CreateProductRequest(
                    values.Name,
                    values.Description,
                    values.CategoryId,
                    values.BrandId,
                    values.UnitId,
                    values.TaxRate,
                    values.Status,
                    values.Sku,
                    values.Barcode,
                    null,
                    productAttributes,
                    variantAttributes),
                cancellationToken);
        }
        catch (EntitlementException ex)
        {
            // max_products reached (OD-02): this row fails with the entitlement code, the job goes on (nothing written).
            catalog.ChangeTracker.Clear();
            return LimitReached(ex);
        }

        // Each row is its own command: a rejected row must not leave entities behind for the next SaveChanges.
        catalog.ChangeTracker.Clear();
        if (result.Error is { } error)
        {
            return Failed(error);
        }

        var product = result.Value!;
        return new CatalogWriteResult("created", product.Variants[0].Id, product.Id, null, null);
    }

    public async Task<CatalogWriteResult> UpdateProductAsync(CatalogVariantSnapshot current, ImportProductValues values, CancellationToken cancellationToken)
    {
        var before = ImportCatalogFields.Read(current);
        var next = new JsonObject
        {
            ["name"] = values.Name,
            ["description"] = values.Description,
            ["taxRate"] = values.TaxRate,
            ["brandId"] = values.BrandId?.ToString(),
            ["categoryId"] = values.CategoryId?.ToString(),
            ["status"] = values.Status,
            ["sku"] = values.Sku,
            ["barcode"] = values.Barcode,
            ["unitId"] = values.UnitId?.ToString(),
        };
        foreach (var (key, json) in values.Attributes ?? new Dictionary<string, string>())
        {
            next[ImportCatalogFields.AttributePrefix + key] = JsonNode.Parse(json);
        }

        // Null = "not in the file": only present values that differ from the current record are written.
        var changed = new JsonObject();
        var previous = new JsonObject();
        foreach (var (key, value) in next)
        {
            if (value is not null && !ImportCatalogFields.Same(before[key], value))
            {
                changed[key] = value.DeepClone();
                previous[key] = before[key]?.DeepClone();
            }
        }

        if (changed.Count == 0)
        {
            return new CatalogWriteResult("unchanged", current.VariantId, current.ProductId, null, null);
        }

        if (await WriteAsync(current, changed, cancellationToken) is { } failed)
        {
            return failed;
        }

        // What Catalog actually stored (it cleans text): the rollback compares the live record with this.
        var after = (await FindVariantsByIdAsync([current.VariantId], cancellationToken)).Single();
        var stored = ImportCatalogFields.Read(after);
        var applied = new JsonObject();
        foreach (var (key, _) in previous)
        {
            applied[key] = stored[key]?.DeepClone();
        }

        return new CatalogWriteResult(
            "updated", current.VariantId, current.ProductId, previous.ToJsonString(Json), null, AppliedValuesJson: applied.ToJsonString(Json));
    }

    public async Task<CatalogWriteResult> ArchiveProductAsync(CatalogVariantSnapshot current, CancellationToken cancellationToken)
    {
        if (current.ProductStatus == Archived)
        {
            return new CatalogWriteResult("unchanged", current.VariantId, current.ProductId, null, null);
        }

        try
        {
            return await products.ArchiveProductAsync(current.ProductId, current.ProductVersion, cancellationToken) is { } error
                ? Failed(error)
                : new CatalogWriteResult("archived", current.VariantId, current.ProductId, null, null);
        }
        finally
        {
            catalog.ChangeTracker.Clear();
        }
    }

    public async Task<CatalogWriteResult> RestoreProductAsync(CatalogVariantSnapshot current, string previousValuesJson, CancellationToken cancellationToken)
    {
        var previous = JsonNode.Parse(previousValuesJson) as JsonObject ?? [];
        return await WriteAsync(current, previous, cancellationToken)
            ?? new CatalogWriteResult("updated", current.VariantId, current.ProductId, null, null);
    }

    /// <summary>
    /// Writes <paramref name="values"/> (<see cref="ImportCatalogFields"/> keys; a JSON null clears the field or removes
    /// the attribute) through the product and variant commands, with the snapshot's versions as the concurrency check.
    /// </summary>
    private async Task<CatalogWriteResult?> WriteAsync(CatalogVariantSnapshot current, JsonObject values, CancellationToken cancellationToken)
    {
        var fields = await FieldsAsync(cancellationToken);
        bool Has(string key, out JsonNode? value) => values.TryGetPropertyValue(key, out value);
        Optional<string?> Text(string key) => Has(key, out var v) ? new Optional<string?>(true, v?.GetValue<string>()) : default;
        Optional<Guid?> Id(string key) => Has(key, out var v) ? new Optional<Guid?>(true, v is null ? null : Guid.Parse(v.GetValue<string>(), CultureInfo.InvariantCulture)) : default;

        var productAttributes = new JsonObject();
        var variantAttributes = new JsonObject();
        foreach (var (key, value) in values.Where(p => p.Key.StartsWith(ImportCatalogFields.AttributePrefix, StringComparison.Ordinal)))
        {
            var name = key[ImportCatalogFields.AttributePrefix.Length..];
            var target = fields.TryGetValue(name, out var field) && field.EntityType == CustomFieldEntityTypes.Variant ? variantAttributes : productAttributes;
            target[name] = value?.DeepClone();
        }

        var taxRate = Has("taxRate", out var tax) ? new Optional<decimal?>(true, tax?.GetValue<decimal>()) : default;
        var productPatch = new ProductPatch(
            Text("name"),
            Text("description"),
            Id("categoryId"),
            Id("brandId"),
            default,
            taxRate,
            Text("status"),
            productAttributes.Count > 0 ? new Optional<JsonObject?>(true, productAttributes) : default);
        var variantPatch = new VariantPatch(
            Text("sku"),
            Text("barcode"),
            default,
            default,
            Id("unitId"),
            default,
            variantAttributes.Count > 0 ? new Optional<JsonObject?>(true, variantAttributes) : default);

        try
        {
            if (productPatch.Name.IsSet || productPatch.Description.IsSet || productPatch.CategoryId.IsSet || productPatch.BrandId.IsSet
                || productPatch.TaxRate.IsSet || productPatch.Status.IsSet || productPatch.Attributes.IsSet)
            {
                BayiOS.Modules.Catalog.Application.AppResult<ProductResponse> result;
                try
                {
                    result = await products.UpdateProductAsync(current.ProductId, productPatch, current.ProductVersion, cancellationToken);
                }
                catch (EntitlementException ex)
                {
                    return LimitReached(ex); // restoring an archived product over max_products (OD-02)
                }

                if (result.Error is { } error)
                {
                    return Failed(error);
                }
            }

            if (variantPatch.Sku.IsSet || variantPatch.Barcode.IsSet || variantPatch.UnitId.IsSet || variantPatch.Attributes.IsSet)
            {
                var result = await products.UpdateVariantAsync(current.VariantId, variantPatch, current.VariantVersion, cancellationToken);
                if (result.Error is { } error)
                {
                    return Failed(error);
                }
            }
        }
        finally
        {
            catalog.ChangeTracker.Clear();
        }

        return null;
    }

    private async Task<(IReadOnlyDictionary<string, JsonElement>? Product, IReadOnlyDictionary<string, JsonElement>? Variant)> SplitAttributesAsync(
        IReadOnlyDictionary<string, string>? attributes, CancellationToken cancellationToken)
    {
        if (attributes is null || attributes.Count == 0)
        {
            return (null, null);
        }

        var fields = await FieldsAsync(cancellationToken);
        var product = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var variant = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var (key, json) in attributes)
        {
            using var document = JsonDocument.Parse(json);
            var target = fields.TryGetValue(key, out var field) && field.EntityType == CustomFieldEntityTypes.Variant ? variant : product;
            target[key] = document.RootElement.Clone();
        }

        return (product.Count > 0 ? product : null, variant.Count > 0 ? variant : null);
    }

    private async Task<Dictionary<string, CustomFieldDefinition>> FieldsAsync(CancellationToken cancellationToken) =>
        _fields ??= (await catalog.CustomFields.AsNoTracking().ToListAsync(cancellationToken)).ToDictionary(f => f.Key, StringComparer.Ordinal);

    private async Task<List<CatalogVariantSnapshot>> SnapshotsAsync(Expression<Func<ProductVariant, bool>> filter, CancellationToken cancellationToken) =>
        await catalog.Variants.AsNoTracking()
            .Where(filter)
            .Join(
                catalog.Products.AsNoTracking(),
                variant => variant.ProductId,
                product => product.Id,
                (variant, product) => new CatalogVariantSnapshot(
                    variant.Id,
                    product.Id,
                    variant.Sku,
                    variant.Barcode,
                    variant.UnitId,
                    variant.Status,
                    variant.RowVersion,
                    product.Name,
                    product.Description,
                    product.TaxRate,
                    product.BrandId,
                    product.CategoryId,
                    product.Status,
                    product.RowVersion,
                    product.Attributes,
                    variant.Attributes))
            .ToListAsync(cancellationToken);

    private static CatalogWriteResult LimitReached(EntitlementException ex) =>
        new("failed", null, null, null, new Dictionary<string, string[]> { [string.Empty] = [ex.Message] }, ex.Code);

    private static CatalogWriteResult Failed(BayiOS.Modules.Catalog.Application.AppError error) =>
        new("failed", null, null, null, error.Errors is { } errors ? errors.ToDictionary() : new Dictionary<string, string[]> { [string.Empty] = [error.Title] }, error.Code);

    /// <summary>Catalog's <see cref="CustomFieldValidator"/> over the tenant's definitions (loaded once).</summary>
    private sealed class AttributeNormalizer(Dictionary<string, CustomFieldDefinition> definitions) : IImportAttributeNormalizer
    {
        public IReadOnlyDictionary<string, ImportAttributeField> Fields { get; } = definitions.Values.ToDictionary(
            d => d.Key,
            d => new ImportAttributeField(d.Key, d.Label, d.EntityType, d.DataType, d.Required, d.ArchivedAt is not null, FieldSet.ReadOptions(d.Options)),
            StringComparer.Ordinal);

        public string? Normalize(string key, JsonNode? value, out string? error)
        {
            if (!definitions.TryGetValue(key, out var definition) || definition.ArchivedAt is not null)
            {
                error = "Unknown or archived custom field.";
                return null;
            }

            return CustomFieldValidator.Normalize(definition, value, out error)?.ToJsonString(Json);
        }
    }
}

internal sealed partial class PricingImportGateway(IPriceListDirectory directory, PriceListService service, PricingDbContext pricing) : IImportPriceGateway
{
    public async Task<ImportPriceListInfo?> FindPriceListAsync(Guid priceListId, CancellationToken cancellationToken) =>
        await directory.FindAsync(priceListId, cancellationToken) is { } list ? new ImportPriceListInfo(list.Id, list.Name, list.Kind, list.Currency, list.Status) : null;

    public async Task<IReadOnlyDictionary<(Guid VariantId, decimal MinQuantity), decimal>> FindPricesAsync(
        Guid priceListId, IReadOnlyCollection<Guid> variantIds, CancellationToken cancellationToken) =>
        (await FindPriceItemsAsync(priceListId, variantIds, cancellationToken)).ToDictionary(i => i.Key, i => i.Value.UnitPrice);

    public async Task<IReadOnlyDictionary<(Guid VariantId, decimal MinQuantity), ImportPriceItemInfo>> FindPriceItemsAsync(
        Guid priceListId, IReadOnlyCollection<Guid> variantIds, CancellationToken cancellationToken)
    {
        if (variantIds.Count == 0)
        {
            return new Dictionary<(Guid, decimal), ImportPriceItemInfo>();
        }

        var ids = variantIds.ToList();
        return (await pricing.Items.AsNoTracking()
                .Where(i => i.PriceListId == priceListId && ids.Contains(i.VariantId))
                .Select(i => new { i.Id, i.VariantId, i.MinQuantity, i.UnitPrice })
                .ToListAsync(cancellationToken))
            .ToDictionary(i => (i.VariantId, i.MinQuantity), i => new ImportPriceItemInfo(i.Id, i.UnitPrice));
    }

    public async Task<string?> DeletePriceItemAsync(Guid priceListId, Guid itemId, CancellationToken cancellationToken)
    {
        var result = await service.DeleteItemAsync(priceListId, itemId, cancellationToken);
        pricing.ChangeTracker.Clear();
        return result.Error?.Title;
    }

    /// <summary>
    /// One Pricing upsert per batch. Pricing validates the whole request; items it rejects (<c>items[i]…</c>) are
    /// reported and the rest is retried once without them, so one bad row does not fail its neighbours.
    /// </summary>
    public async Task<PriceUpsertResult> UpsertPricesAsync(Guid priceListId, IReadOnlyList<ImportPriceItem> items, CancellationToken cancellationToken)
    {
        var ids = new Guid?[items.Count];
        var errors = new Dictionary<int, string[]>();
        var pending = Enumerable.Range(0, items.Count).ToList();
        for (var attempt = 0; attempt < 2 && pending.Count > 0; attempt++)
        {
            var request = new UpsertPriceListItemsRequest(
                [.. pending.Select(i => new PriceListItemInput(items[i].VariantId, items[i].MinQuantity, items[i].UnitPrice))]);
            var result = await service.UpsertItemsAsync(priceListId, request, cancellationToken);
            pricing.ChangeTracker.Clear();
            if (result.Error is null)
            {
                for (var k = 0; k < pending.Count; k++)
                {
                    ids[pending[k]] = result.Value!.Items[k].Id;
                }

                return new PriceUpsertResult(ids, errors, null);
            }

            var itemErrors = (result.Error.Errors ?? new Dictionary<string, string[]>())
                .Select(e => (Match: ItemIndex().Match(e.Key), e.Value))
                .Where(e => e.Match.Success)
                .ToList();
            if (itemErrors.Count == 0)
            {
                foreach (var index in pending)
                {
                    errors[index] = [result.Error.Title];
                }

                return new PriceUpsertResult(ids, errors, null);
            }

            var rejected = new HashSet<int>();
            foreach (var (match, messages) in itemErrors)
            {
                var original = pending[int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)];
                errors[original] = messages;
                rejected.Add(original);
            }

            pending = [.. pending.Where(i => !rejected.Contains(i))];
        }

        foreach (var index in pending)
        {
            errors[index] = ["Price list rejected the batch."];
        }

        return new PriceUpsertResult(ids, errors, null);
    }

    [GeneratedRegex(@"^items\[(\d+)\]")]
    private static partial Regex ItemIndex();
}

internal sealed class MembershipImportActorDirectory(IMembershipLookup memberships) : IImportActorDirectory
{
    public Task<string?> FindActiveRoleAsync(Guid userId, Guid tenantId, CancellationToken cancellationToken) =>
        memberships.FindActiveRoleAsync(userId, tenantId, cancellationToken);
}

/// <summary>
/// Tenant-less job primitives for the Imports maintenance over <see cref="JobStore"/> (no change to the platform job
/// API): a scheduler tick, the dead-job scan of <c>platform.jobs</c> (no RLS, data-model §2.3) and per-tenant fan-out
/// on the maintenance job's own transaction.
/// </summary>
internal sealed class ImportSystemJobs(IServiceProvider services, UnitOfWork unitOfWork) : IImportSystemJobs
{
    public bool WorkerEnabled
    {
        get
        {
            try
            {
                return services.GetRequiredService<IOptions<JobsOptions>>().Value.Enabled;
            }
            catch (OptionsValidationException)
            {
                return false; // unconfigured instance: no background work (same as the platform worker)
            }
        }
    }

    public async Task EnqueueSystemAsync(string type, string dedupeKey, CancellationToken cancellationToken)
    {
        EnsureRegistered(type);
        await services.GetRequiredService<JobStore>().InsertAsync(new NewJob(null, type, "{}", null, -1, 3, dedupeKey, null), cancellationToken);
    }

    public async Task<IReadOnlyList<DeadImportJob>> FindDeadJobsAsync(
        IReadOnlyCollection<string> types, TimeSpan lookback, int limit, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT DISTINCT ON (d.payload->>'importId', d.payload->>'runId')
                   d.tenant_id, d.type, (d.payload->>'importId')::uuid, (d.payload->>'runId')::uuid
            FROM platform.jobs d
            WHERE d.type = ANY(@types) AND d.status = 'dead' AND d.tenant_id IS NOT NULL
              AND d.finished_at > now() - @lookback
              AND d.payload ? 'importId' AND d.payload ? 'runId'
              AND NOT EXISTS (
                  SELECT 1 FROM platform.jobs l
                  WHERE l.type = d.type AND l.status IN ('queued', 'running')
                    AND l.payload->>'importId' = d.payload->>'importId' AND l.payload->>'runId' = d.payload->>'runId')
            ORDER BY d.payload->>'importId', d.payload->>'runId', d.finished_at
            LIMIT @limit
            """,
            unitOfWork.Connection,
            unitOfWork.Transaction);
        command.Parameters.Add(new NpgsqlParameter<string[]>("types", [.. types]));
        command.Parameters.Add(new NpgsqlParameter<TimeSpan>("lookback", lookback));
        command.Parameters.Add(new NpgsqlParameter<int>("limit", limit));
        var found = new List<DeadImportJob>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            found.Add(new DeadImportJob(reader.GetGuid(0), reader.GetString(1), reader.GetGuid(2), reader.GetGuid(3)));
        }

        return found;
    }

    public async Task EnqueueForTenantAsync(Guid tenantId, JobRequest job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        EnsureRegistered(job.Type);
        var transaction = unitOfWork.Transaction
            ?? throw new InvalidOperationException("Jobs are enqueued inside the unit of work transaction.");
        await JobStore.InsertAsync(
            unitOfWork.Connection,
            transaction,
            new NewJob(tenantId, job.Type, JobStore.Serialize(job.Payload), job.RunAfter, job.Priority, job.MaxAttempts, job.DedupeKey, null),
            cancellationToken);
    }

    /// <summary>Same rule as <c>IBackgroundJobs</c> (#89): a type without a registered handler is never enqueued.</summary>
    private void EnsureRegistered(string type)
    {
        if (!services.GetRequiredService<JobHandlerRegistry>().IsRegistered(type))
        {
            throw new UnknownJobTypeException(type);
        }
    }
}
