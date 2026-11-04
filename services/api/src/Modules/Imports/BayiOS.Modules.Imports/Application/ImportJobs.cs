using System.Globalization;
using BayiOS.Modules.Imports.Contracts;
using BayiOS.Modules.Imports.Domain;
using BayiOS.Modules.Imports.Infrastructure.Files;
using BayiOS.Modules.Imports.Infrastructure.Persistence;
using BayiOS.Modules.Imports.Parsing;
using BayiOS.SharedKernel.Authorization;
using BayiOS.SharedKernel.Platform.Audit;
using BayiOS.SharedKernel.Platform.Jobs;
using BayiOS.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BayiOS.Modules.Imports.Application;

/// <summary><c>Imports:*</c> configuration.</summary>
internal sealed class ImportsOptions
{
    public const string Section = "Imports";

    /// <summary>Rows per execution chunk; each chunk is one job run and one transaction (≤ 500: Pricing batch limit).</summary>
    public int ChunkSize { get; set; } = 500;
}

/// <summary>Row lock on an import inside the job/request transaction (serialises jobs with cancel/start).</summary>
internal static class ImportLocks
{
    public static async Task<ImportJob?> LockAsync(ImportsDbContext db, Guid id, Guid tenantId, CancellationToken cancellationToken)
    {
        await db.JoinTransactionAsync(cancellationToken);
        return (await db.Jobs
                .FromSql($"SELECT * FROM imports.import_jobs WHERE id = {id} AND tenant_id = {tenantId} FOR UPDATE")
                .IgnoreQueryFilters()
                .ToListAsync(cancellationToken))
            .SingleOrDefault();
    }
}

/// <summary>
/// <c>imports.validate</c>: parses the stored file again, runs <see cref="ImportRowValidator"/> and replaces the staged
/// rows with the dry-run result. Idempotent: a job of an older run (other <c>runId</c>) or a non-validating import is
/// a no-op, and staging is delete-then-insert in one transaction.
/// </summary>
internal sealed class ValidateImportJob(
    ImportsDbContext db,
    IImportFileStore files,
    ImportRowValidator validator,
    IRequestContext requestContext,
    TimeProvider clock) : IJobHandler
{
    public const string Type = "imports.validate";

    public async Task HandleAsync(JobExecution job, CancellationToken cancellationToken)
    {
        var payload = job.PayloadAs<ImportRunPayload>();
        var tenantId = requestContext.TenantId ?? throw new InvalidOperationException("Import jobs need a tenant.");
        var import = await ImportLocks.LockAsync(db, payload.ImportId, tenantId, cancellationToken);
        if (import is null || import.Status != ImportStatuses.Validating || import.RunId != payload.RunId)
        {
            return;
        }

        var now = ImportClock.Now(clock);
        var content = await files.ReadAsync(import.Id, cancellationToken);
        if (content is null)
        {
            await FailAsync(import, "imports.file_missing", "The uploaded file is no longer available; upload it again.", now, cancellationToken);
            return;
        }

        ParsedTable table;
        try
        {
            table = TabularFileReader.Read(content, import.FileName, null, cancellationToken);
        }
        catch (ImportFileRejectedException ex)
        {
            await FailAsync(import, ex.Code, ex.Message, now, cancellationToken);
            return;
        }

        if (!table.Headers.SequenceEqual(ImportJson.ReadStrings(import.Headers), StringComparer.Ordinal))
        {
            await FailAsync(import, "imports.file_changed", "The stored file no longer matches its headers.", now, cancellationToken);
            return;
        }

        var plans = await validator.ValidateAsync(import, table, cancellationToken);
        await db.Rows.Where(r => r.ImportJobId == import.Id).ExecuteDeleteAsync(cancellationToken);
        db.Rows.AddRange(plans.Select(p => ImportRow.Create(
            tenantId,
            import.Id,
            p.RowNumber,
            ImportJson.Serialize(p.Raw),
            ImportJson.Serialize(p.Values),
            p.Action,
            p.Status,
            ImportJson.Serialize(p.Errors),
            p.MatchedVariantId)));

        import.PreviewCreate = plans.Count(p => p.Action == RowActions.Create);
        import.PreviewUpdate = plans.Count(p => p.Action == RowActions.Update);
        import.PreviewSkip = plans.Count(p => p.Action == RowActions.Skip);
        import.PreviewError = plans.Count(p => p.Action == RowActions.Error);
        import.Status = ImportStatuses.Validated;
        import.ValidatedAt = now;
        import.Touch(null, now);
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task FailAsync(ImportJob import, string code, string message, DateTimeOffset now, CancellationToken cancellationToken)
    {
        import.Status = ImportStatuses.Failed;
        import.FailureCode = code;
        import.FailureMessage = message;
        import.FinishedAt = now;
        import.Touch(null, now);
        await db.SaveChangesAsync(cancellationToken);
    }
}

/// <summary>
/// <c>imports.execute</c>: processes the next chunk of <c>valid</c> rows after the checkpoint in this job's
/// transaction, then enqueues the next chunk (same transaction) or completes the import. A crash rolls the whole
/// chunk back and the retry repeats it; rows that left <c>valid</c> are never processed twice, and creates re-check
/// the SKU first, so neither a retry nor a resume duplicates products. Runs with the permissions of the user who
/// started the import, re-checked on every chunk.
/// </summary>
internal sealed class ExecuteImportJob(
    ImportsDbContext db,
    ImportChunkProcessor processor,
    IImportActorDirectory actors,
    IBackgroundJobs jobs,
    IAuditLog audit,
    IOutbox outbox,
    RequestContext requestContext,
    IOptions<ImportsOptions> options,
    TimeProvider clock) : IJobHandler
{
    public const string Type = "imports.execute";

    public static JobRequest Request(Guid importId, Guid runId, int checkpoint) =>
        new(Type, new ImportRunPayload(importId, runId), DedupeKey: $"{Type}:{importId:N}:{runId:N}:{checkpoint.ToString(CultureInfo.InvariantCulture)}");

    public async Task HandleAsync(JobExecution job, CancellationToken cancellationToken)
    {
        var payload = job.PayloadAs<ImportRunPayload>();
        var tenantId = requestContext.TenantId ?? throw new InvalidOperationException("Import jobs need a tenant.");
        var import = await ImportLocks.LockAsync(db, payload.ImportId, tenantId, cancellationToken);
        if (import is null || import.Status != ImportStatuses.Running || import.RunId != payload.RunId)
        {
            return; // cancelled, superseded by a newer run, or already finished
        }

        var now = ImportClock.Now(clock);
        var role = import.StartedBy is { } userId ? await actors.FindActiveRoleAsync(userId, tenantId, cancellationToken) : null;
        if (!IsAllowed(import, role))
        {
            import.Status = ImportStatuses.Failed;
            import.FailureCode = "imports.actor_not_authorized";
            import.FailureMessage = "The user who started the import no longer has the required permissions.";
            import.FinishedAt = now;
            import.Touch(null, now);
            await db.SaveChangesAsync(cancellationToken);
            await audit.RecordAsync(new AuditEvent(ImportAuditActions.Failed, "import_job", import.Id, Counts(import)), cancellationToken);
            return;
        }

        // Catalog/Pricing commands stamp created_by/updated_by from the context: the importing user.
        requestContext.UserId = import.StartedBy;
        requestContext.Role = role;

        var chunkSize = Math.Clamp(options.Value.ChunkSize, 1, 500);
        var rows = await db.Rows
            .Where(r => r.ImportJobId == import.Id && r.Status == RowStatuses.Valid && r.RowNumber > import.CheckpointRow)
            .OrderBy(r => r.RowNumber)
            .Take(chunkSize)
            .ToListAsync(cancellationToken);

        if (rows.Count > 0)
        {
            await processor.ProcessAsync(import, rows, cancellationToken);
            import.CheckpointRow = rows[^1].RowNumber;
            import.ProcessedRows += rows.Count;
        }

        if (rows.Count < chunkSize)
        {
            import.Status = ImportStatuses.Completed;
            import.FinishedAt = now;
            import.Touch(null, now);
            await db.SaveChangesAsync(cancellationToken);
            await audit.RecordAsync(new AuditEvent(ImportAuditActions.Completed, "import_job", import.Id, Counts(import)), cancellationToken);
            await outbox.PublishAsync(
                ImportEvents.Completed,
                new { importId = import.Id, type = import.Type, created = import.CreatedRows, updated = import.UpdatedRows, failed = import.FailedRows },
                cancellationToken);
            return;
        }

        import.Touch(null, now);
        await db.SaveChangesAsync(cancellationToken);
        await jobs.EnqueueAsync(Request(import.Id, payload.RunId, import.CheckpointRow), cancellationToken);
    }

    private static bool IsAllowed(ImportJob import, string? role) =>
        RolePermissions.IsGranted(role, Permissions.ImportsRun)
        && (import.Type != ImportTypes.Products || RolePermissions.IsGranted(role, Permissions.CatalogWrite))
        && ((import.Type != ImportTypes.Prices && import.PriceListId is null) || RolePermissions.IsGranted(role, Permissions.PricesWrite));

    private static IReadOnlyDictionary<string, object?> Counts(ImportJob import) => new AuditChanges()
        .Value("type", import.Type)
        .Value("processedRows", import.ProcessedRows)
        .Value("created", import.CreatedRows)
        .Value("updated", import.UpdatedRows)
        .Value("skipped", import.SkippedRows)
        .Value("failed", import.FailedRows)
        .ToDictionary();
}

/// <summary>
/// Applies one chunk through the Catalog/Pricing ports. Re-matches every row against the current catalog (the
/// preview may be stale), so the decision is always made on committed data: an existing SKU is updated, never
/// created twice. Never writes stock.
/// </summary>
internal sealed class ImportChunkProcessor(IImportCatalogGateway catalog, IImportPriceGateway prices, TimeProvider clock)
{
    public async Task ProcessAsync(ImportJob import, List<ImportRow> rows, CancellationToken cancellationToken)
    {
        var values = rows.ToDictionary(r => r, r => ImportJson.ReadValues(r.Normalized));
        var skus = values.Values.Select(v => v.GetValueOrDefault(ImportFields.Sku)).OfType<string>().Distinct().ToList();
        var barcodes = values.Values.Select(v => v.GetValueOrDefault(ImportFields.Barcode)).OfType<string>().Distinct().ToList();
        var found = await catalog.FindVariantsAsync(skus, barcodes, cancellationToken);
        var byKey = import.MatchKey == MatchKeys.Barcode
            ? found.Where(f => f.Barcode is not null).ToDictionary(f => f.Barcode!, StringComparer.Ordinal)
            : found.ToDictionary(f => f.Sku, StringComparer.Ordinal);
        var keyField = import.MatchKey == MatchKeys.Barcode ? ImportFields.Barcode : ImportFields.Sku;
        var now = ImportClock.Now(clock);

        var priceRequests = new List<(ImportRow Row, ImportPriceItem Item)>();
        foreach (var row in rows)
        {
            var v = values[row];
            var existing = v.GetValueOrDefault(keyField) is { } key ? byKey.GetValueOrDefault(key) : null;
            row.ProcessedAt = now;
            if (import.Type == ImportTypes.Prices)
            {
                if (existing is null || IsArchived(existing))
                {
                    Finish(import, row, RowOutcomes.Failed, null, null, "product.not_found", "Ürün bulunamadı veya arşivde.");
                    continue;
                }

                priceRequests.Add((row, new ImportPriceItem(existing.VariantId, Decimal(v[ImportFields.MinQuantity]) ?? 1m, Decimal(v[ImportFields.Price])!.Value)));
                continue;
            }

            CatalogWriteResult result;
            if (existing is not null)
            {
                if (IsArchived(existing) || import.Mode == ImportModes.CreateOnly)
                {
                    Finish(import, row, RowOutcomes.Skipped, existing.VariantId, null);
                    continue;
                }

                result = await catalog.UpdateProductAsync(existing, ProductValues(v), cancellationToken);
            }
            else if (import.Mode == ImportModes.UpdateOnly)
            {
                Finish(import, row, RowOutcomes.Skipped, null, null);
                continue;
            }
            else
            {
                result = await catalog.CreateProductAsync(ProductValues(v), cancellationToken);
            }

            if (result.Outcome == RowOutcomes.Failed)
            {
                var messages = result.Errors?.SelectMany(e => e.Value.Select(m => $"{e.Key}: {m}")) ?? [];
                Finish(import, row, RowOutcomes.Failed, result.VariantId, null, result.ErrorCode ?? "catalog.rejected", string.Join(" ", messages));
                continue;
            }

            row.Outcome = result.Outcome;
            row.TargetId = result.VariantId;
            row.PreviousValues = result.PreviousValuesJson;
            if (import.PriceListId is not null && Decimal(v.GetValueOrDefault(ImportFields.Price)) is { } price)
            {
                priceRequests.Add((row, new ImportPriceItem(result.VariantId!.Value, 1m, price)));
            }
            else
            {
                Finish(import, row, result.Outcome, result.VariantId, result.PreviousValuesJson);
            }
        }

        if (priceRequests.Count > 0)
        {
            await ApplyPricesAsync(import, priceRequests, cancellationToken);
        }
    }

    private async Task ApplyPricesAsync(ImportJob import, List<(ImportRow Row, ImportPriceItem Item)> requests, CancellationToken cancellationToken)
    {
        var listId = import.PriceListId!.Value;
        var current = await prices.FindPricesAsync(listId, [.. requests.Select(r => r.Item.VariantId).Distinct()], cancellationToken);
        var toWrite = new List<(ImportRow Row, ImportPriceItem Item, decimal? Previous)>();
        foreach (var (row, item) in requests)
        {
            var exists = current.TryGetValue((item.VariantId, item.MinQuantity), out var previous);
            var productOutcome = import.Type == ImportTypes.Products ? row.Outcome : null;
            if ((exists && previous == item.UnitPrice)
                || (import.Type == ImportTypes.Prices && exists && import.Mode == ImportModes.CreateOnly)
                || (import.Type == ImportTypes.Prices && !exists && import.Mode == ImportModes.UpdateOnly))
            {
                var outcome = productOutcome ?? (exists && previous == item.UnitPrice ? RowOutcomes.Unchanged : RowOutcomes.Skipped);
                Finish(import, row, outcome, row.TargetId ?? item.VariantId, row.PreviousValues);
                continue;
            }

            toWrite.Add((row, item, exists ? previous : null));
        }

        if (toWrite.Count == 0)
        {
            return;
        }

        var result = await prices.UpsertPricesAsync(listId, [.. toWrite.Select(w => w.Item)], cancellationToken);
        for (var i = 0; i < toWrite.Count; i++)
        {
            var (row, item, previous) = toWrite[i];
            if (result.ItemIds[i] is null)
            {
                var message = result.Errors.TryGetValue(i, out var itemErrors) ? string.Join(" ", itemErrors) : result.BatchError ?? "Price list rejected the item.";
                Finish(import, row, RowOutcomes.Failed, row.TargetId, row.PreviousValues, "price.rejected", message);
                continue;
            }

            if (import.Type == ImportTypes.Prices)
            {
                var previousJson = previous is { } p ? ImportJson.Serialize(new Dictionary<string, decimal> { ["unitPrice"] = p }) : null;
                Finish(import, row, previous is null ? RowOutcomes.Created : RowOutcomes.Updated, result.ItemIds[i], previousJson);
            }
            else
            {
                // Product row whose price changed: at least an update even if the product itself was unchanged.
                var outcome = row.Outcome == RowOutcomes.Unchanged ? RowOutcomes.Updated : row.Outcome!;
                Finish(import, row, outcome, row.TargetId, MergePrevious(row.PreviousValues, previous));
            }
        }
    }

    private static string? MergePrevious(string? productPrevious, decimal? previousPrice)
    {
        if (previousPrice is null)
        {
            return productPrevious;
        }

        var merged = productPrevious is null
            ? new Dictionary<string, object?>()
            : System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object?>>(productPrevious, ImportJson.Options) ?? [];
        merged["unitPrice"] = previousPrice;
        return ImportJson.Serialize(merged);
    }

    private static void Finish(
        ImportJob import, ImportRow row, string outcome, Guid? targetId, string? previousValues, string? errorCode = null, string? message = null)
    {
        row.Outcome = outcome;
        row.TargetId = targetId;
        row.PreviousValues = previousValues;
        row.Status = outcome switch
        {
            RowOutcomes.Created or RowOutcomes.Updated => RowStatuses.Imported,
            RowOutcomes.Failed => RowStatuses.Failed,
            _ => RowStatuses.Skipped,
        };
        switch (outcome)
        {
            case RowOutcomes.Created:
                import.CreatedRows++;
                break;
            case RowOutcomes.Updated:
                import.UpdatedRows++;
                break;
            case RowOutcomes.Failed:
                import.FailedRows++;
                break;
            default:
                import.SkippedRows++;
                break;
        }

        if (errorCode is not null)
        {
            var errors = ImportJson.ReadErrors(row.Errors);
            errors.Add(new ImportRowError(null, null, errorCode, string.IsNullOrWhiteSpace(message) ? "Kayıt reddedildi." : message));
            row.Errors = ImportJson.Serialize(errors);
        }
    }

    private static bool IsArchived(CatalogVariantSnapshot v) => v.ProductStatus == "archived" || v.VariantStatus == "archived";

    private static decimal? Decimal(string? value) => value is null ? null : decimal.Parse(value, CultureInfo.InvariantCulture);

    private static Guid? Id(string? value) => value is null ? null : Guid.Parse(value);

    private static ImportProductValues ProductValues(IReadOnlyDictionary<string, string?> v) => new(
        v.GetValueOrDefault(ImportFields.Sku),
        v.GetValueOrDefault(ImportFields.Barcode),
        v.GetValueOrDefault(ImportFields.Name),
        v.GetValueOrDefault(ImportFields.Description),
        Id(v.GetValueOrDefault(ResolvedKeys.UnitId)),
        Decimal(v.GetValueOrDefault(ImportFields.TaxRate)),
        Id(v.GetValueOrDefault(ResolvedKeys.BrandId)),
        Id(v.GetValueOrDefault(ResolvedKeys.CategoryId)),
        v.GetValueOrDefault(ImportFields.Status));
}
