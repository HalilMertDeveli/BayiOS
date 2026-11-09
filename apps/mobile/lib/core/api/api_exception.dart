import 'package:bayios_mobile/core/api/problem_details.dart';

/// What went wrong, independent of wording. The Turkish message comes from
/// `apiErrorMessage` (problem_messages.dart).
enum ApiErrorKind {
  /// No HTTP response: offline, DNS, refused connection, TLS.
  network,

  /// Connect / send / receive timeout (slow warehouse Wi-Fi).
  timeout,
  unauthorized,
  forbidden,
  tenantAccessDenied,
  tenantInactive,
  disabledByTenantRule,
  entitlementRequired,
  entitlementLimit,
  notFound,
  malformed,
  validation,
  conflict,
  idempotencyInProgress,
  idempotencyKeyReused,
  versionMismatch,
  preconditionRequired,
  rateLimited,
  clientUpgradeRequired,
  server,
}

/// A failed API call. Thrown by `ApiClient` for every non-2xx response and
/// for transport failures (status 0).
class ApiException implements Exception {
  ApiException({
    required this.status,
    this.problem,
    this.retryAfter,
    ApiErrorKind? kind,
    this.cause,
  }) : kind = kind ?? classify(status, problem?.code);

  ApiException.network({this.cause, bool timeout = false})
    : status = 0,
      problem = null,
      retryAfter = null,
      kind = timeout ? ApiErrorKind.timeout : ApiErrorKind.network;

  /// HTTP status, or 0 when no response was received.
  final int status;
  final ProblemDetails? problem;

  /// From the `Retry-After` header (429, 409 `*.busy`, 503).
  final Duration? retryAfter;
  final ApiErrorKind kind;
  final Object? cause;

  /// Stable problem code, e.g. `tenant.access_denied`.
  String? get code => problem?.code;
  Map<String, List<String>> get fieldErrors => problem?.errors ?? const {};

  /// True when the same request may succeed later without any change: the
  /// UI offers "Tekrar dene" (and writes reuse their Idempotency-Key).
  bool get isTransient => switch (kind) {
    ApiErrorKind.network ||
    ApiErrorKind.timeout ||
    ApiErrorKind.rateLimited ||
    ApiErrorKind.idempotencyInProgress ||
    ApiErrorKind.server => true,
    _ => status == 409 && (code?.endsWith('.busy') ?? false),
  };

  @override
  String toString() =>
      'ApiException($status, ${code ?? kind.name}'
      '${problem?.correlationId == null ? '' : ', ${problem!.correlationId}'})';

  /// Problem code first, then HTTP status (same order as the web client).
  static ApiErrorKind classify(int status, String? code) {
    switch (code) {
      case 'auth.invalid_token':
        return ApiErrorKind.unauthorized;
      case 'tenant.access_denied':
        return ApiErrorKind.tenantAccessDenied;
      case 'tenant.inactive':
        return ApiErrorKind.tenantInactive;
      case 'auth.disabled_by_tenant_rule':
        return ApiErrorKind.disabledByTenantRule;
      case 'entitlement.required':
        return ApiErrorKind.entitlementRequired;
      case 'entitlement.limit_exceeded':
        return ApiErrorKind.entitlementLimit;
      case 'resource.not_found':
        return ApiErrorKind.notFound;
      case 'request.malformed':
        return ApiErrorKind.malformed;
      case 'validation.failed':
        return ApiErrorKind.validation;
      case 'idempotency.in_progress':
        return ApiErrorKind.idempotencyInProgress;
      case 'idempotency.key_reused':
        return ApiErrorKind.idempotencyKeyReused;
      case 'concurrency.version_mismatch':
        return ApiErrorKind.versionMismatch;
      case 'precondition.required':
        return ApiErrorKind.preconditionRequired;
      case 'rate_limit.exceeded':
        return ApiErrorKind.rateLimited;
      case 'client-upgrade-required':
      case 'client.upgrade_required':
        return ApiErrorKind.clientUpgradeRequired;
    }
    if (status == 0) return ApiErrorKind.network;
    if (status >= 500) return ApiErrorKind.server;
    return switch (status) {
      400 => ApiErrorKind.malformed,
      401 => ApiErrorKind.unauthorized,
      403 => ApiErrorKind.forbidden,
      404 => ApiErrorKind.notFound,
      409 => ApiErrorKind.conflict,
      412 => ApiErrorKind.versionMismatch,
      422 => ApiErrorKind.validation,
      426 => ApiErrorKind.clientUpgradeRequired,
      428 => ApiErrorKind.preconditionRequired,
      429 => ApiErrorKind.rateLimited,
      _ => ApiErrorKind.server,
    };
  }
}

/// `Retry-After` as delay-seconds or an HTTP-date (RFC 9110 §10.2.3).
Duration? parseRetryAfter(String? value, {DateTime? now}) {
  if (value == null) return null;
  final trimmed = value.trim();
  if (trimmed.isEmpty) return null;
  final seconds = int.tryParse(trimmed);
  if (seconds != null) return seconds < 0 ? null : Duration(seconds: seconds);
  final date = _parseHttpDate(trimmed);
  if (date == null) return null;
  final delta = date.difference((now ?? DateTime.now()).toUtc());
  return delta.isNegative ? Duration.zero : delta;
}

const _months = {
  'Jan': 1,
  'Feb': 2,
  'Mar': 3,
  'Apr': 4,
  'May': 5,
  'Jun': 6,
  'Jul': 7,
  'Aug': 8,
  'Sep': 9,
  'Oct': 10,
  'Nov': 11,
  'Dec': 12,
};

/// IMF-fixdate only (`Sun, 06 Nov 1994 08:49:37 GMT`), which is what
/// ASP.NET Core writes.
DateTime? _parseHttpDate(String value) {
  final match = RegExp(
    r'^\w{3}, (\d{2}) (\w{3}) (\d{4}) (\d{2}):(\d{2}):(\d{2}) GMT$',
  ).firstMatch(value);
  if (match == null) return null;
  final month = _months[match.group(2)];
  if (month == null) return null;
  return DateTime.utc(
    int.parse(match.group(3)!),
    month,
    int.parse(match.group(1)!),
    int.parse(match.group(4)!),
    int.parse(match.group(5)!),
    int.parse(match.group(6)!),
  );
}
