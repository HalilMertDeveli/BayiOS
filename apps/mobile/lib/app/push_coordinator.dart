import 'dart:async';

import 'package:bayios_mobile/app/router.dart';
import 'package:bayios_mobile/core/push/push_registration.dart';
import 'package:bayios_mobile/core/push/push_service.dart';
import 'package:bayios_mobile/core/session/session_controller.dart';
import 'package:bayios_mobile/l10n/app_localizations.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

/// A tapped notification waiting until the session can open it.
final pendingPushTapProvider = NotifierProvider<PendingPushTap, PushTap?>(
  PendingPushTap.new,
);

class PendingPushTap extends Notifier<PushTap?> {
  @override
  PushTap? build() => null;

  PushTap? get value => state;
  set value(PushTap? tap) => state = tap;
}

/// Wires push into the app: keeps [pushRegistrationProvider] alive, turns a
/// tapped notification into navigation to `/gorevler/:id` — only once auth
/// and the tenant are restored ([SessionReady]); a task of another business
/// the user belongs to switches to it first, one they do not belong to is
/// dropped — and shows notifications arriving in the foreground as a banner
/// with "Aç". The router's guards still apply (no permission → home).
class PushCoordinator extends ConsumerStatefulWidget {
  const PushCoordinator({required this.child, super.key});

  final Widget child;

  @override
  ConsumerState<PushCoordinator> createState() => _PushCoordinatorState();
}

class _PushCoordinatorState extends ConsumerState<PushCoordinator> {
  final _subscriptions = <StreamSubscription<Object?>>[];
  String? _switchingTo;

  @override
  void initState() {
    super.initState();
    final push = ref.read(pushServiceProvider);
    if (!push.available) return;
    _subscriptions
      ..add(push.taps.listen(_open))
      ..add(push.foregroundMessages.listen(_onForeground));
    unawaited(
      push.initialTap().then((tap) {
        if (tap != null && mounted) _open(tap);
      }),
    );
  }

  @override
  void dispose() {
    for (final s in _subscriptions) {
      unawaited(s.cancel());
    }
    super.dispose();
  }

  void _open(PushTap tap) {
    ref.read(pendingPushTapProvider.notifier).value = tap;
    _tryOpen();
  }

  void _tryOpen() {
    final tap = ref.read(pendingPushTapProvider);
    final session = ref.read(sessionControllerProvider);
    if (tap == null || session is! SessionReady) return;

    final tenantId = tap.tenantId;
    if (tenantId != null && tenantId != session.tenantId) {
      final member = session.memberships.any(
        (m) => m.tenantId == tenantId && m.isTenantUsable,
      );
      if (!member) {
        ref.read(pendingPushTapProvider.notifier).value = null;
        return;
      }
      if (_switchingTo != tenantId) {
        _switchingTo = tenantId;
        unawaited(
          ref.read(sessionControllerProvider.notifier).selectTenant(tenantId),
        );
      }
      return; // opened when the session is ready in that business
    }

    _switchingTo = null;
    ref.read(pendingPushTapProvider.notifier).value = null;
    ref.read(routerProvider).go(tap.location);
  }

  void _onForeground(ForegroundPush message) {
    final messenger = ScaffoldMessenger.maybeOf(context);
    if (messenger == null) return;
    final l10n = AppLocalizations.of(context);
    final tap = message.tap;
    messenger.showSnackBar(
      SnackBar(
        key: const Key('push-foreground'),
        duration: const Duration(seconds: 8),
        content: Text(
          [message.title, message.body].where((t) => t.isNotEmpty).join('\n'),
        ),
        action: tap == null
            ? null
            : SnackBarAction(label: l10n.pushOpen, onPressed: () => _open(tap)),
      ),
    );
  }

  @override
  Widget build(BuildContext context) {
    ref
      ..watch(pushRegistrationProvider)
      ..listen(sessionControllerProvider, (_, _) => _tryOpen());
    return widget.child;
  }
}
