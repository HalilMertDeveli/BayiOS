using System.Globalization;
using BayiOS.Modules.Imports.Contracts;
using BayiOS.Modules.Imports.Domain;
using BayiOS.Modules.Imports.Parsing;

namespace BayiOS.Modules.Imports.Application;

/// <summary>The validation decision for one source row (persisted as an <see cref="ImportRow"/>).</summary>
internal sealed record RowPlan(
    int RowNumber,
    string?[] Raw,
    Dictionary<string, string?> Values,
    string Action,
    List<ImportRowError> Errors,
    Guid? MatchedVariantId)
{
    public string Status => Action switch
    {
        RowActions.Create or RowActions.Update => RowStatuses.Valid,
        RowActions.Skip => RowStatuses.Skipped,
        _ => RowStatuses.Invalid,
    };
}

/// <summary>Normalised keys stored in <c>import_rows.normalized</c> besides the field keys.</summary>
internal static class ResolvedKeys
{
    public const string UnitId = "unitId";
    public const string BrandId = "brandId";
    public const string CategoryId = "categoryId";
    public const string VariantId = "variantId";
}

/// <summary>
/// Dry run (PRD IMP-05–IMP-09): normalises every row with the saved mapping, matches it against the catalog by SKU or
/// barcode, detects duplicates inside the file and conflicts with the catalog, and decides create / update / skip /
/// error. It only reads; nothing outside <c>imports</c> is written. Stock is never part of any import here.
/// </summary>
internal sealed class ImportRowValidator(IImportCatalogGateway catalog, IImportPriceGateway prices)
{
    public async Task<List<RowPlan>> ValidateAsync(ImportJob job, ParsedTable table, CancellationToken cancellationToken)
    {
        var mapping = ImportJson.ReadMap(job.Mapping);
        var columns = new Dictionary<string, (int Index, string Header)>(StringComparer.Ordinal);
        foreach (var (field, header) in mapping)
        {
            var index = table.Headers.ToList().IndexOf(header);
            if (index < 0)
            {
                throw new InvalidOperationException($"Mapped header '{header}' is not in the file.");
            }

            columns[field] = (index, header);
        }

        var plans = table.Rows.Select(row => Normalize(job, row, columns)).ToList();
        return job.Type == ImportTypes.Prices
            ? await PlanPricesAsync(job, plans, columns, cancellationToken)
            : await PlanProductsAsync(job, plans, columns, cancellationToken);
    }

    // ---------------------------------------------------------------- normalisation

    private static RowPlan Normalize(ImportJob job, ParsedRow row, Dictionary<string, (int Index, string Header)> columns)
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        var errors = new List<ImportRowError>();

        string? Cell(string field) => columns.TryGetValue(field, out var c) ? row.Cells[c.Index] : null;

        void Error(string field, string code, string message) =>
            errors.Add(new ImportRowError(field, columns.TryGetValue(field, out var c) ? c.Header : null, code, message));

        string? Text(string field, int maxLength, Func<string, string>? normalize = null)
        {
            var raw = Cell(field);
            if (raw is null)
            {
                return null;
            }

            if (ImportValues.LooksLikeFormula(raw))
            {
                Error(field, "value.formula_not_allowed", "Formül gibi başlayan değer (= veya @) kabul edilmez.");
                return null;
            }

            var value = normalize is null ? raw : normalize(raw);
            if (value.Length > maxLength || value.Any(char.IsControl))
            {
                Error(field, "value.too_long", $"En fazla {maxLength} karakter olmalı ve kontrol karakteri içermemeli.");
                return null;
            }

            values[field] = value;
            return value;
        }

        Text(ImportFields.Sku, 64, s => s.Trim().ToUpperInvariant());
        if (Cell(ImportFields.Barcode) is { } barcode)
        {
            if (!ImportValues.IsValidBarcode(barcode))
            {
                Error(ImportFields.Barcode, "barcode.invalid", "Geçersiz barkod (EAN-8/13, UPC kontrol hanesi veya boşluksuz en fazla 64 karakter).");
            }
            else
            {
                values[ImportFields.Barcode] = barcode;
            }
        }

        if (Cell(ImportFields.Price) is { } price)
        {
            if (ImportValues.TryParseDecimal(price, out var amount) && amount >= 0 && amount < 100_000_000_000_000m && ImportValues.HasScale(amount, 4))
            {
                values[ImportFields.Price] = amount.ToString(CultureInfo.InvariantCulture);
            }
            else
            {
                Error(ImportFields.Price, "price.invalid", "Fiyat 0 veya pozitif bir sayı olmalı (en fazla 4 ondalık).");
            }
        }

        if (job.Type == ImportTypes.Prices)
        {
            var minQuantity = 1m;
            if (Cell(ImportFields.MinQuantity) is { } min
                && !(ImportValues.TryParseDecimal(min, out minQuantity) && minQuantity > 0 && minQuantity < 1_000_000_000_000_000m && ImportValues.HasScale(minQuantity, 3)))
            {
                Error(ImportFields.MinQuantity, "min_quantity.invalid", "Minimum miktar 0'dan büyük olmalı (en fazla 3 ondalık).");
            }

            values[ImportFields.MinQuantity] = minQuantity.ToString(CultureInfo.InvariantCulture);
            if (!values.ContainsKey(ImportFields.Price) && Cell(ImportFields.Price) is null)
            {
                Error(ImportFields.Price, "value.required", "Fiyat zorunludur.");
            }
        }
        else
        {
            Text(ImportFields.Name, 200);
            Text(ImportFields.Description, 4000);
            Text(ImportFields.Unit, 64, s => s.Trim().ToLowerInvariant());
            Text(ImportFields.Brand, 200);
            Text(ImportFields.Category, 200);
            if (Cell(ImportFields.TaxRate) is { } tax)
            {
                if (ImportValues.TryParseDecimal(tax, out var rate) && rate is >= 0 and <= 100 && ImportValues.HasScale(rate, 2))
                {
                    values[ImportFields.TaxRate] = rate.ToString(CultureInfo.InvariantCulture);
                }
                else
                {
                    Error(ImportFields.TaxRate, "tax_rate.invalid", "KDV oranı 0–100 arası olmalı (en fazla 2 ondalık).");
                }
            }

            if (Cell(ImportFields.Status) is { } status)
            {
                if (ImportValues.ParseStatus(status) is { } parsed)
                {
                    values[ImportFields.Status] = parsed;
                }
                else
                {
                    Error(ImportFields.Status, "status.invalid", "Durum 'aktif' veya 'pasif' olmalı.");
                }
            }
        }

        var key = job.MatchKey == MatchKeys.Barcode ? ImportFields.Barcode : ImportFields.Sku;
        if (!values.ContainsKey(key) && !errors.Any(e => e.Field == key))
        {
            Error(key, "match_key.missing", key == ImportFields.Sku ? "Stok kodu (SKU) boş." : "Barkod boş.");
        }

        return new RowPlan(row.RowNumber, row.Cells, values, RowActions.Error, errors, null);
    }

    // ---------------------------------------------------------------- products

    private async Task<List<RowPlan>> PlanProductsAsync(
        ImportJob job, List<RowPlan> rows, Dictionary<string, (int Index, string Header)> columns, CancellationToken cancellationToken)
    {
        var (bySku, byBarcode) = await LookupAsync(rows, cancellationToken);
        var units = await catalog.FindUnitsAsync(cancellationToken);
        var brands = await catalog.FindBrandsAsync(cancellationToken);
        var categories = await catalog.FindCategoriesAsync(cancellationToken);

        // Current prices only when the product file also carries a price column.
        var currentPrices = new Dictionary<(Guid, decimal), decimal>();
        if (job.PriceListId is { } listId && columns.ContainsKey(ImportFields.Price))
        {
            var matched = bySku.Values.Concat(byBarcode.Values).Select(v => v.VariantId).Distinct().ToList();
            foreach (var (k, v) in await prices.FindPricesAsync(listId, matched, cancellationToken))
            {
                currentPrices[k] = v;
            }
        }

        var seenKeys = new Dictionary<string, int>(StringComparer.Ordinal);
        var seenSkus = new Dictionary<string, int>(StringComparer.Ordinal);
        var seenBarcodes = new Dictionary<string, int>(StringComparer.Ordinal);
        var result = new List<RowPlan>(rows.Count);
        foreach (var row in rows)
        {
            var v = row.Values;
            var errors = row.Errors;

            void Error(string field, string code, string message) =>
                errors.Add(new ImportRowError(field, columns.TryGetValue(field, out var c) ? c.Header : null, code, message));

            Resolve(v, ImportFields.Unit, ResolvedKeys.UnitId, units, Error, "unit.not_found", "Birim bulunamadı; önce katalogda tanımlayın.");
            Resolve(v, ImportFields.Brand, ResolvedKeys.BrandId, brands, Error, "brand.not_found", "Marka bulunamadı; önce katalogda tanımlayın.", fold: true);
            if (v.GetValueOrDefault(ImportFields.Category) is { } categoryName)
            {
                if (!categories.TryGetValue(ImportValues.Fold(categoryName), out var category))
                {
                    Error(ImportFields.Category, "category.not_found", "Kategori bulunamadı; önce katalogda tanımlayın.");
                }
                else if (category.Ambiguous)
                {
                    Error(ImportFields.Category, "category.ambiguous", "Bu adda birden fazla kategori var.");
                }
                else
                {
                    v[ResolvedKeys.CategoryId] = category.Id.ToString();
                }
            }

            var sku = v.GetValueOrDefault(ImportFields.Sku);
            var barcode = v.GetValueOrDefault(ImportFields.Barcode);
            var key = job.MatchKey == MatchKeys.Barcode ? barcode : sku;
            CheckDuplicate(seenKeys, key, row.RowNumber, job.MatchKey == MatchKeys.Barcode ? ImportFields.Barcode : ImportFields.Sku, Error);
            if (job.MatchKey == MatchKeys.Barcode)
            {
                CheckDuplicate(seenSkus, sku, row.RowNumber, ImportFields.Sku, Error);
            }
            else
            {
                CheckDuplicate(seenBarcodes, barcode, row.RowNumber, ImportFields.Barcode, Error);
            }

            var existing = key is null ? null : (job.MatchKey == MatchKeys.Barcode ? byBarcode : bySku).GetValueOrDefault(key);
            string action;
            if (existing is not null)
            {
                if (barcode is not null && byBarcode.TryGetValue(barcode, out var owner) && owner.VariantId != existing.VariantId)
                {
                    Error(ImportFields.Barcode, "barcode.taken", $"Barkod başka bir üründe kayıtlı ({owner.Sku}).");
                }

                if (sku is not null && bySku.TryGetValue(sku, out var skuOwner) && skuOwner.VariantId != existing.VariantId)
                {
                    Error(ImportFields.Sku, "sku.taken", "Stok kodu başka bir üründe kayıtlı.");
                }

                if (errors.Count > 0)
                {
                    action = RowActions.Error;
                }
                else if (existing.ProductStatus == "archived" || existing.VariantStatus == "archived")
                {
                    action = Skip(errors, "skipped.archived", "Ürün arşivde; içe aktarma arşivli ürünü değiştirmez.");
                }
                else if (job.Mode == ImportModes.CreateOnly)
                {
                    action = Skip(errors, "skipped.exists", "Ürün zaten var (yalnızca oluştur modu).");
                }
                else if (!HasProductChanges(existing, v) && !HasPriceChange(job, existing.VariantId, v, currentPrices))
                {
                    action = Skip(errors, "skipped.unchanged", "Değişiklik yok.");
                }
                else
                {
                    action = RowActions.Update;
                }
            }
            else
            {
                if (barcode is not null && byBarcode.TryGetValue(barcode, out var owner))
                {
                    Error(ImportFields.Barcode, "barcode.taken", $"Barkod başka bir üründe kayıtlı ({owner.Sku}).");
                }

                if (job.MatchKey == MatchKeys.Barcode && sku is not null && bySku.ContainsKey(sku))
                {
                    Error(ImportFields.Sku, "sku.taken", "Stok kodu başka bir üründe kayıtlı.");
                }

                if (errors.Count > 0)
                {
                    action = RowActions.Error;
                }
                else if (job.Mode == ImportModes.UpdateOnly)
                {
                    action = Skip(errors, "skipped.not_found", "Ürün bulunamadı (yalnızca güncelle modu).");
                }
                else
                {
                    foreach (var (field, label) in new[]
                    {
                        (ImportFields.Sku, "Stok kodu"), (ImportFields.Name, "Ürün adı"), (ImportFields.Unit, "Birim"), (ImportFields.TaxRate, "KDV oranı"),
                    })
                    {
                        if (v.GetValueOrDefault(field) is null)
                        {
                            Error(field, "value.required", $"{label} yeni ürün için zorunludur.");
                        }
                    }

                    action = errors.Count > 0 ? RowActions.Error : RowActions.Create;
                }
            }

            result.Add(row with { Action = action, MatchedVariantId = existing?.VariantId });
        }

        return result;
    }

    private static bool HasProductChanges(CatalogVariantSnapshot current, Dictionary<string, string?> v) =>
        Differs(v, ImportFields.Name, current.ProductName)
        || Differs(v, ImportFields.Description, current.Description)
        || Differs(v, ImportFields.Barcode, current.Barcode)
        || Differs(v, ImportFields.Sku, current.Sku)
        || Differs(v, ImportFields.Status, current.ProductStatus)
        || Differs(v, ResolvedKeys.UnitId, current.UnitId.ToString())
        || Differs(v, ResolvedKeys.BrandId, current.BrandId?.ToString())
        || Differs(v, ResolvedKeys.CategoryId, current.CategoryId?.ToString())
        || (v.GetValueOrDefault(ImportFields.TaxRate) is { } tax && decimal.Parse(tax, CultureInfo.InvariantCulture) != current.TaxRate);

    private static bool HasPriceChange(ImportJob job, Guid variantId, Dictionary<string, string?> v, Dictionary<(Guid, decimal), decimal> current) =>
        job.PriceListId is not null
        && v.GetValueOrDefault(ImportFields.Price) is { } price
        && (!current.TryGetValue((variantId, 1m), out var existing) || existing != decimal.Parse(price, CultureInfo.InvariantCulture));

    private static bool Differs(Dictionary<string, string?> v, string key, string? current) =>
        v.GetValueOrDefault(key) is { } value && !string.Equals(value, current, StringComparison.Ordinal);

    // ---------------------------------------------------------------- prices

    private async Task<List<RowPlan>> PlanPricesAsync(
        ImportJob job, List<RowPlan> rows, Dictionary<string, (int Index, string Header)> columns, CancellationToken cancellationToken)
    {
        var (bySku, byBarcode) = await LookupAsync(rows, cancellationToken);
        var variantIds = bySku.Values.Concat(byBarcode.Values).Select(v => v.VariantId).Distinct().ToList();
        var current = await prices.FindPricesAsync(job.PriceListId!.Value, variantIds, cancellationToken);
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        var result = new List<RowPlan>(rows.Count);
        foreach (var row in rows)
        {
            var v = row.Values;
            var errors = row.Errors;

            void Error(string field, string code, string message) =>
                errors.Add(new ImportRowError(field, columns.TryGetValue(field, out var c) ? c.Header : null, code, message));

            var keyField = job.MatchKey == MatchKeys.Barcode ? ImportFields.Barcode : ImportFields.Sku;
            var key = v.GetValueOrDefault(keyField);
            var existing = key is null ? null : (job.MatchKey == MatchKeys.Barcode ? byBarcode : bySku).GetValueOrDefault(key);
            if (key is not null && existing is null)
            {
                Error(keyField, "product.not_found", "Ürün bulunamadı; fiyat içe aktarma ürün oluşturmaz.");
            }

            var minQuantity = decimal.Parse(v[ImportFields.MinQuantity]!, CultureInfo.InvariantCulture);
            if (existing is not null)
            {
                v[ResolvedKeys.VariantId] = existing.VariantId.ToString();
                CheckDuplicate(seen, $"{existing.VariantId:N}|{minQuantity.ToString(CultureInfo.InvariantCulture)}", row.RowNumber, keyField, Error);
            }

            string action;
            if (errors.Count > 0 || existing is null)
            {
                action = RowActions.Error;
            }
            else if (existing.ProductStatus == "archived" || existing.VariantStatus == "archived")
            {
                action = Skip(errors, "skipped.archived", "Ürün arşivde.");
            }
            else
            {
                var price = decimal.Parse(v[ImportFields.Price]!, CultureInfo.InvariantCulture);
                var exists = current.TryGetValue((existing.VariantId, minQuantity), out var currentPrice);
                action = (exists, job.Mode) switch
                {
                    (true, ImportModes.CreateOnly) => Skip(errors, "skipped.exists", "Fiyat zaten var (yalnızca oluştur modu)."),
                    (false, ImportModes.UpdateOnly) => Skip(errors, "skipped.not_found", "Listede fiyat yok (yalnızca güncelle modu)."),
                    (true, _) when currentPrice == price => Skip(errors, "skipped.unchanged", "Fiyat aynı."),
                    (true, _) => RowActions.Update,
                    _ => RowActions.Create,
                };
            }

            result.Add(row with { Action = action, MatchedVariantId = existing?.VariantId });
        }

        return result;
    }

    // ---------------------------------------------------------------- helpers

    private async Task<(Dictionary<string, CatalogVariantSnapshot> BySku, Dictionary<string, CatalogVariantSnapshot> ByBarcode)> LookupAsync(
        List<RowPlan> rows, CancellationToken cancellationToken)
    {
        var skus = rows.Select(r => r.Values.GetValueOrDefault(ImportFields.Sku)).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        var barcodes = rows.Select(r => r.Values.GetValueOrDefault(ImportFields.Barcode)).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        var found = await catalog.FindVariantsAsync(skus, barcodes, cancellationToken);
        var bySku = new Dictionary<string, CatalogVariantSnapshot>(StringComparer.Ordinal);
        var byBarcode = new Dictionary<string, CatalogVariantSnapshot>(StringComparer.Ordinal);
        foreach (var variant in found)
        {
            bySku[variant.Sku] = variant;
            if (variant.Barcode is not null)
            {
                byBarcode[variant.Barcode] = variant;
            }
        }

        return (bySku, byBarcode);
    }

    private static void Resolve(
        Dictionary<string, string?> values,
        string field,
        string idKey,
        IReadOnlyDictionary<string, Guid> lookup,
        Action<string, string, string> error,
        string code,
        string message,
        bool fold = false)
    {
        if (values.GetValueOrDefault(field) is not { } name)
        {
            return;
        }

        if (lookup.TryGetValue(fold ? ImportValues.Fold(name) : name, out var id))
        {
            values[idKey] = id.ToString();
        }
        else
        {
            error(field, code, message);
        }
    }

    private static void CheckDuplicate(Dictionary<string, int> seen, string? key, int rowNumber, string field, Action<string, string, string> error)
    {
        if (key is null)
        {
            return;
        }

        if (seen.TryGetValue(key, out var first))
        {
            error(field, "duplicate.in_file", $"Dosyada tekrar ediyor (ilk geçtiği satır: {first.ToString(CultureInfo.InvariantCulture)}).");
        }
        else
        {
            seen[key] = rowNumber;
        }
    }

    private static string Skip(List<ImportRowError> errors, string code, string message)
    {
        errors.Add(new ImportRowError(null, null, code, message));
        return RowActions.Skip;
    }
}
