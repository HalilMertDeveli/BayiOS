/// How an endpoint uses `X-Tenant-Id` (api-strategy §4).
enum TenantScope {
  /// No tenant header (e.g. `GET /meta`).
  none,

  /// Header optional and chosen per call (`GET /me`).
  optional,

  /// Every tenant-scoped endpoint: the active tenant is always sent.
  required,
}

/// HTTP methods the client sends.
enum HttpMethod { get, post, put, patch, delete }

/// One operation of packages/contracts/openapi.json, declared once per
/// feature (e.g. `IdentityEndpoints.me`). `ApiClient` derives the headers
/// from it, and test/contract/contract_test.dart checks every declared
/// endpoint against the contract: the path and method exist, `X-Tenant-Id`
/// is required exactly when [tenant] is [TenantScope.required] and
/// `Idempotency-Key` is required exactly when [idempotent] is true. Add new
/// endpoints to `allEndpoints` (lib/core/api/contract_registry.dart).
class ApiEndpoint {
  const ApiEndpoint(
    this.method,
    this.path, {
    this.tenant = TenantScope.required,
    this.idempotent = false,
    this.authenticated = true,
  });

  final HttpMethod method;

  /// Contract path template, e.g. `/api/v1/picking-tasks/{id}/start`.
  final String path;
  final TenantScope tenant;

  /// The contract requires an `Idempotency-Key` (creation POSTs and state
  /// transitions). `ApiClient` refuses to send such a call without a key.
  final bool idempotent;

  /// Sends `Authorization: Bearer` (everything except anonymous endpoints).
  final bool authenticated;

  /// [path] with `{name}` placeholders replaced (URL-encoded).
  String resolve(Map<String, String> params) {
    final resolved = path.replaceAllMapped(RegExp(r'\{(\w+)\}'), (m) {
      final value = params[m.group(1)];
      if (value == null || value.isEmpty) {
        throw ArgumentError('Missing path parameter "${m.group(1)}" for $path');
      }
      return Uri.encodeComponent(value);
    });
    return resolved;
  }

  @override
  String toString() => '${method.name.toUpperCase()} $path';
}
