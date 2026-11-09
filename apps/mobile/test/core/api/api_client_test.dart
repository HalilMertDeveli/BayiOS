import 'package:bayios_mobile/core/api/api_client.dart';
import 'package:bayios_mobile/core/api/api_exception.dart';
import 'package:bayios_mobile/core/api/endpoint.dart';
import 'package:bayios_mobile/core/api/idempotency.dart';
import 'package:bayios_mobile/core/auth/auth_repository.dart';
import 'package:dio/dio.dart';
import 'package:flutter_test/flutter_test.dart';

import '../../helpers/fakes.dart';

const _tenantRead = ApiEndpoint(HttpMethod.get, '/api/v1/tenant');
const _start = ApiEndpoint(
  HttpMethod.post,
  '/api/v1/picking-tasks/{id}/start',
  idempotent: true,
);
const _me = ApiEndpoint(
  HttpMethod.get,
  '/api/v1/me',
  tenant: TenantScope.optional,
);
const _meta = ApiEndpoint(
  HttpMethod.get,
  '/api/v1/meta',
  tenant: TenantScope.none,
  authenticated: false,
);

Object? _json(Object? json) => json;

void main() {
  late FakeAdapter adapter;
  late FakeAuthRepository auth;
  String? tenantId;
  late List<String> events;
  late ApiClient api;

  setUp(() {
    adapter = FakeAdapter();
    auth = FakeAuthRepository(user: const AuthUser(id: 'user-1'));
    tenantId = 'tenant-a';
    events = [];
    api = ApiClient(
      dio: fakeDio(adapter),
      tokens: auth,
      activeTenantId: () => tenantId,
      clientVersion: '0.1.0',
      clientPlatform: 'android',
      events: ApiEvents(
        onUnauthorized: () => events.add('unauthorized'),
        onTenantAccessDenied: () => events.add('tenantAccessDenied'),
        onTenantInactive: () => events.add('tenantInactive'),
        onReachable: () => events.add('reachable'),
        onUnreachable: () => events.add('unreachable'),
      ),
    );
  });

  group('X-Tenant-Id', () {
    test('tenant-scoped endpoints carry the active tenant', () async {
      adapter.enqueue((_) => FakeReply(200, json: {'ok': true}));
      await api.send(_tenantRead, decode: _json);

      final headers = adapter.requests.single.headers;
      expect(headers[ApiClient.tenantHeader], 'tenant-a');
      expect(headers['Authorization'], 'Bearer token-1');
      expect(headers['X-Client-Platform'], 'android');
      expect(headers['X-Client-Version'], '0.1.0');
      expect(headers[ApiClient.correlationHeader], isNotEmpty);
    });

    test('follows a changed selection on the next call', () async {
      adapter
        ..enqueue((_) => FakeReply(200, json: {}))
        ..enqueue((_) => FakeReply(200, json: {}));
      await api.send(_tenantRead, decode: _json);
      tenantId = 'tenant-b';
      await api.send(_tenantRead, decode: _json);

      expect(adapter.requests.map((r) => r.headers[ApiClient.tenantHeader]), [
        'tenant-a',
        'tenant-b',
      ]);
    });

    test('without an active tenant nothing is sent', () async {
      tenantId = null;
      await expectLater(
        api.send(_tenantRead, decode: _json),
        throwsA(
          isA<ApiException>().having(
            (e) => e.kind,
            'kind',
            ApiErrorKind.tenantAccessDenied,
          ),
        ),
      );
      expect(adapter.requests, isEmpty);
      expect(events, ['tenantAccessDenied']);
    });

    test('optional scope sends only an explicit tenant (GET /me)', () async {
      adapter
        ..enqueue((_) => FakeReply(200, json: {}))
        ..enqueue((_) => FakeReply(200, json: {}));
      await api.send(_me, decode: _json);
      await api.send(_me, decode: _json, tenantId: 'tenant-x');

      expect(adapter.requests[0].headers[ApiClient.tenantHeader], isNull);
      expect(adapter.requests[1].headers[ApiClient.tenantHeader], 'tenant-x');
    });

    test('anonymous endpoints send neither tenant nor token', () async {
      adapter.enqueue((_) => FakeReply(200, json: {}));
      await api.send(_meta, decode: _json);

      final headers = adapter.requests.single.headers;
      expect(headers[ApiClient.tenantHeader], isNull);
      expect(headers['Authorization'], isNull);
    });
  });

  group('Idempotency-Key', () {
    test('is required by idempotent endpoints', () {
      expect(
        () => api.send(_start, decode: _json, pathParams: {'id': 't1'}),
        throwsArgumentError,
      );
      expect(
        () => api.send(
          _tenantRead,
          decode: _json,
          idempotencyKey: IdempotencyKey.generate(),
        ),
        throwsArgumentError,
      );
    });

    test('is sent and the same key is reused on retry', () async {
      adapter
        ..enqueue((_) => FakeReply.fail(DioExceptionType.connectionError))
        ..enqueue((_) => FakeReply(200, json: {'status': 'in_progress'}));
      final action = IdempotentAction(
        (key) => api.send(
          _start,
          decode: _json,
          pathParams: {'id': 'task-1'},
          idempotencyKey: key,
          body: <String, Object?>{},
        ),
      );

      await expectLater(
        action.run(),
        throwsA(
          isA<ApiException>()
              .having((e) => e.kind, 'kind', ApiErrorKind.network)
              .having((e) => e.isTransient, 'isTransient', isTrue),
        ),
      );
      await action.run(); // "Tekrar dene"

      final keys = adapter.requests
          .map((r) => r.headers[ApiClient.idempotencyHeader])
          .toList();
      expect(keys, hasLength(2));
      expect(keys[0], isNotNull);
      expect(keys[1], keys[0]);
      expect(adapter.requests.first.path, '/api/v1/picking-tasks/task-1/start');
      expect(action.succeeded, isTrue);
    });

    test('a new user action gets a new key', () {
      final first = IdempotentAction<void>((_) async {});
      final second = IdempotentAction<void>((_) async {});
      expect(first.key, isNot(second.key));
      final before = first.key;
      first.restart();
      expect(first.key, isNot(before));
    });
  });

  group('token refresh', () {
    test('401 → refresh → the same request again with the new token', () async {
      adapter
        ..enqueue(
          (_) => FakeReply(
            401,
            json: {'status': 401, 'code': 'auth.invalid_token'},
          ),
        )
        ..enqueue((_) => FakeReply(200, json: {'id': 'task-1'}));
      final key = IdempotencyKey.generate();

      final response = await api.send(
        _start,
        decode: _json,
        pathParams: {'id': 'task-1'},
        idempotencyKey: key,
      );

      expect(response.status, 200);
      expect(auth.refreshCalls, 1);
      final [first, second] = adapter.requests;
      expect(first.headers['Authorization'], 'Bearer token-1');
      expect(second.headers['Authorization'], 'Bearer token-2');
      expect(second.headers[ApiClient.idempotencyHeader], key.value);
      expect(
        second.headers[ApiClient.correlationHeader],
        first.headers[ApiClient.correlationHeader],
      );
      expect(events, isNot(contains('unauthorized')));
    });

    test('a session that cannot be refreshed signs out', () async {
      auth.refreshedToken = null;
      adapter.enqueue((_) => FakeReply(401, json: {'status': 401}));

      await expectLater(
        api.send(_tenantRead, decode: _json),
        throwsA(isA<ApiException>().having((e) => e.status, 'status', 401)),
      );
      expect(adapter.requests, hasLength(1));
      expect(events, contains('unauthorized'));
    });

    test('401 after a refresh signs out', () async {
      adapter
        ..enqueue((_) => FakeReply(401, json: {'status': 401}))
        ..enqueue((_) => FakeReply(401, json: {'status': 401}));

      await expectLater(
        api.send(_tenantRead, decode: _json),
        throwsA(isA<ApiException>()),
      );
      expect(auth.refreshCalls, 1);
      expect(events.where((e) => e == 'unauthorized'), hasLength(1));
    });

    test('no token at all signs out without calling the API', () async {
      auth.user = null;
      await expectLater(
        api.send(_tenantRead, decode: _json),
        throwsA(
          isA<ApiException>().having(
            (e) => e.kind,
            'kind',
            ApiErrorKind.unauthorized,
          ),
        ),
      );
      expect(adapter.requests, isEmpty);
      expect(events, ['unauthorized']);
    });

    test('an offline refresh is a network error, not a sign-out', () async {
      auth.refreshError = Exception('offline');
      adapter.enqueue((_) => FakeReply(401, json: {'status': 401}));

      await expectLater(
        api.send(_tenantRead, decode: _json),
        throwsA(
          isA<ApiException>().having(
            (e) => e.kind,
            'kind',
            ApiErrorKind.network,
          ),
        ),
      );
      expect(events, isNot(contains('unauthorized')));
    });
  });

  group('Problem Details', () {
    test('429 carries Retry-After', () async {
      adapter.enqueue(
        (_) => FakeReply(
          429,
          json: {'status': 429, 'code': 'rate_limit.exceeded'},
          headers: {'Retry-After': '7'},
        ),
      );
      await expectLater(
        api.send(_tenantRead, decode: _json),
        throwsA(
          isA<ApiException>()
              .having((e) => e.kind, 'kind', ApiErrorKind.rateLimited)
              .having(
                (e) => e.retryAfter,
                'retryAfter',
                const Duration(seconds: 7),
              )
              .having((e) => e.isTransient, 'isTransient', isTrue),
        ),
      );
    });

    test(
      '403 tenant.access_denied and tenant.inactive notify the session',
      () async {
        adapter
          ..enqueue(
            (_) => FakeReply(
              403,
              json: {'status': 403, 'code': 'tenant.access_denied'},
            ),
          )
          ..enqueue(
            (_) => FakeReply(
              403,
              json: {'status': 403, 'code': 'tenant.inactive'},
            ),
          );
        await expectLater(
          api.send(_tenantRead, decode: _json),
          throwsA(isA<ApiException>()),
        );
        await expectLater(
          api.send(_tenantRead, decode: _json),
          throwsA(isA<ApiException>()),
        );
        expect(
          events,
          containsAllInOrder(['tenantAccessDenied', 'tenantInactive']),
        );
      },
    );

    test('extensions and field errors are kept', () async {
      adapter.enqueue(
        (_) => FakeReply(
          422,
          json: {
            'status': 422,
            'code': 'validation.failed',
            'correlationId': 'c-1',
            'errors': {
              'quantity': ['must be positive'],
            },
            'shortages': [
              {'sku': 'ABC-1'},
            ],
          },
        ),
      );
      try {
        await api.send(_tenantRead, decode: _json);
        fail('expected ApiException');
      } on ApiException catch (e) {
        expect(e.kind, ApiErrorKind.validation);
        expect(e.fieldErrors['quantity'], ['must be positive']);
        expect(e.problem?.correlationId, 'c-1');
        expect(e.problem?.extensions['shortages'], isA<List<Object?>>());
      }
    });

    test('non-JSON 502 maps by status', () async {
      adapter.enqueue((_) => FakeReply(502));
      await expectLater(
        api.send(_tenantRead, decode: _json),
        throwsA(
          isA<ApiException>().having(
            (e) => e.kind,
            'kind',
            ApiErrorKind.server,
          ),
        ),
      );
    });

    test('timeouts and reachability are reported', () async {
      adapter
        ..enqueue((_) => FakeReply.fail(DioExceptionType.receiveTimeout))
        ..enqueue((_) => FakeReply(200, json: {}));
      await expectLater(
        api.send(_tenantRead, decode: _json),
        throwsA(
          isA<ApiException>().having(
            (e) => e.kind,
            'kind',
            ApiErrorKind.timeout,
          ),
        ),
      );
      await api.send(_tenantRead, decode: _json);
      expect(events, ['unreachable', 'reachable']);
    });
  });
}
