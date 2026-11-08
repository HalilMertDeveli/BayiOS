import 'dart:async';

import 'package:bayios_mobile/core/api/idempotency.dart';
import 'package:bayios_mobile/core/auth/auth_controller.dart';
import 'package:bayios_mobile/core/providers.dart';
import 'package:bayios_mobile/core/push/device_api.dart';
import 'package:bayios_mobile/core/push/push_service.dart';
import 'package:bayios_mobile/core/session/session_controller.dart';
import 'package:flutter/foundation.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

/// Where this install stands with push in the active tenant.
sealed class PushRegistrationState {
  const PushRegistrationState();
}

/// No Firebase configuration in this build: nothing is requested or sent.
final class PushUnavailable extends PushRegistrationState {
  const PushUnavailable();
}

final class PushIdle extends PushRegistrationState {
  const PushIdle();
}

/// The user declined notifications: no registration (nothing would show).
final class PushPermissionDenied extends PushRegistrationState {
  const PushPermissionDenied();
}

final class PushRegistered extends PushRegistrationState {
  const PushRegistered({required this.tenantId, required this.deviceId});

  final String tenantId;
  final String deviceId;
}

/// Registration failed (e.g. offline); retried when the API is reachable
/// again, with the same Idempotency-Key.
final class PushRegistrationFailed extends PushRegistrationState {
  const PushRegistrationFailed({required this.tenantId, required this.error});

  final String tenantId;
  final Object error;
}

final deviceApiProvider = Provider<DeviceApi>(
  (ref) => DeviceApi(ref.watch(apiClientProvider)),
);

/// How long sign-out waits for the unregistration before it signs out anyway.
final pushSignOutTimeoutProvider = Provider<Duration>(
  (ref) => const Duration(seconds: 5),
);

final pushRegistrationProvider =
    NotifierProvider<PushRegistrationController, PushRegistrationState>(
      PushRegistrationController.new,
    );

/// Keeps this install's FCM token registered with the API for the active
/// tenant (`POST /api/v1/devices`):
/// - after sign-in and tenant selection ([SessionReady]): the notification
///   permission is asked once (a sensible moment: the user is in, in a
///   business, about to get tasks), then the token is registered;
/// - on token refresh: the new token is registered, the old one removed;
/// - on tenant switch: registered in the new tenant, removed from the old;
/// - on sign-out ([prepareSignOut], a [beforeSignOutProvider] step): removed
///   and the token invalidated with FCM.
/// Everything is best effort and never blocks the UI; operations run one at a
/// time. With [NoopPushService] (no Firebase config) it does nothing.
class PushRegistrationController extends Notifier<PushRegistrationState> {
  Future<void> _queue = Future.value();
  bool _permissionAsked = false;
  PushPermission? _permission;
  String? _targetTenant;
  _Registration? _registered;
  _PendingRegistration? _pending;

  PushService get _push => ref.read(pushServiceProvider);

  @override
  PushRegistrationState build() {
    final push = ref.watch(pushServiceProvider);
    if (!push.available) return const PushUnavailable();

    final refreshes = push.tokenRefreshes.listen(
      (token) => _enqueue(() => _onTokenRefresh(token)),
    );
    final hooks = ref.read(beforeSignOutProvider)..add(prepareSignOut);
    ref
      ..onDispose(() => unawaited(refreshes.cancel()))
      ..onDispose(() => hooks.remove(prepareSignOut))
      ..listen<SessionState>(
        sessionControllerProvider,
        (_, next) => _onSession(next),
        fireImmediately: true,
      )
      ..listen<bool>(networkStatusProvider, (previous, next) {
        if (previous == false && next && state is PushRegistrationFailed) {
          _enqueue(_sync);
        }
      });
    return const PushIdle();
  }

  void _onSession(SessionState session) {
    switch (session) {
      case SessionReady(:final tenantId):
        _targetTenant = tenantId;
        _enqueue(_sync);
      case SessionSignedOut():
        // prepareSignOut already removed the registration (if it could).
        _targetTenant = null;
        _registered = null;
        _pending = null;
      // Loading / picker / failure: keep the registration until the next
      // business is confirmed (a reload of the same one changes nothing).
      case _:
        break;
    }
  }

  /// Called by sign-out *before* the session ends (the API call needs it):
  /// removes this install's registration and invalidates the FCM token.
  /// Bounded by [pushSignOutTimeoutProvider]; failures are ignored (the API
  /// drops stale registrations, and FCM reports the dead token).
  Future<void> prepareSignOut() async {
    if (!_push.available) return;
    final timeout = ref.read(pushSignOutTimeoutProvider);
    _targetTenant = null;
    try {
      await _queue.timeout(timeout ~/ 2);
    } on Object {
      // A slow registration must not hold up sign-out.
    }
    final registered = _registered;
    _registered = null;
    _pending = null;
    final api = ref.read(deviceApiProvider);
    final push = _push;
    // In parallel: the API row (needs the session) and the FCM token.
    await Future.wait([
      if (registered != null)
        _quietly(
          () => api.unregister(
            registered.deviceId,
            tenantId: registered.tenantId,
          ),
          timeout,
        ),
      _quietly(push.deleteToken, timeout),
    ]);
    if (ref.mounted) state = const PushIdle();
  }

  void _enqueue(Future<void> Function() operation) {
    _queue = _queue.then((_) async {
      if (!ref.mounted) return;
      try {
        await operation();
      } on Object catch (error) {
        debugPrint('push registration: ${error.runtimeType}');
      }
    });
  }

  Future<void> _sync() async {
    final target = _targetTenant;
    if (target == null) return;
    final registered = _registered;
    if (registered != null && registered.tenantId == target) return;

    if (!_permissionAsked) {
      _permissionAsked = true;
      _permission = await _push.requestPermission();
    }
    if (_permission == PushPermission.denied) {
      if (registered != null) await _unregister(registered);
      state = const PushPermissionDenied();
      return;
    }

    final token = await _push.token();
    if (token == null || _targetTenant != target) return;
    if (await _register(target, token) && registered != null) {
      // Switched business: the old one no longer notifies this phone.
      await _unregister(registered);
    }
  }

  Future<void> _onTokenRefresh(String token) async {
    final registered = _registered;
    if (registered == null || registered.token == token) return;
    if (await _register(registered.tenantId, token)) {
      final now = _registered;
      if (now != null && now.deviceId != registered.deviceId) {
        await _unregister(registered);
      }
    }
  }

  /// Same tenant + token as a failed attempt → the same Idempotency-Key.
  Future<bool> _register(String tenantId, String token) async {
    final pending = _pending;
    final action = pending != null && pending.matches(tenantId, token)
        ? pending.action
        : IdempotentAction<DeviceRegistration>(
            (key) => ref
                .read(deviceApiProvider)
                .register(
                  token: token,
                  platform: _platform(),
                  appVersion: ref.read(appEnvProvider).clientVersion,
                  locale: 'tr-TR',
                  tenantId: tenantId,
                  key: key,
                ),
          );
    try {
      final device = await action.run();
      _pending = null;
      _registered = _Registration(tenantId, device.id, token);
      state = PushRegistered(tenantId: tenantId, deviceId: device.id);
      return true;
    } on Object catch (error) {
      _pending = _PendingRegistration(tenantId, token, action);
      state = PushRegistrationFailed(tenantId: tenantId, error: error);
      return false;
    }
  }

  Future<void> _unregister(_Registration registration) => _quietly(
    () => ref
        .read(deviceApiProvider)
        .unregister(registration.deviceId, tenantId: registration.tenantId),
    ref.read(pushSignOutTimeoutProvider),
  );

  static Future<void> _quietly(
    Future<void> Function() action,
    Duration timeout,
  ) async {
    try {
      await action().timeout(timeout);
    } on Object {
      // Best effort (see prepareSignOut).
    }
  }

  static String _platform() =>
      defaultTargetPlatform == TargetPlatform.iOS ? 'ios' : 'android';
}

class _Registration {
  const _Registration(this.tenantId, this.deviceId, this.token);

  final String tenantId;
  final String deviceId;
  final String token;
}

class _PendingRegistration {
  const _PendingRegistration(this.tenantId, this.token, this.action);

  final String tenantId;
  final String token;
  final IdempotentAction<DeviceRegistration> action;

  bool matches(String tenant, String value) =>
      tenant == tenantId && value == token;
}
