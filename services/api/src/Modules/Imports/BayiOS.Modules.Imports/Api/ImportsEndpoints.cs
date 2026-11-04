using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using BayiOS.Modules.Imports.Application;
using BayiOS.Modules.Imports.Contracts;
using BayiOS.Modules.Imports.Domain;
using BayiOS.Modules.Imports.Parsing;
using BayiOS.SharedKernel.Authorization;
using BayiOS.SharedKernel.Platform.Idempotency;
using BayiOS.SharedKernel.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace BayiOS.Modules.Imports.Api;

/// <summary>
/// Bulk import (api-strategy §12.9). Every endpoint needs <c>imports.run</c> (owner, admin — roles-permissions §3.3);
/// product imports additionally <c>catalog.write</c>, anything with prices <c>prices.write</c>. Upload, validate,
/// start and cancel require Idempotency-Key. Validation and execution are background jobs: clients poll
/// <c>GET /import-jobs/{id}</c> for status and progress.
/// </summary>
internal static class ImportsEndpoints
{
    /// <summary>Multipart framing allowance on top of the file limit.</summary>
    private const long MultipartOverheadBytes = 64 * 1024;

    private static readonly FormOptions UploadFormOptions = new()
    {
        MultipartBodyLengthLimit = ImportLimits.MaxFileBytes + MultipartOverheadBytes,
        ValueCountLimit = 16,
        ValueLengthLimit = 1024,
        KeyLengthLimit = 64,
    };

    public static IEndpointRouteBuilder MapImportsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1")
            .WithTags("Imports")
            .RequireAuthorization()
            .WithUnitOfWork()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapPost("/import-jobs", Upload).WithName("Imports_Upload")
            .WithSummary("Dosya yükler (.xlsx/.csv, ≤ 10 MB, ≤ 10.000 satır): içerik/magic byte, makro, zip bombası ve satır/kolon limitleri doğrulanır")
            .RequirePermission(Permissions.ImportsRun)
            .RequireIdempotencyKey()
            .WithMetadata(new UploadIdempotencyHash())
            .Accepts<UploadImportForm>("multipart/form-data")
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status415UnsupportedMediaType)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity);
        group.MapGet("/import-jobs", List).WithName("Imports_List")
            .WithSummary("İçe aktarma geçmişi (yeniden eskiye, keyset sayfalama)").RequirePermission(Permissions.ImportsRun);
        group.MapGet("/import-jobs/{id:guid}", Get).WithName("Imports_Get")
            .WithSummary("Durum, önizleme sayıları ve ilerleme").RequirePermission(Permissions.ImportsRun)
            .ProducesProblem(StatusCodes.Status404NotFound);
        group.MapGet("/import-jobs/{id:guid}/mapping-suggestions", Suggest).WithName("Imports_MappingSuggestions")
            .WithSummary("Kolon eşleme önerileri (başlık eş anlamlıları + uyan şablonlar); otomatik uygulanmaz").RequirePermission(Permissions.ImportsRun)
            .ProducesProblem(StatusCodes.Status404NotFound);
        group.MapPut("/import-jobs/{id:guid}/mapping", SaveMapping).WithName("Imports_SaveMapping")
            .WithSummary("Kolon eşlemesini kaydeder (isteğe bağlı şablon olarak); önceki önizlemeyi siler").RequirePermission(Permissions.ImportsRun)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity);
        group.MapPost("/import-jobs/{id:guid}/validate", Validate).WithName("Imports_Validate")
            .WithSummary("Önizlemeyi (dry run) arka planda çalıştırır: eşleşme, dosya içi tekrar, satır hataları").RequirePermission(Permissions.ImportsRun)
            .RequireIdempotencyKey()
            .ProducesProblem(StatusCodes.Status404NotFound);
        group.MapPost("/import-jobs/{id:guid}/start", Start).WithName("Imports_Start")
            .WithSummary("İçe aktarmayı başlatır veya iptal/hata sonrası kaldığı satırdan devam ettirir").RequirePermission(Permissions.ImportsRun)
            .RequireIdempotencyKey()
            .ProducesProblem(StatusCodes.Status404NotFound);
        group.MapPost("/import-jobs/{id:guid}/cancel", Cancel).WithName("Imports_Cancel")
            .WithSummary("İptal eder; işlenmiş parçalar kalır, kalan satırlar işlenmez").RequirePermission(Permissions.ImportsRun)
            .RequireIdempotencyKey()
            .ProducesProblem(StatusCodes.Status404NotFound);
        group.MapGet("/import-jobs/{id:guid}/rows", Rows).WithName("Imports_Rows")
            .WithSummary("Satırlar: status (error = invalid + failed) ve action filtresi, satır no ile keyset").RequirePermission(Permissions.ImportsRun)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity);
        group.MapGet("/import-jobs/{id:guid}/error-report", ErrorReport).WithName("Imports_ErrorReport")
            .WithSummary("Hatalı satırlar CSV (UTF-8 BOM, ';'; formül enjeksiyonuna karşı nötrlenmiş)").RequirePermission(Permissions.ImportsRun)
            .Produces(StatusCodes.Status200OK, contentType: "text/csv")
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/import-mapping-templates", ListTemplates).WithName("Imports_ListTemplates")
            .WithSummary("Kayıtlı kolon eşleme şablonları").RequirePermission(Permissions.ImportsRun);
        group.MapGet("/import-mapping-templates/{id:guid}", GetTemplate).WithName("Imports_GetTemplate")
            .WithSummary("Şablon (ETag döner)").RequirePermission(Permissions.ImportsRun)
            .ProducesProblem(StatusCodes.Status404NotFound);
        group.MapPost("/import-mapping-templates", CreateTemplate).WithName("Imports_CreateTemplate")
            .WithSummary("Şablon oluşturur").RequirePermission(Permissions.ImportsRun)
            .RequireIdempotencyKey()
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity);
        group.MapPut("/import-mapping-templates/{id:guid}", UpdateTemplate).WithName("Imports_UpdateTemplate")
            .WithSummary("Şablonu değiştirir — If-Match zorunlu").RequirePermission(Permissions.ImportsRun)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity);
        group.MapDelete("/import-mapping-templates/{id:guid}", DeleteTemplate).WithName("Imports_DeleteTemplate")
            .WithSummary("Şablonu siler").RequirePermission(Permissions.ImportsRun)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return app;
    }

    private static async Task<Results<Created<ImportJobResponse>, ProblemHttpResult>> Upload(
        HttpRequest request, ImportService service, CancellationToken cancellationToken)
    {
        if (!request.HasFormContentType)
        {
            return Problem(StatusCodes.Status415UnsupportedMediaType, "request.unsupported_media_type", "Use multipart/form-data with a 'file' part");
        }

        if (request.ContentLength > ImportLimits.MaxFileBytes + MultipartOverheadBytes)
        {
            return FileProblem(ImportFileRejectedException.TooLarge());
        }

        IFormCollection form;
        try
        {
            form = await request.ReadFormAsync(UploadFormOptions, cancellationToken);
        }
        catch (InvalidDataException)
        {
            // Body or part limits exceeded, or broken multipart framing.
            return FileProblem(ImportFileRejectedException.TooLarge());
        }

        if (form.Files.Count != 1 || form.Files.GetFile("file") is not { } file)
        {
            return TypedResults.Problem(ApiProblems.Validation(new Dictionary<string, string[]> { ["file"] = ["Exactly one 'file' part is required."] }));
        }

        if (file.Length > ImportLimits.MaxFileBytes)
        {
            return FileProblem(ImportFileRejectedException.TooLarge());
        }

        byte[] content;
        using (var buffer = new MemoryStream((int)file.Length))
        {
            await file.CopyToAsync(buffer, cancellationToken);
            content = buffer.ToArray();
        }

        try
        {
            var result = await service.UploadAsync(form["type"].ToString() is { Length: > 0 } type ? type : null, SafeFileName(file.FileName), file.ContentType, content, cancellationToken);
            return result.Error is { } error ? Problem(error) : TypedResults.Created($"/api/v1/import-jobs/{result.Value!.Id}", result.Value);
        }
        catch (ImportFileRejectedException ex)
        {
            return FileProblem(ex);
        }
    }

    private static async Task<Results<Ok<ImportJobPageResponse>, ProblemHttpResult>> List(
        HttpRequest http, ImportService service, string? limit, string? cursor, CancellationToken cancellationToken)
    {
        if (RejectUnknownQuery(http, "limit", "cursor") is { } unknown)
        {
            return unknown;
        }

        if (ParseLimit(limit, out var pageSize) is { } badLimit)
        {
            return badLimit;
        }

        Guid? before = null;
        if (cursor is not null)
        {
            if (!ImportCursor.TryDecodeId(cursor, out var id))
            {
                return Malformed("cursor is invalid for this query");
            }

            before = id;
        }

        return TypedResults.Ok(await service.ListAsync(pageSize, before, cancellationToken));
    }

    private static async Task<Results<Ok<ImportJobResponse>, ProblemHttpResult>> Get(Guid id, ImportService service, CancellationToken cancellationToken) =>
        Ok(await service.GetAsync(id, cancellationToken));

    private static async Task<Results<Ok<MappingSuggestionsResponse>, ProblemHttpResult>> Suggest(Guid id, ImportService service, CancellationToken cancellationToken) =>
        Ok(await service.SuggestAsync(id, cancellationToken));

    private static async Task<Results<Ok<ImportJobResponse>, ProblemHttpResult>> SaveMapping(
        Guid id, SaveImportMappingRequest request, ImportService service, CancellationToken cancellationToken) =>
        Ok(await service.SaveMappingAsync(id, request, cancellationToken));

    private static async Task<Results<Accepted<ImportJobResponse>, ProblemHttpResult>> Validate(Guid id, ImportService service, CancellationToken cancellationToken) =>
        Accepted(await service.ValidateAsync(id, cancellationToken));

    private static async Task<Results<Accepted<ImportJobResponse>, ProblemHttpResult>> Start(Guid id, ImportService service, CancellationToken cancellationToken) =>
        Accepted(await service.StartAsync(id, cancellationToken));

    private static async Task<Results<Ok<ImportJobResponse>, ProblemHttpResult>> Cancel(Guid id, ImportService service, CancellationToken cancellationToken) =>
        Ok(await service.CancelAsync(id, cancellationToken));

    private static async Task<Results<Ok<ImportRowPageResponse>, ProblemHttpResult>> Rows(
        Guid id, HttpRequest http, ImportService service, string? status, string? action, string? limit, string? cursor, CancellationToken cancellationToken)
    {
        if (RejectUnknownQuery(http, "status", "action", "limit", "cursor") is { } unknown)
        {
            return unknown;
        }

        if (ParseLimit(limit, out var pageSize) is { } badLimit)
        {
            return badLimit;
        }

        var after = 0;
        if (cursor is not null && !ImportCursor.TryDecodeRow(cursor, out after))
        {
            return Malformed("cursor is invalid for this query");
        }

        return Ok(await service.ListRowsAsync(id, status, action, pageSize, after, cancellationToken));
    }

    private static async Task<Results<FileContentHttpResult, ProblemHttpResult>> ErrorReport(Guid id, ImportService service, CancellationToken cancellationToken)
    {
        var result = await service.ErrorReportAsync(id, cancellationToken);
        return result.Error is { } error
            ? Problem(error)
            : TypedResults.File(result.Value.Content, "text/csv; charset=utf-8", result.Value.FileName);
    }

    private static async Task<Results<Ok<MappingTemplateListResponse>, ProblemHttpResult>> ListTemplates(
        HttpRequest http, ImportService service, string? type, CancellationToken cancellationToken)
    {
        if (RejectUnknownQuery(http, "type") is { } unknown)
        {
            return unknown;
        }

        if (type is not null && !ImportTypes.All.Contains(type))
        {
            return Malformed($"type must be one of: {string.Join(", ", ImportTypes.All)}");
        }

        return TypedResults.Ok(new MappingTemplateListResponse(await service.ListTemplatesAsync(type, cancellationToken)));
    }

    private static async Task<Results<Ok<MappingTemplateResponse>, ProblemHttpResult>> GetTemplate(
        Guid id, ImportService service, HttpContext http, CancellationToken cancellationToken) =>
        WithETag(http, await service.GetTemplateAsync(id, cancellationToken));

    private static async Task<Results<Created<MappingTemplateResponse>, ProblemHttpResult>> CreateTemplate(
        SaveMappingTemplateRequest request, ImportService service, HttpContext http, CancellationToken cancellationToken)
    {
        var result = await service.CreateTemplateAsync(request, cancellationToken);
        if (result.Error is { } error)
        {
            return Problem(error);
        }

        http.Response.Headers.ETag = ETag(result.Value!.Version);
        return TypedResults.Created($"/api/v1/import-mapping-templates/{result.Value.Id}", result.Value);
    }

    private static async Task<Results<Ok<MappingTemplateResponse>, ProblemHttpResult>> UpdateTemplate(
        Guid id,
        SaveMappingTemplateRequest request,
        [FromHeader(Name = "If-Match")] string? ifMatch,
        ImportService service,
        HttpContext http,
        CancellationToken cancellationToken)
    {
        if (IfMatch(ifMatch, out var version) is { } precondition)
        {
            return precondition;
        }

        return WithETag(http, await service.UpdateTemplateAsync(id, request, version, cancellationToken));
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteTemplate(Guid id, ImportService service, CancellationToken cancellationToken) =>
        await service.DeleteTemplateAsync(id, cancellationToken) is { } error ? Problem(error) : TypedResults.NoContent();

    // ---- Helpers ----

    /// <summary>
    /// Idempotency hash input for the multipart upload: every form field and, per file part, its name, file name,
    /// declared content type, length and SHA-256 of the bytes. The form is parsed here with the upload limits and cached
    /// by ASP.NET Core, so the handler reads the same form; an over-limit or broken form contributes nothing and the
    /// handler rejects it (the error rolls back the key claim).
    /// </summary>
    private sealed class UploadIdempotencyHash : IIdempotencyHashContributor
    {
        public async ValueTask ContributeAsync(HttpContext context, IncrementalHash hash, CancellationToken cancellationToken)
        {
            var request = context.Request;
            if (!request.HasFormContentType || request.ContentLength > ImportLimits.MaxFileBytes + MultipartOverheadBytes)
            {
                return;
            }

            IFormCollection form;
            try
            {
                form = await request.ReadFormAsync(UploadFormOptions, cancellationToken);
            }
            catch (InvalidDataException)
            {
                return;
            }

            foreach (var key in form.Keys.Order(StringComparer.Ordinal))
            {
                hash.AppendData(Encoding.UTF8.GetBytes($"field\n{key}\n{form[key]}\n"));
            }

            foreach (var file in form.Files.OrderBy(f => f.Name, StringComparer.Ordinal))
            {
                hash.AppendData(Encoding.UTF8.GetBytes(
                    $"file\n{file.Name}\n{file.FileName}\n{file.ContentType}\n{file.Length.ToString(CultureInfo.InvariantCulture)}\n"));
                await using var stream = file.OpenReadStream();
                hash.AppendData(await SHA256.HashDataAsync(stream, cancellationToken));
            }
        }
    }

    /// <summary>Base name only, no control or path characters, ≤ 200 characters; the extension is kept.</summary>
    internal static string SafeFileName(string? fileName)
    {
        var name = Path.GetFileName((fileName ?? string.Empty).Replace('\\', '/'));
        name = new string([.. name.Where(c => !char.IsControl(c) && c is not ('/' or ':' or '*' or '?' or '"' or '<' or '>' or '|'))]).Trim();
        if (name.Length > 200)
        {
            var extension = Path.GetExtension(name);
            name = name[..(200 - extension.Length)] + extension;
        }

        return name.Length == 0 ? "upload" : name;
    }

    private static Results<Ok<T>, ProblemHttpResult> Ok<T>(AppResult<T> result) =>
        result.Error is { } error ? Problem(error) : TypedResults.Ok(result.Value);

    private static Results<Accepted<ImportJobResponse>, ProblemHttpResult> Accepted(AppResult<ImportJobResponse> result) =>
        result.Error is { } error ? Problem(error) : TypedResults.Accepted($"/api/v1/import-jobs/{result.Value!.Id}", result.Value);

    private static Results<Ok<MappingTemplateResponse>, ProblemHttpResult> WithETag(HttpContext http, AppResult<MappingTemplateResponse> result)
    {
        if (result.Error is { } error)
        {
            return Problem(error);
        }

        http.Response.Headers.ETag = ETag(result.Value!.Version);
        return TypedResults.Ok(result.Value);
    }

    private static ProblemHttpResult Problem(AppError error) => TypedResults.Problem(
        error.Errors is null ? ApiProblems.Create(error.Status, error.Code, error.Title) : ApiProblems.Validation(error.Errors));

    private static ProblemHttpResult Problem(int status, string code, string title) => TypedResults.Problem(ApiProblems.Create(status, code, title));

    private static ProblemHttpResult FileProblem(ImportFileRejectedException ex) =>
        TypedResults.Problem(ApiProblems.Create(ex.Status, ex.Code, "The file was rejected", ex.Message));

    private static ProblemHttpResult Malformed(string title) => Problem(StatusCodes.Status400BadRequest, "request.malformed", title);

    private static ProblemHttpResult? ParseLimit(string? limit, out int pageSize)
    {
        pageSize = ImportService.DefaultPageSize;
        return limit is not null
            && (!int.TryParse(limit, NumberStyles.None, CultureInfo.InvariantCulture, out pageSize) || pageSize is < 1 or > ImportService.MaxPageSize)
            ? Malformed($"limit must be an integer between 1 and {ImportService.MaxPageSize}")
            : null;
    }

    private static ProblemHttpResult? RejectUnknownQuery(HttpRequest request, params string[] allowed)
    {
        var unknown = request.Query.Keys.Where(k => !allowed.Contains(k, StringComparer.OrdinalIgnoreCase)).ToList();
        return unknown.Count == 0 ? null : Malformed($"Unknown query parameter(s): {string.Join(", ", unknown)}");
    }

    private static ProblemHttpResult? IfMatch(string? ifMatch, out long version)
    {
        version = 0;
        if (string.IsNullOrWhiteSpace(ifMatch))
        {
            return Problem(StatusCodes.Status428PreconditionRequired, "precondition.required", "If-Match header is required");
        }

        var trimmed = ifMatch.Trim();
        if (trimmed.StartsWith("W/", StringComparison.Ordinal))
        {
            trimmed = trimmed[2..];
        }

        return long.TryParse(trimmed.Trim('"'), NumberStyles.None, CultureInfo.InvariantCulture, out version)
            ? null
            : Problem(StatusCodes.Status412PreconditionFailed, "concurrency.version_mismatch", "If-Match does not match the current version");
    }

    private static string ETag(long version) => $"W/\"{version.ToString(CultureInfo.InvariantCulture)}\"";
}
