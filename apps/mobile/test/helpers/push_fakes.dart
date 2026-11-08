import 'dart:async';

import 'package:bayios_mobile/app/env.dart';
import 'package:bayios_mobile/core/api/api_exception.dart';
import 'package:bayios_mobile/core/api/idempotency.dart';
import 'package:bayios_mobile/core/push/device_api.dart';
import 'package:bayios_mobile/core/push/push_service.dart';

const testEnv = AppEnv(
  environment: AppEnvironment.dev,
  apiBaseUrl: 'http://api.test',
  supabaseUrl: 'http://auth.test',
  supabasePublishableKey: 'publishable-test',
  clientVersion: '0.1.0',
);

/// Controllable push service (no Firebase in tests).
class FakePushService implements PushService {
  FakePushService({this.currentToken = 'fcm-token-1'});

  PushPermission permission = PushPermission.granted;
  String? currentToken;
  PushTap? initial;
  int permissionRequests = 0;
  int deleteTokenCalls = 0;
  final _refreshes = StreamController<String>.broadcast();
  final _taps = StreamController<PushTap>.broadcast();
  final _foreground = StreamController<ForegroundPush>.broadcast();

  void refreshToken(String token) {
    currentToken = token;
    _refreshes.add(token);
  }

  void tap(PushTap tap) => _taps.add(tap);

  void foreground(ForegroundPush message) => _foreground.add(message);

  @override
  bool get available => true;

  @override
  Future<PushPermission> requestPermission() async {
    permissionRequests++;
    return permission;
  }

  @override
  Future<String?> token() async => currentToken;

  @override
  Stream<String> get tokenRefreshes => _refreshes.stream;

  @override
  Stream<PushTap> get taps => _taps.stream;

  @override
  Future<PushTap?> initialTap() async => initial;

  @override
  Stream<ForegroundPush> get foregroundMessages => _foreground.stream;

  @override
  Future<void> deleteToken() async {
    deleteTokenCalls++;
    currentToken = null;
  }
}

typedef RegisterCall = ({
  String token,
  String tenantId,
  String platform,
  IdempotencyKey key,
});

/// Records device registrations; the "server" hands out ids per token+tenant.
class FakeDeviceApi implements DeviceApi {
  final registers = <RegisterCall>[];
  final unregisters = <({String id, String tenantId})>[];
  final events = <String>[];

  /// Fails the next register calls with a connection error.
  int failNextRegisters = 0;

  @override
  Future<DeviceRegistration> register({
    required String token,
    required String platform,
    required String appVersion,
    required String locale,
    required String tenantId,
    required IdempotencyKey key,
  }) async {
    registers.add((
      token: token,
      tenantId: tenantId,
      platform: platform,
      key: key,
    ));
    events.add('register $tenantId');
    if (failNextRegisters > 0) {
      failNextRegisters--;
      throw ApiException.network(cause: 'offline');
    }
    return DeviceRegistration(id: idFor(token, tenantId), platform: platform);
  }

  @override
  Future<void> unregister(String id, {required String tenantId}) async {
    unregisters.add((id: id, tenantId: tenantId));
    events.add('unregister $tenantId');
  }

  static String idFor(String token, String tenantId) => 'dev-$tenantId-$token';
}
