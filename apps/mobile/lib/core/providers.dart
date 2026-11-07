import 'dart:async';

import 'package:bayios_mobile/app/env.dart';
import 'package:bayios_mobile/core/api/api_client.dart';
import 'package:bayios_mobile/core/auth/auth_controller.dart';
import 'package:bayios_mobile/core/session/active_tenant_store.dart';
import 'package:bayios_mobile/core/session/identity_api.dart';
import 'package:bayios_mobile/core/session/session_controller.dart';
import 'package:dio/dio.dart';
import 'package:flutter/foundation.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

/// Overridden in bootstrap with the `--dart-define` configuration.
final appEnvProvider = Provider<AppEnv>(
  (ref) => throw UnimplementedError('appEnvProvider not overridden'),
);

final activeTenantStoreProvider = Provider<ActiveTenantStore>(
  (ref) => const SecureActiveTenantStore(),
);

/// Timeouts sized for weak warehouse Wi-Fi: fail visibly instead of hanging.
final dioProvider = Provider<Dio>((ref) {
  final env = ref.watch(appEnvProvider);
  final dio = Dio(
    BaseOptions(
      baseUrl: env.apiBaseUrl,
      connectTimeout: const Duration(seconds: 10),
      sendTimeout: const Duration(seconds: 20),
      receiveTimeout: const Duration(seconds: 20),
    ),
  );
  ref.onDispose(dio.close);
  return dio;
});

/// The tenant sent as `X-Tenant-Id`; set by [SessionController] once the API
/// confirmed the membership (`GET /me` returned `activeTenant`).
final activeTenantIdProvider = NotifierProvider<ActiveTenantId, String?>(
  ActiveTenantId.new,
);

class ActiveTenantId extends Notifier<String?> {
  @override
  String? build() => null;

  String? get value => state;
  set value(String? tenantId) => state = tenantId;
}

/// False after a request got no response; true again after any response.
final networkStatusProvider = NotifierProvider<NetworkStatus, bool>(
  NetworkStatus.new,
);

class NetworkStatus extends Notifier<bool> {
  bool _probing = false;

  @override
  bool build() => true;

  bool get reachable => state;

  void markReachable() {
    if (!state) state = true;
  }

  void markUnreachable() {
    if (state) state = false;
  }

  bool get probing => _probing;

  /// "Tekrar dene" on the offline banner: an anonymous `GET /api/v1/meta`.
  Future<void> probe() async {
    if (_probing) return;
    _probing = true;
    try {
      await ref.read(identityApiProvider).ping();
    } on Object {
      // markUnreachable already happened through ApiEvents.
    } finally {
      _probing = false;
    }
  }
}

String _platform() =>
    defaultTargetPlatform == TargetPlatform.iOS ? 'ios' : 'android';

final apiClientProvider = Provider<ApiClient>((ref) {
  final env = ref.watch(appEnvProvider);
  // Callbacks read providers lazily, at call time (no build-time cycle).
  void later(void Function() action) => scheduleMicrotask(action);
  return ApiClient(
    dio: ref.watch(dioProvider),
    tokens: ref.watch(authRepositoryProvider),
    activeTenantId: () => ref.read(activeTenantIdProvider),
    clientVersion: env.clientVersion,
    clientPlatform: _platform(),
    events: ApiEvents(
      onUnauthorized: () => later(
        () => ref.read(authControllerProvider.notifier).sessionExpired(),
      ),
      onTenantAccessDenied: () => later(
        () => ref.read(sessionControllerProvider.notifier).tenantAccessDenied(),
      ),
      onTenantInactive: () => later(
        () => ref.read(sessionControllerProvider.notifier).tenantInactive(),
      ),
      onReachable: () =>
          later(() => ref.read(networkStatusProvider.notifier).markReachable()),
      onUnreachable: () => later(
        () => ref.read(networkStatusProvider.notifier).markUnreachable(),
      ),
    ),
  );
});

final identityApiProvider = Provider<IdentityApi>(
  (ref) => IdentityApi(ref.watch(apiClientProvider)),
);
