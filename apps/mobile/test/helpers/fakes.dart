import 'dart:async';
import 'dart:convert';
import 'dart:typed_data';

import 'package:bayios_mobile/core/auth/auth_repository.dart';
import 'package:bayios_mobile/core/session/identity_api.dart';
import 'package:bayios_mobile/core/session/me.dart';
import 'package:dio/dio.dart';

/// Controllable [AuthRepository].
class FakeAuthRepository implements AuthRepository {
  FakeAuthRepository({this.user, this.token = 'token-1'});

  AuthUser? user;
  String? token;

  /// Returned by [refreshAccessToken]; null = session cannot be refreshed.
  String? refreshedToken = 'token-2';
  Exception? refreshError;
  Exception? signInError;
  AuthUser signInUser = const AuthUser(id: 'user-1', email: 'depo@ornek.test');

  int refreshCalls = 0;
  int signOutCalls = 0;

  /// Called inside [signOut] (ordering checks).
  void Function()? onSignOut;
  final signInCalls = <({String email, String password})>[];
  final _changes = StreamController<AuthChange>.broadcast();

  void emit(AuthChange change) {
    user = change.user;
    _changes.add(change);
  }

  @override
  AuthUser? get currentUser => user;

  @override
  Stream<AuthChange> changes() => _changes.stream;

  @override
  Future<void> signInWithPassword({
    required String email,
    required String password,
  }) async {
    signInCalls.add((email: email, password: password));
    final error = signInError;
    if (error != null) throw error;
    user = signInUser;
  }

  @override
  Future<void> signOut() async {
    signOutCalls++;
    onSignOut?.call();
    user = null;
  }

  @override
  Future<String?> accessToken() async => user == null ? null : token;

  @override
  Future<String?> refreshAccessToken() async {
    refreshCalls++;
    final error = refreshError;
    if (error != null) throw error;
    token = refreshedToken;
    return refreshedToken;
  }
}

typedef FakeHandler = FakeReply Function(RequestOptions request);

class FakeReply {
  FakeReply(
    this.status, {
    Object? json,
    Map<String, String> headers = const {},
    this.error,
  }) : body = json == null ? '' : jsonEncode(json),
       headers = {
         if (json != null)
           'content-type': [
             if (status >= 400)
               'application/problem+json'
             else
               'application/json; charset=utf-8',
           ],
         for (final h in headers.entries) h.key.toLowerCase(): [h.value],
       };

  /// Transport failure instead of a response.
  FakeReply.fail(DioExceptionType type)
    : status = 0,
      body = '',
      headers = const {},
      error = type;

  final int status;
  final String body;
  final Map<String, List<String>> headers;
  final DioExceptionType? error;
}

/// dio adapter answering from a queue and recording every request.
class FakeAdapter implements HttpClientAdapter {
  FakeAdapter([List<FakeHandler>? replies]) : _replies = [...?replies];

  final List<FakeHandler> _replies;
  final requests = <RequestOptions>[];

  void enqueue(FakeHandler handler) => _replies.add(handler);

  @override
  Future<ResponseBody> fetch(
    RequestOptions options,
    Stream<Uint8List>? requestStream,
    Future<void>? cancelFuture,
  ) async {
    requests.add(options);
    if (_replies.isEmpty) {
      throw StateError('No reply queued for ${options.method} ${options.uri}');
    }
    final reply = _replies.removeAt(0)(options);
    final error = reply.error;
    if (error != null) {
      throw DioException(requestOptions: options, type: error);
    }
    return ResponseBody.fromString(
      reply.body,
      reply.status,
      headers: reply.headers,
    );
  }

  @override
  void close({bool force = false}) {}
}

Dio fakeDio(FakeAdapter adapter) =>
    Dio(BaseOptions(baseUrl: 'http://api.test'))..httpClientAdapter = adapter;

// ---------------------------------------------------------------- /me data

Map<String, Object?> membershipJson(
  String tenantId, {
  String name = 'Demo Depo',
  String role = 'warehouse_staff',
  String status = 'active',
  String tenantStatus = 'active',
}) => {
  'tenantId': tenantId,
  'tenantName': name,
  'tenantSlug': 'slug-$tenantId',
  'role': role,
  'status': status,
  'tenantStatus': tenantStatus,
};

Map<String, Object?> activeTenantJson(
  String tenantId, {
  String role = 'warehouse_staff',
  List<String> permissions = const ['tenant.read', 'tasks.pick'],
}) => {
  'tenantId': tenantId,
  'role': role,
  'permissions': [
    for (final p in permissions)
      {'permission': p, 'scope': 'all', 'condition': null, 'parameters': null},
  ],
  'entitlements': {
    'plan': 'professional',
    'status': 'trialing',
    'trialEndsAt': null,
    'catalogVersion': '1',
    'features': <String>[],
    'limits': <Object>[],
  },
};

Me meFixture({
  List<Map<String, Object?>> memberships = const [],
  Map<String, Object?>? activeTenant,
  String userId = 'user-1',
  String? fullName = 'Ayşe Depocu',
}) => Me.fromJson({
  'userId': userId,
  'email': 'depo@ornek.test',
  'fullName': fullName,
  'memberships': memberships,
  'activeTenant': activeTenant,
});

/// [IdentityApi] whose `/me` answers come from [respond].
class FakeIdentityApi implements IdentityApi {
  FakeIdentityApi(this.respond);

  Future<Me> Function(String? tenantId) respond;
  final calls = <String?>[];

  @override
  Future<Me> getMe({String? tenantId}) {
    calls.add(tenantId);
    return respond(tenantId);
  }

  @override
  Future<void> ping() async {}
}
