import 'dart:convert';

import 'package:bayios_mobile/core/api/api_exception.dart';
import 'package:bayios_mobile/core/api/endpoint.dart';
import 'package:bayios_mobile/core/api/idempotency.dart';
import 'package:bayios_mobile/core/api/problem_details.dart';
import 'package:dio/dio.dart';
import 'package:uuid/uuid.dart';

/// Supplies the Supabase access token to the API client.
abstract interface class AccessTokenSource {
  /// A currently valid access token (refreshed first when about to expire),
  /// or null when signed out. Throws on a transient failure (offline).
  Future<String?> accessToken();

  /// Forces a refresh after the API rejected the token (401). Null when the
  /// session can no longer be refreshed (revoked / expired refresh token).
  Future<String?> refreshAccessToken();
}

/// Session-level reactions to API outcomes (wired in core/providers.dart).
class ApiEvents {
  const ApiEvents({
    this.onUnauthorized,
    this.onTenantAccessDenied,
    this.onTenantInactive,
    this.onReachable,
    this.onUnreachable,
  });

  /// 401 even after a token refresh: the session is over → signed out.
  final void Function()? onUnauthorized;

  /// 403 `tenant.access_denied`: drop the selection, show the picker.
  final void Function()? onTenantAccessDenied;

  /// 403 `tenant.inactive`: the business was suspended or closed.
  final void Function()? onTenantInactive;

  /// Any HTTP response arrived (clears the offline banner).
  final void Function()? onReachable;

  /// No response (offline / timeout): shows the offline banner.
  final void Function()? onUnreachable;
}

class ApiResponse<T> {
  const ApiResponse({
    required this.data,
    required this.status,
    required this.headers,
  });

  final T data;
  final int status;
  final Headers headers;

  /// `ETag` for a later `If-Match`.
  String? get etag => headers.value('etag');

  /// `Idempotent-Replayed: true` — the API returned the stored response of
  /// an earlier attempt with the same key.
  bool get replayed => headers.value('idempotent-replayed') == 'true';
}

/// Thin dio layer over the BayiOS API (ADR 0009 §6): Bearer token with one
/// refresh-and-retry on 401, `X-Tenant-Id` for tenant-scoped endpoints,
/// `Idempotency-Key` for writes, `X-Correlation-Id` / `X-Client-*` and
/// Problem Details → [ApiException]. The app never talks to the database or
/// to Supabase data APIs; every read and write goes through here.
class ApiClient {
  /// Named arguments: `dio:`, `tokens:`, `activeTenantId:`,
  /// `clientVersion:`, `clientPlatform:`, `events:`.
  ApiClient({
    required this._dio,
    required this._tokens,
    required this._activeTenantId,
    required this._clientVersion,
    required this._clientPlatform,
    this._events = const ApiEvents(),
  });

  static const tenantHeader = 'X-Tenant-Id';
  static const idempotencyHeader = 'Idempotency-Key';
  static const correlationHeader = 'X-Correlation-Id';

  final Dio _dio;
  final AccessTokenSource _tokens;
  final String? Function() _activeTenantId;
  final String _clientVersion;
  final String _clientPlatform;
  final ApiEvents _events;
  static const _uuid = Uuid();

  /// Sends [endpoint] and decodes the JSON body with [decode].
  ///
  /// - [idempotencyKey] is required by name for every call; pass the key of
  ///   the user action (`IdempotentAction`) for idempotent endpoints and
  ///   `null` otherwise. A mismatch with the endpoint is a programming error
  ///   ([ArgumentError]).
  /// - [tenantId] selects the tenant of [TenantScope.optional] endpoints
  ///   (`GET /me`); for [TenantScope.required] it overrides the active tenant
  ///   (e.g. removing the push registration of the business being left). A
  ///   `tenant.*` 403 for an overridden tenant does not touch the session.
  Future<ApiResponse<T>> send<T>(
    ApiEndpoint endpoint, {
    required T Function(Object? json) decode,
    IdempotencyKey? idempotencyKey,
    Map<String, String> pathParams = const {},
    Map<String, Object?>? query,
    Object? body,
    String? ifMatch,
    String? tenantId,
    String? contentType,
    CancelToken? cancelToken,
  }) async {
    if (endpoint.idempotent && idempotencyKey == null) {
      throw ArgumentError('$endpoint requires an Idempotency-Key');
    }
    if (!endpoint.idempotent && idempotencyKey != null) {
      throw ArgumentError('$endpoint does not take an Idempotency-Key');
    }

    var foreignTenant = false;
    final headers = <String, Object>{
      'Accept': 'application/json',
      // Same id on the refresh retry: one user action = one correlation id.
      correlationHeader: _uuid.v4().replaceAll('-', ''),
      'X-Client-Version': _clientVersion,
      'X-Client-Platform': _clientPlatform,
      'Accept-Language': 'tr-TR',
    };

    switch (endpoint.tenant) {
      case TenantScope.required:
        final current = _activeTenantId();
        final active = tenantId != null && tenantId.isNotEmpty
            ? tenantId
            : current;
        foreignTenant = active != current;
        if (active == null || active.isEmpty) {
          _events.onTenantAccessDenied?.call();
          throw ApiException(
            status: 403,
            problem: const ProblemDetails(code: 'tenant.access_denied'),
          );
        }
        headers[tenantHeader] = active;
      case TenantScope.optional:
        if (tenantId != null && tenantId.isNotEmpty) {
          headers[tenantHeader] = tenantId;
        }
      case TenantScope.none:
        break;
    }
    if (idempotencyKey != null) {
      headers[idempotencyHeader] = idempotencyKey.value;
    }
    if (ifMatch != null) headers['If-Match'] = ifMatch;

    final path = endpoint.resolve(pathParams);
    var response = await _attempt(
      endpoint,
      path,
      headers,
      query,
      body,
      contentType,
      cancelToken,
      refresh: false,
    );
    if (response.statusCode == 401 && endpoint.authenticated) {
      // Expired or rotated token: refresh once and repeat the very same
      // request (same Idempotency-Key and correlation id).
      response = await _attempt(
        endpoint,
        path,
        headers,
        query,
        body,
        contentType,
        cancelToken,
        refresh: true,
      );
    }

    final status = response.statusCode ?? 0;
    if (status >= 200 && status < 300) {
      final json = _decodeJson(response.data);
      return ApiResponse(
        data: decode(json),
        status: status,
        headers: response.headers,
      );
    }
    throw _failure(response, sessionEvents: !foreignTenant);
  }

  Future<Response<Object?>> _attempt(
    ApiEndpoint endpoint,
    String path,
    Map<String, Object> headers,
    Map<String, Object?>? query,
    Object? body,
    String? contentType,
    CancelToken? cancelToken, {
    required bool refresh,
  }) async {
    if (endpoint.authenticated) {
      final String? token;
      try {
        token = refresh
            ? await _tokens.refreshAccessToken()
            : await _tokens.accessToken();
      } on Object catch (error) {
        _events.onUnreachable?.call();
        throw ApiException.network(cause: error);
      }
      if (token == null) {
        _events.onUnauthorized?.call();
        throw ApiException(
          status: 401,
          problem: const ProblemDetails(code: 'auth.invalid_token'),
        );
      }
      headers['Authorization'] = 'Bearer $token';
    }

    try {
      final response = await _dio.request<Object?>(
        path,
        data: body == null ? null : jsonEncode(body),
        queryParameters: query == null
            ? null
            : {
                for (final e in query.entries)
                  if (e.value != null) e.key: e.value,
              },
        cancelToken: cancelToken,
        options: Options(
          method: endpoint.method.name.toUpperCase(),
          headers: Map<String, Object>.of(headers),
          contentType: body == null
              ? null
              : (contentType ?? Headers.jsonContentType),
          responseType: ResponseType.plain,
          // Every status is handled here (Problem Details), not by dio.
          validateStatus: (_) => true,
        ),
      );
      _events.onReachable?.call();
      return response;
    } on DioException catch (error) {
      if (error.type == DioExceptionType.cancel) rethrow;
      _events.onUnreachable?.call();
      final timeout = switch (error.type) {
        DioExceptionType.connectionTimeout ||
        DioExceptionType.sendTimeout ||
        DioExceptionType.receiveTimeout => true,
        _ => false,
      };
      throw ApiException.network(cause: error, timeout: timeout);
    }
  }

  ApiException _failure(
    Response<Object?> response, {
    required bool sessionEvents,
  }) {
    final status = response.statusCode ?? 0;
    ProblemDetails? problem;
    final json = _tryDecodeJson(response.data);
    if (json is Map<String, Object?>) problem = ProblemDetails.fromJson(json);
    final error = ApiException(
      status: status,
      problem: problem,
      retryAfter: parseRetryAfter(response.headers.value('retry-after')),
    );
    switch (error.kind) {
      case ApiErrorKind.unauthorized:
        _events.onUnauthorized?.call();
      case ApiErrorKind.tenantAccessDenied when sessionEvents:
        _events.onTenantAccessDenied?.call();
      case ApiErrorKind.tenantInactive when sessionEvents:
        _events.onTenantInactive?.call();
      // ignore: no_default_cases, other kinds are handled by the caller's UI.
      default:
        break;
    }
    return error;
  }

  static Object? _decodeJson(Object? data) {
    if (data == null) return null;
    if (data is String) return data.isEmpty ? null : jsonDecode(data);
    return data;
  }

  static Object? _tryDecodeJson(Object? data) {
    try {
      return _decodeJson(data);
    } on FormatException {
      return null;
    }
  }
}
