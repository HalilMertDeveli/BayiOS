using System.Globalization;
using System.Text.Json.Nodes;
using BayiOS.Modules.Imports.Contracts;
using BayiOS.Modules.Imports.Domain;
using BayiOS.Modules.Imports.Infrastructure.Persistence;
using BayiOS.SharedKernel.Authorization;
using BayiOS.SharedKernel.Platform.Audit;
using BayiOS.SharedKernel.Platform.Jobs;
using BayiOS.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BayiOS.Modules.Imports.Application;

/// <summary>Keys of <c>previous_values</c> / <c>applied_values</c> besides the catalog field keys.</summary>
internal static class RollbackKeys
{
    /// <summary>The list price for (variant, min quantity); JSON null in previous values = the import created the item.</summary>
    public const string UnitPrice = "unitPrice";
}

/// <summary>
/// <c>imports.rollback</c> (PRD IMP-12): undoes the next chunk of imported rows in this job's transaction, then
/// enqueues the next chunk (same transaction) or finishes the rollback. A row whose <c>rollback_status</c> is set is
/// never handled again (rollback checkpoint), so a retry or a resume does not undo twice. Runs with the permissions of
/// the user who requested the rollback, re-checked on every chunk. Never touches stock and never reads the raw file.
/// </summary>
internal sealed class RollbackImportJob(
    ImportsDbContext db,
    ImportRollbackProcessor processor,
    IImportActorDirectory actors,
    IBackgroundJobs jobs,
    IAuditLog audit,
    IOutbox outbox,
    RequestContext requestContext,
    IOptions<ImportsOptions> options,
    TimeProvider clock) : IJobHandler
{
    public const string Type = "imports.rollback";

    public static JobRequest Request(Guid importId, Guid runId, int handled) =>
        new(Type, new ImportRunPayload(importId, runId), DedupeKey: $"{Type}:{importId:N}:{runId:N}:{handled.ToString(CultureInfo.InvariantCulture)}");

    public async Task HandleAsync(JobExecution job, CancellationToken cancellationToken)
    {
        var payload = job.PayloadAs<ImportRunPayload>();
        var tenantId = requestContext.TenantId ?? throw new InvalidOperationException("Import jobs need a tenant.");
        var import = await ImportLocks.LockAsync(db, payload.ImportId, tenantId, cancellationToken);
        if (import is null || import.Status != ImportStatuses.RollingBack || import.RunId != payload.RunId)
        {
            return; // superseded, stalled-and-failed, or already finished
        }

        var now = ImportClock.Now(clock);
        var role = import.RollbackRequestedBy is { } userId ? await actors.FindActiveRoleAsync(userId, tenantId, cancellationToken) : null;
        if (ImportPermissions.RollbackDenied(import, role) is not null)
        {
            import.Status = ImportStatuses.RollbackFailed;
            import.RunId = null;
            import.FailureCode = "imports.actor_not_authorized";
            import.FailureMessage = "The user who requested the rollback no longer has the required permissions.";
            import.Touch(null, now);
            await db.SaveChangesAsync(cancellationToken);
            await audit.RecordAsync(new AuditEvent(ImportAuditActions.RollbackFailed, "import_job", import.Id, Counts(import)), cancellationToken);
            return;
        }

        // Catalog/Pricing commands stamp updated_by from the context: the user who requested the rollback.
        requestContext.UserId = import.RollbackRequestedBy;
        requestContext.Role = role;

        var chunkSize = Math.Clamp(options.Value.ChunkSize, 1, 500);
        var rows = await db.Rows
            .Where(r => r.ImportJobId == import.Id && r.Status == RowStatuses.Imported && r.RollbackStatus == null)
            .OrderBy(r => r.RowNumber)
            .Take(chunkSize)
            .ToListAsync(cancellationToken);

        if (rows.Count > 0)
        {
            await processor.ProcessAsync(import, rows, cancellationToken);
        }

        if (rows.Count < chunkSize)
        {
            import.Status = import.RollbackConflictRows + import.RollbackFailedRows == 0 ? ImportStatuses.RolledBack : ImportStatuses.PartiallyRolledBack;
            import.RunId = null;
            import.RollbackFinishedAt = now;
            import.Touch(null, now);
            await db.SaveChangesAsync(cancellationToken);
            await audit.RecordAsync(new AuditEvent(ImportAuditActions.RolledBack, "import_job", import.Id, Counts(import)), cancellationToken);
            await outbox.PublishAsync(
                ImportEvents.RolledBack,
                new
                {
                    importId = import.Id,
                    type = import.Type,
                    status = import.Status,
                    rolledBack = import.RolledBackRows,
                    conflicts = import.RollbackConflictRows,
                    failed = import.RollbackFailedRows,
                    skipped = import.RollbackSkippedRows,
                },
                cancellationToken);
            return;
        }

        import.Touch(null, now);
        await db.SaveChangesAsync(cancellationToken);
        await jobs.EnqueueAsync(Request(import.Id, payload.RunId, import.RollbackProcessedRows), cancellationToken);
    }

    internal static IReadOnlyDictionary<string, object?> Counts(ImportJob import) => new AuditChanges()
        .Value("type", import.Type)
        .Value("status", import.Status)
        .Value("rolledBack", import.RolledBackRows)
        .Value("conflicts", import.RollbackConflictRows)
        .Value("failed", import.RollbackFailedRows)
        .Value("skipped", import.RollbackSkippedRows)
        .ToDictionary();
}

/// <summary>
/// Undoes one chunk of imported rows through the Catalog/Pricing ports (PRD IMP-12):
/// <list type="bullet">
/// <item>created product → the product is archived (the catalog is archive-only); a price item the import created for
/// it is deleted while it still holds the imported price;</item>
/// <item>updated product → the changed fields are written back from <c>previous_values</c>, and the price restored
/// (or the item the import created deleted) — only if the record still holds exactly what the import stored
/// (<c>applied_values</c>). Any later change makes the whole row a <c>conflict</c>: it is skipped, never overwritten;</item>
/// <item>price rows → the item is restored to its previous price, or deleted if the import created it, under the same
/// "still the imported price" rule.</item>
/// </list>
/// Stock is never touched (imports never write it).
/// </summary>
internal sealed class ImportRollbackProcessor(IImportCatalogGateway catalog, IImportPriceGateway prices, TimeProvider clock)
{
    private static readonly HashSet<string> ConflictCodes = new(StringComparer.Ordinal)
    {
        "concurrency.version_mismatch", "catalog.sku_taken", "catalog.barcode_taken",
    };

    public async Task ProcessAsync(ImportJob import, List<ImportRow> rows, CancellationToken cancellationToken)
    {
        var decisions = new Dictionary<ImportRow, Decision>();
        if (import.Type == ImportTypes.Prices)
        {
            await PricesAsync(import, rows, decisions, cancellationToken);
        }
        else
        {
            await ProductsAsync(import, rows, decisions, cancellationToken);
        }

        var now = ImportClock.Now(clock);
        foreach (var row in rows)
        {
            var decision = decisions.GetValueOrDefault(row) ?? new Decision(RowRollbackStatuses.Skipped, "rollback.nothing_to_undo", "Geri alınacak bir şey yok.");
            Apply(import, row, decision, now);
        }
    }

    // ---------------------------------------------------------------- products

    private async Task ProductsAsync(ImportJob import, List<ImportRow> rows, Dictionary<ImportRow, Decision> decisions, CancellationToken cancellationToken)
    {
        var variantIds = rows.Where(r => r.TargetId is not null).Select(r => r.TargetId!.Value).Distinct().ToList();
        var snapshots = (await catalog.FindVariantsByIdAsync(variantIds, cancellationToken)).ToDictionary(s => s.VariantId);
        var items = import.PriceListId is { } listId
            ? await prices.FindPriceItemsAsync(listId, variantIds, cancellationToken)
            : new Dictionary<(Guid, decimal), ImportPriceItemInfo>();
        var priceOps = new List<PriceOp>();

        foreach (var row in rows)
        {
            if (row.TargetId is not { } variantId || !snapshots.TryGetValue(variantId, out var current))
            {
                decisions[row] = Decision.Skip("rollback.not_found", "Kayıt artık yok.");
                continue;
            }

            var previous = Read(row.PreviousValues);
            var applied = Read(row.AppliedValues);
            var writesPrice = import.PriceListId is not null && applied.ContainsKey(RollbackKeys.UnitPrice);
            var appliedPrice = writesPrice ? Price(applied[RollbackKeys.UnitPrice]) : null;
            var item = writesPrice ? items.GetValueOrDefault((variantId, 1m)) : null;

            if (row.Outcome == RowOutcomes.Created)
            {
                var archived = await catalog.ArchiveProductAsync(current, cancellationToken);
                if (archived.Outcome == RowOutcomes.Failed)
                {
                    decisions[row] = CatalogFailure(archived);
                    continue;
                }

                // The product is archived either way; a later-edited price on it is left as it is.
                var deletesPrice = item is not null && item.UnitPrice == appliedPrice;
                if (deletesPrice)
                {
                    priceOps.Add(new PriceOp(row, item!.ItemId, null));
                }

                decisions[row] = archived.Outcome == "archived" || deletesPrice
                    ? Decision.Done
                    : Decision.Skip("rollback.already_archived", "Ürün zaten arşivde.");
                continue;
            }

            if (current.ProductStatus == "archived" || current.VariantStatus == "archived")
            {
                decisions[row] = Decision.Conflict("Ürün içe aktarmadan sonra arşivlenmiş; geri alınmadı.");
                continue;
            }

            var catalogKeys = previous.Select(p => p.Key).Where(k => k != RollbackKeys.UnitPrice).ToList();
            if (catalogKeys.Count > 0 && catalogKeys.Any(k => !applied.ContainsKey(k)))
            {
                decisions[row] = new Decision(RowRollbackStatuses.Failed, "rollback.no_snapshot", "İçe aktarılan değerler kayıtlı değil; satır geri alınamaz.");
                continue;
            }

            var live = ImportCatalogFields.Read(current);
            if (catalogKeys.FirstOrDefault(k => !ImportCatalogFields.Same(live[k], applied[k])) is { } changed)
            {
                decisions[row] = Decision.Conflict($"'{changed}' içe aktarmadan sonra değişmiş; satır geri alınmadı.");
                continue;
            }

            var previousPrice = writesPrice ? Price(previous[RollbackKeys.UnitPrice]) : null;
            if (writesPrice && item is null && previousPrice is not null)
            {
                decisions[row] = Decision.Conflict("Fiyat içe aktarmadan sonra silinmiş; satır geri alınmadı.");
                continue;
            }

            if (writesPrice && item is not null && item.UnitPrice != appliedPrice)
            {
                decisions[row] = Decision.Conflict("Fiyat içe aktarmadan sonra değişmiş; satır geri alınmadı.");
                continue;
            }

            if (catalogKeys.Count > 0)
            {
                var restore = new JsonObject();
                foreach (var key in catalogKeys)
                {
                    restore[key] = previous[key]?.DeepClone();
                }

                var restored = await catalog.RestoreProductAsync(current, restore.ToJsonString(), cancellationToken);
                if (restored.Outcome == RowOutcomes.Failed)
                {
                    decisions[row] = CatalogFailure(restored);
                    continue;
                }
            }

            if (writesPrice && item is not null)
            {
                priceOps.Add(new PriceOp(row, item.ItemId, previousPrice is null ? null : new ImportPriceItem(variantId, 1m, previousPrice.Value)));
            }

            decisions[row] = Decision.Done;
        }

        await ApplyPriceOpsAsync(import, priceOps, decisions, cancellationToken);
    }

    // ---------------------------------------------------------------- prices

    private async Task PricesAsync(ImportJob import, List<ImportRow> rows, Dictionary<ImportRow, Decision> decisions, CancellationToken cancellationToken)
    {
        var listId = import.PriceListId!.Value;
        var keys = rows.ToDictionary(r => r, r =>
        {
            var v = ImportJson.ReadValues(r.Normalized);
            return v.GetValueOrDefault(ResolvedKeys.VariantId) is { } id
                ? ((Guid VariantId, decimal MinQuantity)?)(Guid.Parse(id, CultureInfo.InvariantCulture), decimal.Parse(v[ImportFields.MinQuantity]!, CultureInfo.InvariantCulture))
                : null;
        });
        var items = await prices.FindPriceItemsAsync(listId, [.. keys.Values.OfType<(Guid VariantId, decimal MinQuantity)>().Select(k => k.VariantId).Distinct()], cancellationToken);
        var priceOps = new List<PriceOp>();
        foreach (var row in rows)
        {
            if (keys[row] is not { } key)
            {
                decisions[row] = Decision.Skip("rollback.not_found", "Kayıt artık yok.");
                continue;
            }

            var applied = Read(row.AppliedValues);
            var appliedPrice = applied.ContainsKey(RollbackKeys.UnitPrice)
                ? Price(applied[RollbackKeys.UnitPrice])
                : decimal.Parse(ImportJson.ReadValues(row.Normalized)[ImportFields.Price]!, CultureInfo.InvariantCulture);
            var item = items.GetValueOrDefault(key);
            if (row.Outcome == RowOutcomes.Created)
            {
                if (item is null)
                {
                    decisions[row] = Decision.Skip("rollback.already_removed", "Fiyat zaten silinmiş.");
                }
                else if (item.UnitPrice != appliedPrice)
                {
                    decisions[row] = Decision.Conflict("Fiyat içe aktarmadan sonra değişmiş; satır geri alınmadı.");
                }
                else
                {
                    priceOps.Add(new PriceOp(row, item.ItemId, null));
                    decisions[row] = Decision.Done;
                }

                continue;
            }

            var previousPrice = Price(Read(row.PreviousValues)[RollbackKeys.UnitPrice]);
            if (item is null || previousPrice is null)
            {
                decisions[row] = Decision.Conflict("Fiyat içe aktarmadan sonra silinmiş; satır geri alınmadı.");
            }
            else if (item.UnitPrice != appliedPrice)
            {
                decisions[row] = Decision.Conflict("Fiyat içe aktarmadan sonra değişmiş; satır geri alınmadı.");
            }
            else
            {
                priceOps.Add(new PriceOp(row, item.ItemId, new ImportPriceItem(key.VariantId, key.MinQuantity, previousPrice.Value)));
                decisions[row] = Decision.Done;
            }
        }

        await ApplyPriceOpsAsync(import, priceOps, decisions, cancellationToken);
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>One batched upsert for restored prices, one delete per item the import created.</summary>
    private async Task ApplyPriceOpsAsync(ImportJob import, List<PriceOp> ops, Dictionary<ImportRow, Decision> decisions, CancellationToken cancellationToken)
    {
        if (ops.Count == 0)
        {
            return;
        }

        var listId = import.PriceListId!.Value;
        var upserts = ops.Where(o => o.Restore is not null).ToList();
        if (upserts.Count > 0)
        {
            var result = await prices.UpsertPricesAsync(listId, [.. upserts.Select(o => o.Restore!)], cancellationToken);
            for (var i = 0; i < upserts.Count; i++)
            {
                if (result.ItemIds[i] is null)
                {
                    var message = result.Errors.TryGetValue(i, out var errors) ? string.Join(" ", errors) : result.BatchError ?? "Price list rejected the item.";
                    decisions[upserts[i].Row] = new Decision(RowRollbackStatuses.Failed, "rollback.price_rejected", message);
                }
            }
        }

        foreach (var op in ops.Where(o => o.Restore is null))
        {
            if (await prices.DeletePriceItemAsync(listId, op.ItemId, cancellationToken) is { } error)
            {
                decisions[op.Row] = new Decision(RowRollbackStatuses.Failed, "rollback.price_rejected", error);
            }
        }
    }

    private static Decision CatalogFailure(CatalogWriteResult result)
    {
        var messages = result.Errors?.SelectMany(e => e.Value.Select(m => string.IsNullOrEmpty(e.Key) ? m : $"{e.Key}: {m}")) ?? [];
        var message = string.Join(" ", messages);
        return result.ErrorCode is { } code && ConflictCodes.Contains(code)
            ? Decision.Conflict($"Kayıt içe aktarmadan sonra değişmiş veya değer başka kayıtta kullanılıyor ({code}); satır geri alınmadı.")
            : new Decision(RowRollbackStatuses.Failed, result.ErrorCode ?? "rollback.rejected", string.IsNullOrWhiteSpace(message) ? "Kayıt reddedildi." : message);
    }

    private static void Apply(ImportJob import, ImportRow row, Decision decision, DateTimeOffset now)
    {
        row.RollbackStatus = decision.Status;
        row.RolledBackAt = now;
        switch (decision.Status)
        {
            case RowRollbackStatuses.RolledBack:
                import.RolledBackRows++;
                break;
            case RowRollbackStatuses.Conflict:
                import.RollbackConflictRows++;
                break;
            case RowRollbackStatuses.Failed:
                import.RollbackFailedRows++;
                break;
            default:
                import.RollbackSkippedRows++;
                break;
        }

        if (decision.Code is not null)
        {
            var errors = ImportJson.ReadErrors(row.Errors);
            errors.Add(new ImportRowError(null, null, decision.Code, decision.Message ?? string.Empty));
            row.Errors = ImportJson.Serialize(errors);
        }
    }

    private static JsonObject Read(string? json) => json is null ? [] : JsonNode.Parse(json) as JsonObject ?? [];

    private static decimal? Price(JsonNode? node) => node is null ? null : node.GetValue<decimal>();

    private sealed record PriceOp(ImportRow Row, Guid ItemId, ImportPriceItem? Restore);

    private sealed record Decision(string Status, string? Code, string? Message)
    {
        public static readonly Decision Done = new(RowRollbackStatuses.RolledBack, null, null);

        public static Decision Skip(string code, string message) => new(RowRollbackStatuses.Skipped, code, message);

        public static Decision Conflict(string message) => new(RowRollbackStatuses.Conflict, "rollback.conflict", message);
    }
}
