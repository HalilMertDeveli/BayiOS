import 'dart:convert';

import 'package:bayios_mobile/core/auth/auth_repository.dart';
import 'package:bayios_mobile/core/auth/supabase_auth_repository.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';
import 'package:supabase_flutter/supabase_flutter.dart' as sb;

/// Unsigned JWT with the given expiry — GoTrue reads `exp` client-side and
/// never verifies the signature (the API does, against the JWKS).
String _jwt(DateTime expiresAt, {String sub = 'user-1'}) {
  String part(Map<String, Object?> json) =>
      base64Url.encode(utf8.encode(jsonEncode(json))).replaceAll('=', '');
  return '${part({'alg': 'none', 'typ': 'JWT'})}.'
      '${part({'sub': sub, 'exp': expiresAt.millisecondsSinceEpoch ~/ 1000})}'
      '.sig';
}

Map<String, Object?> _user() => {
  'id': 'user-1',
  'aud': 'authenticated',
  'email': 'depo@ornek.test',
  'app_metadata': <String, Object?>{},
  'user_metadata': <String, Object?>{},
  'created_at': '2026-11-01T00:00:00Z',
};

Map<String, Object?> _session(String accessToken, String refreshToken) => {
  'access_token': accessToken,
  'refresh_token': refreshToken,
  'token_type': 'bearer',
  'expires_in': 3600,
  'user': _user(),
};

void main() {
  late List<http.Request> requests;
  late http.Response Function(http.Request) respond;
  late sb.GoTrueClient client;
  late SupabaseAuthRepository repo;

  setUp(() {
    requests = [];
    respond = (_) => http.Response('{}', 500);
    client = sb.GoTrueClient(
      url: 'http://auth.test/auth/v1',
      autoRefreshToken: false,
      httpClient: MockClient((request) async {
        requests.add(request);
        return respond(request);
      }),
    );
    repo = SupabaseAuthRepository(client);
  });

  Future<void> restore(String accessToken) =>
      client.setInitialSession(jsonEncode(_session(accessToken, 'refresh-1')));

  test('a valid token is used as is', () async {
    final token = _jwt(DateTime.now().add(const Duration(hours: 1)));
    await restore(token);
    expect(await repo.accessToken(), token);
    expect(requests, isEmpty);
    expect(repo.currentUser?.id, 'user-1');
  });

  test('an expired token is refreshed before use', () async {
    final fresh = _jwt(DateTime.now().add(const Duration(hours: 1)));
    respond = (request) => http.Response(
      jsonEncode(_session(fresh, 'refresh-2')),
      200,
      headers: {'content-type': 'application/json'},
    );
    await restore(_jwt(DateTime.now().subtract(const Duration(minutes: 1))));

    expect(await repo.accessToken(), fresh);
    final refresh = requests.single;
    expect(refresh.url.path, '/auth/v1/token');
    expect(refresh.url.queryParameters['grant_type'], 'refresh_token');
    expect(
      jsonDecode(refresh.body),
      containsPair('refresh_token', 'refresh-1'),
    );
  });

  test('a revoked refresh token means signed out', () async {
    respond = (_) => http.Response(
      jsonEncode({
        'code': 400,
        'error_code': 'refresh_token_not_found',
        'msg': 'Invalid Refresh Token: Refresh Token Not Found',
      }),
      400,
      headers: {'content-type': 'application/json'},
    );
    await restore(_jwt(DateTime.now().subtract(const Duration(minutes: 1))));
    final changes = <AuthChange>[];
    final sub = repo.changes().listen(changes.add);

    expect(await repo.refreshAccessToken(), isNull);
    await Future<void>.delayed(Duration.zero);

    expect(repo.currentUser, isNull);
    expect(changes.last.user, isNull);
    expect(changes.last.sessionExpired, isTrue);
    await sub.cancel();
  });

  test('no session → no token, no request', () async {
    expect(await repo.accessToken(), isNull);
    expect(await repo.refreshAccessToken(), isNull);
    expect(requests, isEmpty);
  });

  test('sign in maps Supabase errors', () async {
    respond = (_) => http.Response(
      jsonEncode({
        'code': 400,
        'error_code': 'invalid_credentials',
        'msg': 'Invalid login credentials',
      }),
      400,
      headers: {'content-type': 'application/json'},
    );
    await expectLater(
      repo.signInWithPassword(email: ' depo@ornek.test ', password: 'x'),
      throwsA(
        isA<AuthFailure>().having(
          (f) => f.kind,
          'kind',
          AuthFailureKind.invalidCredentials,
        ),
      ),
    );
    expect(
      jsonDecode(requests.single.body),
      containsPair('email', 'depo@ornek.test'),
    );
  });

  test('sign out never throws offline and clears the session', () async {
    await restore(_jwt(DateTime.now().add(const Duration(hours: 1))));
    respond = (_) => throw http.ClientException('offline');
    await repo.signOut();
    expect(repo.currentUser, isNull);
  });

  test('mapAuthError', () {
    expect(
      mapAuthError('email_not_confirmed', '400'),
      AuthFailureKind.emailNotConfirmed,
    );
    expect(
      mapAuthError('over_request_rate_limit', '429'),
      AuthFailureKind.rateLimited,
    );
    expect(mapAuthError(null, '429'), AuthFailureKind.rateLimited);
    expect(mapAuthError(null, '400'), AuthFailureKind.invalidCredentials);
    expect(mapAuthError('something_new', '500'), AuthFailureKind.unknown);
  });
}
