import 'dart:async';

import 'package:flutter/foundation.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

/// Notification permission as the OS reports it.
enum PushPermission { granted, denied, notDetermined }

/// What a tapped (or foreground) notification points at. Built from the FCM
/// `data` the API sends (ids only, no personal data): `type`, `taskId`,
/// `tenantId`. Unknown types are ignored.
@immutable
class PushTap {
  const PushTap({required this.type, required this.taskId, this.tenantId});

  static const pickingTaskAssigned = 'picking_task_assigned';

  /// Null when the data is not a notification this app understands.
  static PushTap? fromData(Map<String, Object?> data) {
    final type = data['type'];
    final taskId = data['taskId'];
    final tenantId = data['tenantId'];
    if (type != pickingTaskAssigned || taskId is! String || taskId.isEmpty) {
      return null;
    }
    if (!_uuid.hasMatch(taskId)) return null;
    return PushTap(
      type: type! as String,
      taskId: taskId,
      tenantId: tenantId is String && _uuid.hasMatch(tenantId)
          ? tenantId
          : null,
    );
  }

  static final _uuid = RegExp(
    '^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-'
    r'[0-9a-fA-F]{12}$',
  );

  final String type;
  final String taskId;

  /// The business the task belongs to; the app switches to it if needed.
  final String? tenantId;

  /// In-app route of the target (go_router, inside the signed-in shell).
  String get location => '/gorevler/$taskId';

  @override
  bool operator ==(Object other) =>
      other is PushTap &&
      other.type == type &&
      other.taskId == taskId &&
      other.tenantId == tenantId;

  @override
  int get hashCode => Object.hash(type, taskId, tenantId);
}

/// A notification that arrived while the app was in the foreground (the OS
/// shows nothing then; the app shows its own banner).
class ForegroundPush {
  const ForegroundPush({required this.title, required this.body, this.tap});

  final String title;
  final String body;
  final PushTap? tap;
}

/// Push messaging behind one interface (Firebase is used for notifications
/// only — CLAUDE.md). [NoopPushService] is used when the build has no
/// Firebase configuration (`BAYIOS_FIREBASE_*` defines), so the app works
/// the same without a Firebase project; tests use fakes.
abstract interface class PushService {
  /// False for the no-op service: nothing is registered or requested.
  bool get available;

  /// Asks the OS for permission (Android 13+ POST_NOTIFICATIONS, iOS alert).
  Future<PushPermission> requestPermission();

  /// The FCM registration token of this install, or null if none yet.
  Future<String?> token();

  /// New tokens (FCM rotates them); the app re-registers each one.
  Stream<String> get tokenRefreshes;

  /// Notifications tapped while the app ran in the background.
  Stream<PushTap> get taps;

  /// The notification that launched the app from terminated, if any.
  Future<PushTap?> initialTap();

  Stream<ForegroundPush> get foregroundMessages;

  /// Invalidates this install's token (sign-out): even a registration the API
  /// still holds then gets "unregistered" from FCM and is deleted.
  Future<void> deleteToken();
}

class NoopPushService implements PushService {
  const NoopPushService();

  @override
  bool get available => false;

  @override
  Future<PushPermission> requestPermission() async => PushPermission.denied;

  @override
  Future<String?> token() async => null;

  @override
  Stream<String> get tokenRefreshes => const Stream.empty();

  @override
  Stream<PushTap> get taps => const Stream.empty();

  @override
  Future<PushTap?> initialTap() async => null;

  @override
  Stream<ForegroundPush> get foregroundMessages => const Stream.empty();

  @override
  Future<void> deleteToken() async {}
}

/// Overridden in bootstrap with the Firebase service when configured.
final pushServiceProvider = Provider<PushService>(
  (ref) => const NoopPushService(),
);
