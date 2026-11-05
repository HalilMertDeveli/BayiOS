using System.Globalization;
using System.Security.Cryptography;
using System.Text;
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
using Npgsql;

namespace BayiOS.Modules.Imports.Application;

/// <summary>Payload of the <c>imports.validate</c> and <c>imports.execute</c> jobs (ids only, ADR 0006).</summary>
internal sealed record ImportRunPayload(Guid ImportId, Guid RunId);

/// <summary>
/// Import commands and queries (PRD IMP-01–IMP-13, api-strategy §12.9). Every state transition locks the import row
/// (<c>FOR UPDATE</c>) so it serialises with the background chunk that may hold the same lock: cancel either lands
/// before a chunk (the chunk then does nothing) or after it commits — never in the middle.
/// </summary>
internal sealed class ImportService(
    ImportsDbContext db,
    IImportFileStore files,
    IBackgroundJobs jobs,
    IAuditLog audit,
    IImportPriceGateway priceGateway,
    IImportCatalogGateway catalogGateway,
    IColumnSuggestionProvider suggestions,
    IRequestContext requestContext,
    IOptions<ImportsOptions> options,
    TimeProvider clock)
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 200;
    public const int MaxTemplates = 200;

    private Guid UserId => requestContext.UserId ?? throw new InvalidOperationException("No authenticated user.");

    private Guid TenantId => requestContext.TenantId ?? throw new InvalidOperationException("No tenant resolved.");

    // ---- Upload ----

    public async Task<AppResult<ImportJobResponse>> UploadAsync(
        string? type, string fileName, string? contentType, byte[] content, CancellationToken cancellationToken)
    {
        if (type is null || !ImportTypes.All.Contains(type))
        {
            return AppError.Validation(new Dictionary<string, string[]> { ["type"] = [$"Must be one of: {string.Join(", ", ImportTypes.All)}."] });
        }

        if (PermissionFor(type, mapsPrice: type == ImportTypes.Prices) is { } denied)
        {
            return denied;
        }

        if (await CheckUploadRateAsync(cancellationToken) is { } limited)
        {
            return limited;
        }

        var table = TabularFileReader.Read(content, fileName, contentType, cancellationToken);
        var sha = Convert.ToHexStringLower(SHA256.HashData(content));
        var duplicateOf = await db.Jobs.AsNoTracking()
            .Where(j => j.FileSha256 == sha && j.Type == type && j.Status == ImportStatuses.Completed)
            .OrderByDescending(j => j.CreatedAt)
            .Select(j => (Guid?)j.Id)
            .FirstOrDefaultAsync(cancellationToken);

        var now = ImportClock.Now(clock);
        var job = ImportJob.Create(
            TenantId,
            type,
            fileName,
            content.LongLength,
            sha,
            table.Format,
            table.Encoding,
            table.Delimiter,
            table.SheetName,
            ImportJson.Serialize(table.Headers),
            ImportJson.Serialize(table.Rows.Take(ImportLimits.SampleRowCount).Select(r => r.Cells)),
            table.Rows.Count,
            duplicateOf,
            UserId,
            now);
        db.Jobs.Add(job);
        await db.SaveChangesAsync(cancellationToken);
        await files.SaveAsync(TenantId, job.Id, content, cancellationToken);
        return ToResponse(job);
    }

    /// <summary>
    /// Per-tenant upload limit (threat-model §6.5: 10/hour, sliding window). The count is the tenant's own
    /// <c>import_jobs</c> rows in the database, so every API instance shares it; a transaction-scoped advisory lock per
    /// tenant serialises concurrent uploads so two instances cannot both take the last slot. Rejected (invalid) files
    /// create no row and do not count.
    /// </summary>
    private async Task<AppError?> CheckUploadRateAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (settings.UploadLimit <= 0)
        {
            return null;
        }

        await db.JoinTransactionAsync(cancellationToken);
        var lockKey = $"imports.upload:{TenantId:N}";
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({lockKey}, 0))", cancellationToken);

        var now = ImportClock.Now(clock);
        var since = now - settings.UploadWindow;
        var recent = await db.Jobs.AsNoTracking()
            .Where(j => j.CreatedAt > since)
            .OrderByDescending(j => j.CreatedAt)
            .Select(j => j.CreatedAt)
            .Take(settings.UploadLimit)
            .ToListAsync(cancellationToken);
        if (recent.Count < settings.UploadLimit)
        {
            return null;
        }

        // A slot frees up when the oldest of the last <limit> uploads leaves the window.
        var retryAfter = (int)Math.Ceiling((recent[^1] + settings.UploadWindow - now).TotalSeconds);
        return AppError.RateLimited(
            $"Upload limit reached: {settings.UploadLimit.ToString(CultureInfo.InvariantCulture)} files per {settings.UploadWindow.TotalMinutes.ToString(CultureInfo.InvariantCulture)} minutes for this tenant",
            Math.Max(1, retryAfter));
    }

    // ---- Queries ----

    public async Task<ImportJobPageResponse> ListAsync(int limit, Guid? before, CancellationToken cancellationToken)
    {
        var query = db.Jobs.AsNoTracking();
        if (before is { } id)
        {
            query = query.Where(j => j.Id.CompareTo(id) < 0);
        }

        var page = await query.OrderByDescending(j => j.Id).Take(limit + 1).ToListAsync(cancellationToken);
        var hasMore = page.Count > limit;
        if (hasMore)
        {
            page.RemoveAt(page.Count - 1);
        }

        return new ImportJobPageResponse(
            page.Select(ToResponse).ToList(), new ImportPageInfo(hasMore ? ImportCursor.Encode(page[^1].Id) : null, hasMore, limit));
    }

    public async Task<AppResult<ImportJobResponse>> GetAsync(Guid id, CancellationToken cancellationToken) =>
        await db.Jobs.AsNoTracking().SingleOrDefaultAsync(j => j.Id == id, cancellationToken) is { } job ? ToResponse(job) : AppError.NotFound();

    /// <summary>Suggestions only: nothing is applied until the client saves a mapping (PRD IMP-04).</summary>
    public async Task<AppResult<MappingSuggestionsResponse>> SuggestAsync(Guid id, CancellationToken cancellationToken)
    {
        if (await db.Jobs.AsNoTracking().SingleOrDefaultAsync(j => j.Id == id, cancellationToken) is not { } job)
        {
            return AppError.NotFound();
        }

        var headers = ImportJson.ReadStrings(job.Headers);
        var fields = ImportFields.For(job.Type);
        var suggested = await suggestions.SuggestAsync(
            new ColumnSuggestionContext(job.Type, headers, ImportJson.ReadRows(job.SampleRows), [.. fields.Select(f => f.ToInfo())]),
            cancellationToken);

        // Only suggestions that point at real headers and known fields, highest confidence per field.
        var known = fields.Select(f => f.Key).ToHashSet(StringComparer.Ordinal);
        var valid = suggested
            .Where(s => known.Contains(s.Field) && headers.Contains(s.Header, StringComparer.Ordinal) && s.Confidence is >= 0 and <= 1)
            .GroupBy(s => s.Field)
            .Select(g => g.OrderByDescending(s => s.Confidence).First())
            .ToList();

        var headerSet = headers.ToHashSet(StringComparer.Ordinal);
        var templates = (await db.Templates.AsNoTracking().Where(t => t.Type == job.Type).OrderBy(t => t.Name).Take(MaxTemplates).ToListAsync(cancellationToken))
            .Where(t => ImportJson.ReadMap(t.Mapping).Values.All(headerSet.Contains))
            .Select(ToResponse)
            .ToList();

        // Custom fields (attr.<key>): offered as targets; a header equal to the field's label or key is suggested.
        var attributeFields = job.Type == ImportTypes.Products ? await ActiveAttributesAsync(cancellationToken) : [];
        var usedHeaders = valid.Select(s => s.Header).ToHashSet(StringComparer.Ordinal);
        foreach (var field in attributeFields)
        {
            var header = headers.FirstOrDefault(h => !usedHeaders.Contains(h)
                && (ImportValues.Fold(h) == ImportValues.Fold(field.Label) || ImportValues.Fold(h) == ImportValues.Fold(field.Key)));
            if (header is not null)
            {
                usedHeaders.Add(header);
                valid.Add(new ColumnSuggestion(ImportCatalogFields.AttributePrefix + field.Key, header, 0.9m, "synonym"));
            }
        }

        return new MappingSuggestionsResponse(
            job.Type,
            headers,
            [
                .. fields.Select(f => new ImportFieldResponse(f.Key, f.Label, f.RequiredForCreate, f.RequiredAlways)),
                .. attributeFields.Select(f => new ImportFieldResponse(ImportCatalogFields.AttributePrefix + f.Key, f.Label, f.Required, false)),
            ],
            valid,
            templates);
    }

    /// <summary>Non-archived custom fields of the tenant (product and variant) that an import column can target.</summary>
    private async Task<List<ImportAttributeField>> ActiveAttributesAsync(CancellationToken cancellationToken) =>
        [.. (await catalogGateway.LoadAttributesAsync(cancellationToken)).Fields.Values.Where(f => !f.Archived).OrderBy(f => f.Key, StringComparer.Ordinal)];

    public async Task<AppResult<ImportRowPageResponse>> ListRowsAsync(
        Guid id, string? status, string? action, string? rollbackStatus, int limit, int afterRow, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        if (status is not null && status != "error" && !RowStatuses.All.Contains(status))
        {
            errors["status"] = [$"Must be 'error' or one of: {string.Join(", ", RowStatuses.All)}."];
        }

        if (action is not null && !RowActions.All.Contains(action))
        {
            errors["action"] = [$"Must be one of: {string.Join(", ", RowActions.All)}."];
        }

        if (rollbackStatus is not null && !RowRollbackStatuses.All.Contains(rollbackStatus))
        {
            errors["rollbackStatus"] = [$"Must be one of: {string.Join(", ", RowRollbackStatuses.All)}."];
        }

        if (errors.Count > 0)
        {
            return AppError.Validation(errors);
        }

        if (!await db.Jobs.AnyAsync(j => j.Id == id, cancellationToken))
        {
            return AppError.NotFound();
        }

        var query = db.Rows.AsNoTracking().Where(r => r.ImportJobId == id && r.RowNumber > afterRow);
        if (status == "error")
        {
            query = query.Where(r => r.Status == RowStatuses.Invalid || r.Status == RowStatuses.Failed);
        }
        else if (status is not null)
        {
            query = query.Where(r => r.Status == status);
        }

        if (action is not null)
        {
            query = query.Where(r => r.Action == action);
        }

        if (rollbackStatus is not null)
        {
            query = query.Where(r => r.RollbackStatus == rollbackStatus);
        }

        var page = await query.OrderBy(r => r.RowNumber).Take(limit + 1).ToListAsync(cancellationToken);
        var hasMore = page.Count > limit;
        if (hasMore)
        {
            page.RemoveAt(page.Count - 1);
        }

        return new ImportRowPageResponse(
            page.Select(ToResponse).ToList(), new ImportPageInfo(hasMore ? ImportCursor.Encode(page[^1].RowNumber) : null, hasMore, limit));
    }

    /// <summary>
    /// Invalid and failed rows as CSV (PRD IMP-08): original columns plus row number and errors. UTF-8 with BOM and
    /// <c>;</c> so Turkish Excel opens it directly; every value is formula-neutralised (threat-model §6.1).
    /// </summary>
    public async Task<AppResult<(string FileName, byte[] Content)>> ErrorReportAsync(Guid id, CancellationToken cancellationToken)
    {
        if (await db.Jobs.AsNoTracking().SingleOrDefaultAsync(j => j.Id == id, cancellationToken) is not { } job)
        {
            return AppError.NotFound();
        }

        const char Delimiter = ';';
        var builder = new StringBuilder();
        builder.Append(CsvSanitizer.Line(["Satır", .. ImportJson.ReadStrings(job.Headers), "Durum", "Hatalar"], Delimiter)).Append("\r\n");
        var after = 0;
        while (true)
        {
            var page = await db.Rows.AsNoTracking()
                .Where(r => r.ImportJobId == id && r.RowNumber > after && (r.Status == RowStatuses.Invalid || r.Status == RowStatuses.Failed))
                .OrderBy(r => r.RowNumber)
                .Take(1000)
                .ToListAsync(cancellationToken);
            foreach (var row in page)
            {
                var messages = ImportJson.ReadErrors(row.Errors).Select(e => e.Column is null ? e.Message : $"{e.Column}: {e.Message}");
                builder.Append(CsvSanitizer.Line(
                        [row.RowNumber.ToString(CultureInfo.InvariantCulture), .. ImportJson.ReadCells(row.Raw), row.Status, string.Join(" | ", messages)],
                        Delimiter))
                    .Append("\r\n");
            }

            if (page.Count < 1000)
            {
                break;
            }

            after = page[^1].RowNumber;
        }

        var safeName = Path.GetFileNameWithoutExtension(job.FileName);
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(builder.ToString())).ToArray();
        return ($"{safeName}-hatalar.csv", bytes);
    }

    // ---- Mapping, validation, execution ----

    public async Task<AppResult<ImportJobResponse>> SaveMappingAsync(Guid id, SaveImportMappingRequest request, CancellationToken cancellationToken)
    {
        if (await LockAsync(id, cancellationToken) is not { } job)
        {
            return AppError.NotFound();
        }

        if (!CanRemap(job))
        {
            return AppError.InvalidState(job.Status, "re-mapped");
        }

        var errors = new Dictionary<string, string[]>();
        var matchKey = request.MatchKey ?? MatchKeys.Sku;
        var mode = request.Mode ?? ImportModes.Upsert;
        var columns = ValidateColumns(
            job.Type, request.Columns, matchKey, mode, ImportJson.ReadStrings(job.Headers), await AttributeTargetsAsync(job.Type, request.Columns, cancellationToken), errors);
        var mapsPrice = columns.ContainsKey(ImportFields.Price);
        if (job.Type == ImportTypes.Prices && request.PriceListId is null)
        {
            errors["priceListId"] = ["Required for price imports."];
        }
        else if (job.Type == ImportTypes.Products && mapsPrice && request.PriceListId is null)
        {
            errors["priceListId"] = ["Required when the price column is mapped."];
        }
        else if (job.Type == ImportTypes.Products && !mapsPrice && request.PriceListId is not null)
        {
            errors["priceListId"] = ["Only allowed when the price column is mapped."];
        }

        var templateName = request.SaveAsTemplate?.Trim();
        if (request.SaveAsTemplate is not null && templateName is not { Length: >= 1 and <= 100 })
        {
            errors["saveAsTemplate"] = ["Must be 1–100 characters."];
        }

        if (errors.Count > 0)
        {
            return AppError.Validation(errors);
        }

        if (PermissionFor(job.Type, mapsPrice) is { } denied)
        {
            return denied;
        }

        if (request.PriceListId is { } listId && await CheckPriceListAsync(listId, cancellationToken) is { } listError)
        {
            return listError;
        }

        var now = ImportClock.Now(clock);
        job.Mapping = ImportJson.Serialize(columns);
        job.MatchKey = matchKey;
        job.Mode = mode;
        job.PriceListId = request.PriceListId;
        job.Status = ImportStatuses.Mapped;
        job.RunId = null;
        job.ResetPreview();
        job.Touch(UserId, now);
        await db.Rows.Where(r => r.ImportJobId == id).ExecuteDeleteAsync(cancellationToken);

        if (templateName is not null)
        {
            var mappingJson = ImportJson.Serialize(columns);
            var existing = await db.Templates.SingleOrDefaultAsync(t => t.Type == job.Type && t.Name == templateName, cancellationToken);
            if (existing is null)
            {
                db.Templates.Add(MappingTemplate.Create(TenantId, templateName, job.Type, mappingJson, matchKey, mode, UserId, now));
            }
            else
            {
                existing.Mapping = mappingJson;
                existing.MatchKey = matchKey;
                existing.Mode = mode;
                existing.Touch(UserId, now);
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        return ToResponse(job);
    }

    /// <summary>Queues the dry run (<c>imports.validate</c>); the result is polled via GET.</summary>
    public async Task<AppResult<ImportJobResponse>> ValidateAsync(Guid id, CancellationToken cancellationToken)
    {
        if (await LockAsync(id, cancellationToken) is not { } job)
        {
            return AppError.NotFound();
        }

        if (job.Mapping is null || !CanRemap(job) || job.Status == ImportStatuses.Uploaded)
        {
            return AppError.InvalidState(job.Status, "validated");
        }

        if (PermissionFor(job.Type, ImportJson.ReadMap(job.Mapping).ContainsKey(ImportFields.Price)) is { } denied)
        {
            return denied;
        }

        if (!await db.Files.AnyAsync(f => f.ImportJobId == job.Id, cancellationToken))
        {
            return AppError.Conflict("imports.file_purged", "The uploaded file was deleted after the retention period; upload it again");
        }

        job.Status = ImportStatuses.Validating;
        job.RunId = Guid.CreateVersion7();
        job.ResetPreview();
        job.Touch(UserId, ImportClock.Now(clock));
        await db.SaveChangesAsync(cancellationToken);
        await jobs.EnqueueAsync(
            new JobRequest(ValidateImportJob.Type, new ImportRunPayload(job.Id, job.RunId.Value), DedupeKey: $"{ValidateImportJob.Type}:{job.Id:N}:{job.RunId:N}"),
            cancellationToken);
        return ToResponse(job);
    }

    /// <summary>
    /// Starts — or resumes after cancel/failure — the execution (<c>imports.execute</c>). Rows already imported are
    /// never processed again (row checkpoint), and every create re-checks the SKU, so a resume cannot duplicate.
    /// </summary>
    public async Task<AppResult<ImportJobResponse>> StartAsync(Guid id, CancellationToken cancellationToken)
    {
        if (await LockAsync(id, cancellationToken) is not { } job)
        {
            return AppError.NotFound();
        }

        var resumable = job.Status is ImportStatuses.Cancelled or ImportStatuses.Failed && job.ValidatedAt is not null;
        if (job.Status != ImportStatuses.Validated && !resumable)
        {
            return AppError.InvalidState(job.Status, "started");
        }

        if (PermissionFor(job.Type, ImportJson.ReadMap(job.Mapping).ContainsKey(ImportFields.Price)) is { } denied)
        {
            return denied;
        }

        var now = ImportClock.Now(clock);
        job.Status = ImportStatuses.Running;
        job.RunId = Guid.CreateVersion7();
        job.StartedAt ??= now;
        job.StartedBy = UserId;
        job.FinishedAt = null;
        job.FailureCode = job.FailureMessage = null;
        job.Touch(UserId, now);
        await db.SaveChangesAsync(cancellationToken);
        await jobs.EnqueueAsync(ExecuteImportJob.Request(job.Id, job.RunId.Value, job.CheckpointRow), cancellationToken);
        await audit.RecordAsync(
            new AuditEvent(ImportAuditActions.Started, "import_job", job.Id, new AuditChanges()
                .Value("type", job.Type).Value("plannedRows", job.PlannedRows).Value("resumedFromRow", job.CheckpointRow).ToDictionary()),
            cancellationToken);
        return ToResponse(job);
    }

    public async Task<AppResult<ImportJobResponse>> CancelAsync(Guid id, CancellationToken cancellationToken)
    {
        if (await LockAsync(id, cancellationToken) is not { } job)
        {
            return AppError.NotFound();
        }

        // Only an import that has not finished; a rollback is never cancelled half-way (it resumes instead).
        if (job.Status is not (ImportStatuses.Uploaded or ImportStatuses.Mapped or ImportStatuses.Validating or ImportStatuses.Validated or ImportStatuses.Running))
        {
            return AppError.InvalidState(job.Status, "cancelled");
        }

        var now = ImportClock.Now(clock);
        var wasRunning = job.Status == ImportStatuses.Running;
        job.Status = ImportStatuses.Cancelled;
        job.RunId = null; // queued jobs of the old run become no-ops
        job.FinishedAt = now;
        job.Touch(UserId, now);
        await db.SaveChangesAsync(cancellationToken);
        if (wasRunning || job.StartedAt is not null)
        {
            await audit.RecordAsync(
                new AuditEvent(ImportAuditActions.Cancelled, "import_job", job.Id, new AuditChanges()
                    .Value("processedRows", job.ProcessedRows).Value("checkpointRow", job.CheckpointRow).ToDictionary()),
                cancellationToken);
        }

        return ToResponse(job);
    }

    /// <summary>
    /// Requests the rollback of a completed import (PRD IMP-12) — within <see cref="ImportsOptions.RollbackWindow"/> of
    /// its completion, once: <c>completed → rolling_back</c>, then <c>imports.rollback</c> chunks undo the imported rows
    /// (see <see cref="ImportRollbackProcessor"/>). A rollback that ended in <c>rollback_failed</c> (stalled, or the
    /// requester lost a permission) can be requested again and resumes after the rows already handled.
    /// </summary>
    public async Task<AppResult<ImportJobResponse>> RollbackAsync(Guid id, long expectedVersion, CancellationToken cancellationToken)
    {
        if (await LockAsync(id, cancellationToken) is not { } job)
        {
            return AppError.NotFound();
        }

        if (job.Status is not (ImportStatuses.Completed or ImportStatuses.RollbackFailed))
        {
            return job.Status is ImportStatuses.RollingBack or ImportStatuses.RolledBack or ImportStatuses.PartiallyRolledBack
                ? AppError.Conflict("imports.already_rolled_back", "This import is already being or has been rolled back")
                : AppError.InvalidState(job.Status, "rolled back");
        }

        if (ImportPermissions.RollbackDenied(job, requestContext.Role) is { } denied)
        {
            return denied;
        }

        if (job.RowVersion != expectedVersion)
        {
            return AppError.VersionMismatch();
        }

        var now = ImportClock.Now(clock);
        if (job.Status == ImportStatuses.Completed && job.FinishedAt + options.Value.RollbackWindow < now)
        {
            return AppError.Conflict("imports.rollback_window_expired", "The rollback period of this import has ended");
        }

        var resumed = job.Status == ImportStatuses.RollbackFailed;
        job.Status = ImportStatuses.RollingBack;
        job.RunId = Guid.CreateVersion7();
        job.RollbackRequestedAt ??= now;
        job.RollbackRequestedBy = UserId;
        job.RollbackFinishedAt = null;
        job.FailureCode = job.FailureMessage = null;
        job.Touch(UserId, now);
        await db.SaveChangesAsync(cancellationToken);
        await jobs.EnqueueAsync(RollbackImportJob.Request(job.Id, job.RunId.Value, job.RollbackProcessedRows), cancellationToken);
        await audit.RecordAsync(
            new AuditEvent(ImportAuditActions.RollbackRequested, "import_job", job.Id, new AuditChanges()
                .Value("type", job.Type)
                .Value("importedRows", job.CreatedRows + job.UpdatedRows)
                .Value("resumedAfterRows", resumed ? job.RollbackProcessedRows : 0)
                .ToDictionary()),
            cancellationToken);
        return ToResponse(job);
    }

    // ---- Templates ----

    public async Task<IReadOnlyList<MappingTemplateResponse>> ListTemplatesAsync(string? type, CancellationToken cancellationToken) =>
        (await db.Templates.AsNoTracking()
            .Where(t => type == null || t.Type == type)
            .OrderBy(t => t.Type).ThenBy(t => t.Name)
            .Take(MaxTemplates)
            .ToListAsync(cancellationToken))
        .Select(ToResponse)
        .ToList();

    public async Task<AppResult<MappingTemplateResponse>> GetTemplateAsync(Guid id, CancellationToken cancellationToken) =>
        await db.Templates.AsNoTracking().SingleOrDefaultAsync(t => t.Id == id, cancellationToken) is { } template ? ToResponse(template) : AppError.NotFound();

    public async Task<AppResult<MappingTemplateResponse>> CreateTemplateAsync(SaveMappingTemplateRequest request, CancellationToken cancellationToken)
    {
        var (name, type, columns, matchKey, mode, errors) = ValidateTemplate(request, await AttributeTargetsAsync(request.Type, request.Columns, cancellationToken));
        if (errors.Count > 0)
        {
            return AppError.Validation(errors);
        }

        var template = MappingTemplate.Create(TenantId, name!, type!, ImportJson.Serialize(columns), matchKey, mode, UserId, ImportClock.Now(clock));
        db.Templates.Add(template);
        return await SaveTemplateAsync(template, cancellationToken);
    }

    public async Task<AppResult<MappingTemplateResponse>> UpdateTemplateAsync(
        Guid id, SaveMappingTemplateRequest request, long expectedVersion, CancellationToken cancellationToken)
    {
        if (await db.Templates.SingleOrDefaultAsync(t => t.Id == id, cancellationToken) is not { } template)
        {
            return AppError.NotFound();
        }

        var typed = request with { Type = request.Type ?? template.Type };
        var (name, type, columns, matchKey, mode, errors) = ValidateTemplate(typed, await AttributeTargetsAsync(typed.Type, typed.Columns, cancellationToken));
        if (type != template.Type)
        {
            errors["type"] = ["The type of a template cannot change."];
        }

        if (errors.Count > 0)
        {
            return AppError.Validation(errors);
        }

        if (template.RowVersion != expectedVersion)
        {
            return AppError.VersionMismatch();
        }

        template.Name = name!;
        template.Mapping = ImportJson.Serialize(columns);
        template.MatchKey = matchKey;
        template.Mode = mode;
        template.Touch(UserId, ImportClock.Now(clock));
        return await SaveTemplateAsync(template, cancellationToken);
    }

    public async Task<AppError?> DeleteTemplateAsync(Guid id, CancellationToken cancellationToken)
    {
        if (await db.Templates.SingleOrDefaultAsync(t => t.Id == id, cancellationToken) is not { } template)
        {
            return AppError.NotFound();
        }

        db.Templates.Remove(template);
        await db.SaveChangesAsync(cancellationToken);
        return null;
    }

    // ---- Helpers ----

    /// <summary>The import row with a row lock for the rest of the transaction (tenant from the request; RLS too).</summary>
    private async Task<ImportJob?> LockAsync(Guid id, CancellationToken cancellationToken)
    {
        await db.JoinTransactionAsync(cancellationToken);
        var tenantId = TenantId;
        return (await db.Jobs
                .FromSql($"SELECT * FROM imports.import_jobs WHERE id = {id} AND tenant_id = {tenantId} FOR UPDATE")
                .IgnoreQueryFilters()
                .ToListAsync(cancellationToken))
            .SingleOrDefault();
    }

    /// <summary>Mapping (and re-validation) only before any row was executed.</summary>
    private static bool CanRemap(ImportJob job) =>
        job.StartedAt is null && job.ProcessedRows == 0
        && job.Status is ImportStatuses.Uploaded or ImportStatuses.Mapped or ImportStatuses.Validated or ImportStatuses.Failed or ImportStatuses.Cancelled;

    /// <summary>
    /// Defence in depth on top of <c>imports.run</c>: a product import writes the catalog (<c>catalog.write</c>), any
    /// price column writes a price list (<c>prices.write</c>).
    /// </summary>
    private AppError? PermissionFor(string type, bool mapsPrice) => ImportPermissions.RunDenied(requestContext.Role, type, mapsPrice);

    /// <summary>
    /// The tenant's custom fields when the request maps any <c>attr.&lt;key&gt;</c> column of a product import (one
    /// Catalog read); null otherwise.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, ImportAttributeField>?> AttributeTargetsAsync(
        string? type, IReadOnlyDictionary<string, string>? columns, CancellationToken cancellationToken) =>
        type == ImportTypes.Products && columns?.Keys.Any(k => k.StartsWith(ImportCatalogFields.AttributePrefix, StringComparison.Ordinal)) == true
            ? (await catalogGateway.LoadAttributesAsync(cancellationToken)).Fields
            : null;

    private async Task<AppError?> CheckPriceListAsync(Guid priceListId, CancellationToken cancellationToken)
    {
        var list = await priceGateway.FindPriceListAsync(priceListId, cancellationToken);
        var visible = list is not null && (list.Kind == "sales" || RolePermissions.IsGranted(requestContext.Role, Permissions.PricesReadCost));
        if (!visible)
        {
            return AppError.Validation(new Dictionary<string, string[]> { ["priceListId"] = ["Price list not found."] });
        }

        return list!.Status == "archived" ? AppError.Conflict("pricing.list_archived", "Archived price lists cannot change") : null;
    }

    private static Dictionary<string, string> ValidateColumns(
        string type,
        IReadOnlyDictionary<string, string>? requested,
        string matchKey,
        string mode,
        IReadOnlyList<string>? headers,
        IReadOnlyDictionary<string, ImportAttributeField>? attributes,
        Dictionary<string, string[]> errors)
    {
        var fields = ImportFields.For(type);
        var columns = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!MatchKeys.All.Contains(matchKey))
        {
            errors["matchKey"] = [$"Must be one of: {string.Join(", ", MatchKeys.All)}."];
        }

        if (!ImportModes.All.Contains(mode))
        {
            errors["mode"] = [$"Must be one of: {string.Join(", ", ImportModes.All)}."];
        }

        if (requested is null || requested.Count == 0)
        {
            errors["columns"] = ["Map at least the match key column."];
            return columns;
        }

        var usedHeaders = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (field, header) in requested)
        {
            if (field.StartsWith(ImportCatalogFields.AttributePrefix, StringComparison.Ordinal))
            {
                var key = field[ImportCatalogFields.AttributePrefix.Length..];
                if (type != ImportTypes.Products)
                {
                    errors[$"columns.{field}"] = ["Custom field columns are only allowed in product imports."];
                    continue;
                }

                if (attributes is null || !attributes.TryGetValue(key, out var attribute) || attribute.Archived)
                {
                    errors[$"columns.{field}"] = ["Unknown or archived custom field."];
                    continue;
                }
            }
            else if (!fields.Any(f => f.Key == field))
            {
                errors[$"columns.{field}"] = [$"Unknown field. Fields: {string.Join(", ", fields.Select(f => f.Key))}, attr.<custom field key>."];
                continue;
            }

            if (string.IsNullOrEmpty(header) || (headers is not null && !headers.Contains(header, StringComparer.Ordinal)))
            {
                errors[$"columns.{field}"] = ["Must be one of the file's headers."];
            }
            else if (header.Length > ImportLimits.MaxHeaderLength || !usedHeaders.Add(header))
            {
                errors[$"columns.{field}"] = ["A header can be mapped to one field only."];
            }
            else
            {
                columns[field] = header;
            }
        }

        if (errors.Count > 0)
        {
            return columns;
        }

        var required = fields.Where(f => f.RequiredAlways || (f.RequiredForCreate && mode != ImportModes.UpdateOnly)).Select(f => f.Key)
            .Append(matchKey == MatchKeys.Barcode ? ImportFields.Barcode : ImportFields.Sku)
            .Distinct();
        foreach (var field in required.Where(f => !columns.ContainsKey(f)))
        {
            errors[$"columns.{field}"] = ["Required for this import type and mode."];
        }

        return columns;
    }

    private static (string? Name, string? Type, Dictionary<string, string> Columns, string MatchKey, string Mode, Dictionary<string, string[]> Errors)
        ValidateTemplate(SaveMappingTemplateRequest request, IReadOnlyDictionary<string, ImportAttributeField>? attributes)
    {
        var errors = new Dictionary<string, string[]>();
        var name = request.Name?.Trim();
        if (name is not { Length: >= 1 and <= 100 })
        {
            errors["name"] = ["Must be 1–100 characters."];
        }

        var type = request.Type;
        if (type is null || !ImportTypes.All.Contains(type))
        {
            errors["type"] = [$"Must be one of: {string.Join(", ", ImportTypes.All)}."];
            return (name, type, [], MatchKeys.Sku, ImportModes.Upsert, errors);
        }

        var matchKey = request.MatchKey ?? MatchKeys.Sku;
        var mode = request.Mode ?? ImportModes.Upsert;
        var columns = ValidateColumns(type, request.Columns, matchKey, mode, null, attributes, errors);
        return (name, type, columns, matchKey, mode, errors);
    }

    private async Task<AppResult<MappingTemplateResponse>> SaveTemplateAsync(MappingTemplate template, CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return AppError.VersionMismatch();
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: ImportsDbContext.TemplateNameUniqueIndex })
        {
            return AppError.Conflict("imports.template_name_taken", "A template with this name already exists for this type");
        }

        return ToResponse(template);
    }

    private ImportJobResponse ToResponse(ImportJob j) => ToResponse(j, options.Value.RollbackWindow);

    internal static ImportJobResponse ToResponse(ImportJob j, TimeSpan rollbackWindow)
    {
        var planned = j.PlannedRows;
        var percent = j.Status == ImportStatuses.Completed ? 100 : planned == 0 ? 0 : (int)Math.Min(100, j.ProcessedRows * 100L / planned);
        return new ImportJobResponse(
            j.Id,
            j.Type,
            j.Status,
            j.FileName,
            j.FileSizeBytes,
            j.FileSha256,
            j.FileFormat,
            j.FileEncoding,
            j.Delimiter,
            j.SheetName,
            ImportJson.ReadStrings(j.Headers),
            ImportJson.ReadRows(j.SampleRows),
            j.TotalRows,
            j.Mapping is null ? null : ImportJson.ReadMap(j.Mapping),
            j.MatchKey,
            j.Mode,
            j.PriceListId,
            new ImportPreviewCounts(j.PreviewCreate, j.PreviewUpdate, j.PreviewSkip, j.PreviewError),
            new ImportProgress(planned, j.ProcessedRows, percent, j.CreatedRows, j.UpdatedRows, j.SkippedRows, j.FailedRows),
            j.FailureCode is null ? null : new ImportFailure(j.FailureCode, j.FailureMessage ?? string.Empty),
            j.DuplicateOfImportId,
            j.RowVersion,
            j.CreatedAt,
            j.CreatedBy,
            j.ValidatedAt,
            j.StartedAt,
            j.StartedBy,
            j.FinishedAt,
            j.Status == ImportStatuses.Completed ? j.FinishedAt + rollbackWindow : null,
            j.RollbackRequestedAt is { } requestedAt
                ? new ImportRollbackInfo(
                    requestedAt,
                    j.RollbackRequestedBy,
                    j.RollbackFinishedAt,
                    j.CreatedRows + j.UpdatedRows,
                    j.RolledBackRows,
                    j.RollbackConflictRows,
                    j.RollbackFailedRows,
                    j.RollbackSkippedRows)
                : null);
    }

    private static ImportRowResponse ToResponse(ImportRow r) => new(
        r.RowNumber,
        r.Action,
        r.Status,
        r.Outcome,
        ImportJson.ReadCells(r.Raw),
        ImportJson.ReadValues(r.Normalized),
        ImportJson.ReadErrors(r.Errors),
        r.MatchedVariantId,
        r.TargetId,
        r.RollbackStatus);

    private static MappingTemplateResponse ToResponse(MappingTemplate t) =>
        new(t.Id, t.Name, t.Type, ImportJson.ReadMap(t.Mapping), t.MatchKey, t.Mode, t.RowVersion, t.CreatedAt, t.UpdatedAt);
}
