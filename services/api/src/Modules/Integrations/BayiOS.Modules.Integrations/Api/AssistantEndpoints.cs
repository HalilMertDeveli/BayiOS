using System.Globalization;
using BayiOS.Modules.Integrations.Application;
using BayiOS.Modules.Integrations.Contracts;
using BayiOS.Modules.Integrations.Domain;
using BayiOS.SharedKernel.Authorization;
using BayiOS.SharedKernel.Entitlements;
using BayiOS.SharedKernel.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace BayiOS.Modules.Integrations.Api;

/// <summary>
/// WhatsApp order assistant (api-strategy §12.10, ADR 0010). Every route needs <c>whatsapp.parse</c>; creating the
/// draft order also needs <c>orders.create</c> (checked by the service). Submit and the review transitions require an
/// Idempotency-Key; transitions honour If-Match when sent.
/// </summary>
internal static class AssistantEndpoints
{
    public static IEndpointRouteBuilder MapAssistantEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/ai")
            .WithTags("AI assistant")
            .RequireAuthorization()
            .WithUnitOfWork()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapPost("/order-parses", Submit).WithName("Ai_OrderParses_Submit")
            .WithSummary("WhatsApp mesajını ayrıştırma kuyruğuna alır (202); sonuç durum ucundan izlenir (fiyat/stok içermez)")
            .WithDescription(
                "Accepts the message and returns 202 with the parse id and `Location` = the status URL; the parse runs in a "
                + "background job (ADR 0010 §2.1): poll `GET /ai/order-parses/{id}/status` with backoff until `state` is "
                + "`succeeded` (then read the review) or `failed`. The text is masked (phone numbers, e-mail addresses, person "
                + "names in context) before it is stored, and parsed by the AI parser when it is enabled, configured and within "
                + "the tenant's daily quota; otherwise — and on any provider error, timeout, refusal or invalid output — by the "
                + "deterministic rules parser (fallbackReason + warnings say which and why). Lines are matched only against the "
                + "tenant's own catalog and learned aliases: matched lines carry one proposal, ambiguous lines up to 5 candidates "
                + "and no proposal, unmatched lines none. 409 `ai.opt_in_required` when the tenant has not switched AI parsing on "
                + "(`PUT /ai/settings`, data-policy §7). 429 `ai.rate_limited` (with Retry-After) when the hourly parse limit is "
                + "reached. Needs the AI assistant add-on (403 `entitlement.required`, feature `ai_order_parsing`); 403 "
                + "`entitlement.limit_exceeded` when the add-on's monthly parse quota (`ai_parses_per_month`) is used up. Input "
                + "limits: `GET /ai/settings`. Reviewing, approving and rejecting stored parses stay open without the add-on.")
            .RequirePermission(Permissions.WhatsappParse)
            .RequireEntitlement(EntitlementCodes.AiOrderParsing)
            .EnforcesEntitlementLimit(EntitlementCodes.AiParsesPerMonth)
            .RequireIdempotencyKey()
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity);
        group.MapGet("/order-parses", List).WithName("Ai_OrderParses_List")
            .WithSummary("Son ayrıştırmalar (yeniden eskiye; mesaj metni yok)")
            .RequirePermission(Permissions.WhatsappParse);
        group.MapGet("/order-parses/{id:guid}", Get).WithName("Ai_OrderParses_Get")
            .WithSummary("Ayrıştırma ve eşleşme adayları (ETag döner; işlenirken status = pending, satır yok)")
            .RequirePermission(Permissions.WhatsappParse)
            .ProducesProblem(StatusCodes.Status404NotFound);
        group.MapGet("/order-parses/{id:guid}/status", Status).WithName("Ai_OrderParses_Status")
            .WithSummary("Ayrıştırmanın işlenme durumu: pending, succeeded veya failed (yoklama hedefi)")
            .WithDescription(
                "Cheap polling target of a submitted parse. `pending`: poll again after `pollAfterMs`, backing off (e.g. ×1.5 up "
                + "to 5 s). `succeeded`: the review is ready at `resultUrl`. `failed`: `failureCode` is `ai.parse_failed` "
                + "(processing gave up), `ai.parse_timed_out` (not processed within the deadline) or `ai.opt_in_revoked` (the "
                + "tenant switched AI off before it ran); fall back to entering the order manually.")
            .RequirePermission(Permissions.WhatsappParse)
            .ProducesProblem(StatusCodes.Status404NotFound);
        Transition(group.MapPost("/order-parses/{id:guid}/approve", Approve).WithName("Ai_OrderParses_Approve")
            .WithSummary("İncelenen satırlardan taslak sipariş oluşturur (kaynak whatsapp_ai; fiyat fiyat listesinden; onay her zaman insan)")
            .WithDescription(
                "Every review line must be listed once: included lines name their variant explicitly (also matched lines), "
                + "skipped lines use include = false. Needs orders.create as well (403 `auth.forbidden`). The created order is a "
                + "draft and is never approved automatically. 409 `ai.parse_not_reviewable` (already approved/rejected), "
                + "`ai.parse_text_purged` (past retention). Orders validation problems are passed through.")
            .RequirePermission(Permissions.WhatsappParse));
        Transition(group.MapPost("/order-parses/{id:guid}/reject", Reject).WithName("Ai_OrderParses_Reject")
            .WithSummary("Ayrıştırmayı reddeder (sipariş oluşmaz)")
            .RequirePermission(Permissions.WhatsappParse));
        group.MapGet("/usage", Usage).WithName("Ai_Usage")
            .WithSummary("Bugünkü AI kullanımı ve limitler (tenant)")
            .RequirePermission(Permissions.WhatsappParse);

        group.MapGet("/settings", GetSettings).WithName("Ai_Settings_Get")
            .WithSummary("Tenant'ın AI ayrıştırma tercihi (opt-in, varsayılan kapalı) ve mesaj giriş limitleri (ETag döner)")
            .RequirePermission(Permissions.WhatsappParse);
        group.MapPut("/settings", UpdateSettings).WithName("Ai_Settings_Update")
            .WithSummary("AI ayrıştırmayı tenant için açar/kapatır (yalnızca owner/admin; denetim kaydı yazılır)")
            .WithDescription(
                "Opt-in of data-policy §7: while off (the default) the message text is never sent to the AI provider and "
                + "submitting is refused with 409 `ai.opt_in_required`; switching it off also stops parses still waiting in "
                + "the queue (`ai.opt_in_revoked`). Needs tenant.settings.manage (owner, admin). If-Match is honoured when sent. "
                + "Audited as `ai.settings_changed` (before/after).")
            .RequirePermission(Permissions.TenantSettingsManage)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapGet("/aliases", ListAliases).WithName("Ai_Aliases_List")
            .WithSummary("Öğrenilmiş eşleştirmeler (ifade → varyant; onaylanan satırlardan, WAI-07)")
            .RequirePermission(Permissions.WhatsappParse);
        group.MapDelete("/aliases/{id:guid}", DeleteAlias).WithName("Ai_Aliases_Delete")
            .WithSummary("Öğrenilmiş bir eşleştirmeyi siler (yanlış öğrenilmişse)")
            .RequirePermission(Permissions.WhatsappParse)
            .ProducesProblem(StatusCodes.Status404NotFound);
        return app;
    }

    private static void Transition(RouteHandlerBuilder builder) => builder
        .RequireIdempotencyKey()
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status409Conflict)
        .ProducesProblem(StatusCodes.Status412PreconditionFailed)
        .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity);

    private static async Task<Results<Accepted<OrderParseAcceptedResponse>, ProblemHttpResult>> Submit(
        SubmitOrderParseRequest request, OrderParseService service, HttpContext http, CancellationToken cancellationToken)
    {
        var result = await service.SubmitAsync(request, cancellationToken);
        if (result.Error is { } error)
        {
            return Problem(http, error);
        }

        return TypedResults.Accepted(result.Value!.StatusUrl, result.Value);
    }

    private static async Task<Results<Ok<OrderParseStatusResponse>, ProblemHttpResult>> Status(
        Guid id, OrderParseService service, HttpContext http, CancellationToken cancellationToken)
    {
        if (await service.StatusAsync(id, cancellationToken) is not { } status)
        {
            return Problem(http, AppError.NotFound());
        }

        // Polled: never cached by intermediaries.
        http.Response.Headers.CacheControl = "no-store";
        return TypedResults.Ok(status);
    }

    private static async Task<Ok<AiSettingsResponse>> GetSettings(OrderParseService service, HttpContext http, CancellationToken cancellationToken)
    {
        var settings = await service.GetSettingsAsync(cancellationToken);
        http.Response.Headers.ETag = ETag(settings.Version);
        return TypedResults.Ok(settings);
    }

    private static async Task<Results<Ok<AiSettingsResponse>, ProblemHttpResult>> UpdateSettings(
        UpdateAiSettingsRequest request, [FromHeader(Name = "If-Match")] string? ifMatch, OrderParseService service, HttpContext http, CancellationToken cancellationToken)
    {
        if (OptionalIfMatch(ifMatch, out var version) is { } precondition)
        {
            return precondition;
        }

        var result = await service.UpdateSettingsAsync(request, version, cancellationToken);
        if (result.Error is { } error)
        {
            return Problem(http, error);
        }

        http.Response.Headers.ETag = ETag(result.Value!.Version);
        return TypedResults.Ok(result.Value);
    }

    /// <param name="variantId">Only aliases of this variant.</param>
    /// <param name="customerId">Only this customer's aliases.</param>
    /// <param name="limit">1–100 (varsayılan 50).</param>
    private static async Task<Results<Ok<LearnedAliasListResponse>, ProblemHttpResult>> ListAliases(
        Guid? variantId, Guid? customerId, int? limit, OrderParseService service, CancellationToken cancellationToken)
    {
        if (limit is < 1 or > OrderParseService.MaxListLimit)
        {
            return Malformed($"limit must be between 1 and {OrderParseService.MaxListLimit}");
        }

        return TypedResults.Ok(await service.ListAliasesAsync(variantId, customerId, limit ?? 50, cancellationToken));
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteAlias(
        Guid id, OrderParseService service, HttpContext http, CancellationToken cancellationToken) =>
        await service.DeleteAliasAsync(id, cancellationToken) ? TypedResults.NoContent() : Problem(http, AppError.NotFound());

    /// <param name="status">pending, failed, needs_review, approved veya rejected.</param>
    /// <param name="limit">1–100 (varsayılan 20).</param>
    private static async Task<Results<Ok<OrderParseListResponse>, ProblemHttpResult>> List(
        string? status, int? limit, OrderParseService service, CancellationToken cancellationToken)
    {
        if (status is not null && !OrderParseStatuses.All.Contains(status))
        {
            return Malformed($"status must be one of: {string.Join(", ", OrderParseStatuses.All)}");
        }

        if (limit is < 1 or > OrderParseService.MaxListLimit)
        {
            return Malformed($"limit must be between 1 and {OrderParseService.MaxListLimit}");
        }

        return TypedResults.Ok(await service.ListAsync(status, limit ?? 20, cancellationToken));
    }

    private static async Task<Results<Ok<OrderParseResponse>, ProblemHttpResult>> Get(
        Guid id, OrderParseService service, HttpContext http, CancellationToken cancellationToken)
    {
        if (await service.GetAsync(id, cancellationToken) is not { } parse)
        {
            return Problem(http, AppError.NotFound());
        }

        http.Response.Headers.ETag = ETag(parse.Version);
        return TypedResults.Ok(parse);
    }

    private static async Task<Results<Ok<OrderParseResponse>, ProblemHttpResult>> Approve(
        Guid id, ApproveOrderParseRequest request, [FromHeader(Name = "If-Match")] string? ifMatch, OrderParseService service, HttpContext http, CancellationToken cancellationToken)
    {
        if (OptionalIfMatch(ifMatch, out var version) is { } precondition)
        {
            return precondition;
        }

        return Ok(http, await service.ApproveAsync(id, request, version, http.TraceIdentifier, cancellationToken));
    }

    private static async Task<Results<Ok<OrderParseResponse>, ProblemHttpResult>> Reject(
        Guid id, [FromHeader(Name = "If-Match")] string? ifMatch, OrderParseService service, HttpContext http, CancellationToken cancellationToken)
    {
        if (OptionalIfMatch(ifMatch, out var version) is { } precondition)
        {
            return precondition;
        }

        return Ok(http, await service.RejectAsync(id, version, cancellationToken));
    }

    private static async Task<Ok<AiUsageResponse>> Usage(OrderParseService service, CancellationToken cancellationToken) =>
        TypedResults.Ok(await service.UsageAsync(cancellationToken));

    private static Results<Ok<OrderParseResponse>, ProblemHttpResult> Ok(HttpContext http, AppResult<OrderParseResponse> result)
    {
        if (result.Error is { } error)
        {
            return Problem(http, error);
        }

        http.Response.Headers.ETag = ETag(result.Value!.Version);
        return TypedResults.Ok(result.Value);
    }

    private static ProblemHttpResult Problem(HttpContext http, AppError error)
    {
        var problem = error.Errors is null || error.Code != "validation.failed"
            ? ApiProblems.Create(error.Status, error.Code, error.Title)
            : ApiProblems.Validation(error.Errors);
        if (error.Errors is not null && error.Code != "validation.failed")
        {
            problem.Extensions["errors"] = error.Errors;
        }

        foreach (var (name, value) in error.Extensions ?? new Dictionary<string, object>())
        {
            problem.Extensions[name] = value;
        }

        if (error.RetryAfterSeconds is { } seconds)
        {
            http.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
        }

        return TypedResults.Problem(problem);
    }

    private static ProblemHttpResult Malformed(string title) =>
        TypedResults.Problem(ApiProblems.Create(StatusCodes.Status400BadRequest, "request.malformed", title));

    private static ProblemHttpResult? OptionalIfMatch(string? ifMatch, out long? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(ifMatch))
        {
            return null;
        }

        var value = ifMatch.Trim();
        if (value.StartsWith("W/", StringComparison.Ordinal))
        {
            value = value[2..];
        }

        if (long.TryParse(value.Trim('"'), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
        {
            version = parsed;
            return null;
        }

        return TypedResults.Problem(ApiProblems.Create(StatusCodes.Status400BadRequest, "request.malformed", "If-Match must be an ETag such as W/\"3\""));
    }

    private static string ETag(long version) => $"W/\"{version.ToString(CultureInfo.InvariantCulture)}\"";
}
