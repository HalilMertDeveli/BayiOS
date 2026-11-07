import 'dart:async';

import 'package:bayios_mobile/core/auth/auth_controller.dart';
import 'package:bayios_mobile/core/providers.dart';
import 'package:bayios_mobile/core/session/access.dart';
import 'package:bayios_mobile/core/session/me.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

/// Where the signed-in user stands with respect to tenants.
sealed class SessionState {
  const SessionState();
}

final class SessionSignedOut extends SessionState {
  const SessionSignedOut();
}

final class SessionLoading extends SessionState {
  const SessionLoading();
}

/// `GET /me` failed (offline, 5xx …); the UI offers "Tekrar dene".
final class SessionFailed extends SessionState {
  const SessionFailed(this.error);

  final Object error;
}

/// No active membership: tenants are created / invitations accepted on the
/// web (mobile is for warehouse staff).
final class SessionNoMembership extends SessionState {
  const SessionNoMembership(this.me);

  final Me me;
}

/// Several memberships and no valid selection, or the selected business is
/// suspended / closed ([inactiveNotice]).
final class SessionNeedsTenant extends SessionState {
  const SessionNeedsTenant(
    this.me,
    this.memberships, {
    this.inactiveNotice = false,
    this.currentTenantId,
  });

  final Me me;

  /// Active memberships (the picker shows unusable businesses disabled).
  final List<Membership> memberships;
  final bool inactiveNotice;

  /// The tenant in use when the user chose "İşletme değiştir".
  final String? currentTenantId;
}

/// Tenant confirmed by the API: role, effective permissions, entitlements.
final class SessionReady extends SessionState {
  SessionReady({
    required this.me,
    required this.memberships,
    required this.membership,
    required this.activeTenant,
  }) : access = Access(activeTenant.permissions);

  final Me me;
  final List<Membership> memberships;
  final Membership membership;
  final ActiveTenant activeTenant;
  final Access access;

  String get tenantId => membership.tenantId;
  String get tenantName => membership.tenantName;

  /// Role from `activeTenant` (authoritative for this tenant), display only.
  String get role => activeTenant.role;
}

final sessionControllerProvider =
    NotifierProvider<SessionController, SessionState>(SessionController.new);

/// Loads `GET /api/v1/me`, resolves the active tenant (stored selection →
/// the only membership → picker) and asks the API for that tenant's
/// effective permissions. Nothing here is authorization; the API re-checks
/// token, membership, tenant status and permission on every request.
class SessionController extends Notifier<SessionState> {
  int _generation = 0;

  @override
  SessionState build() {
    final auth = ref.watch(authControllerProvider);
    _generation++; // drop results of a load for the previous user
    // Providers may not modify other providers while building.
    unawaited(
      Future.microtask(() async {
        if (!ref.mounted) return;
        ref.read(activeTenantIdProvider.notifier).value = null;
        if (auth is SignedIn) await _load(auth.user.id);
      }),
    );
    return auth is SignedIn ? const SessionLoading() : const SessionSignedOut();
  }

  /// Reloads after a failure ("Tekrar dene") or a membership change.
  Future<void> reload() async {
    final userId = _userId;
    if (userId == null) return;
    state = const SessionLoading();
    await _load(userId);
  }

  /// The user picked a business. Another tenant means other permissions, so
  /// `/me` is asked again for it before anything of that tenant is shown.
  Future<void> selectTenant(String tenantId) async {
    final userId = _userId;
    if (userId == null) return;
    state = const SessionLoading();
    await _load(userId, choose: tenantId);
  }

  /// Shows the picker without dropping the current session (profile →
  /// "İşletme değiştir").
  void switchTenant() {
    final current = state;
    if (current is SessionReady) {
      state = SessionNeedsTenant(
        current.me,
        current.memberships,
        currentTenantId: current.tenantId,
      );
    }
  }

  /// 403 `tenant.access_denied`: the membership is gone — forget the
  /// selection and reload (the picker shows what is left).
  Future<void> tenantAccessDenied() async {
    final userId = _userId;
    if (userId == null) return;
    await ref.read(activeTenantStoreProvider).clear(userId);
    ref.read(activeTenantIdProvider.notifier).value = null;
    state = const SessionLoading();
    await _load(userId);
  }

  /// 403 `tenant.inactive`: the business was suspended or closed meanwhile.
  void tenantInactive() {
    final current = state;
    if (current is! SessionReady) return;
    ref.read(activeTenantIdProvider.notifier).value = null;
    state = SessionNeedsTenant(
      current.me,
      current.memberships,
      inactiveNotice: true,
    );
  }

  String? get _userId => switch (ref.read(authControllerProvider)) {
    SignedIn(:final user) => user.id,
    SignedOut() => null,
  };

  Future<void> _load(String userId, {String? choose}) async {
    final generation = ++_generation;
    bool stale() => !ref.mounted || generation != _generation;

    final store = ref.read(activeTenantStoreProvider);
    final api = ref.read(identityApiProvider);
    try {
      final stored = choose ?? await store.read(userId);
      if (stale()) return;
      var me = await api.getMe(tenantId: stored);
      if (stale()) return;

      final memberships = me.memberships
          .where((m) => m.isActiveMembership)
          .toList();
      if (memberships.isEmpty) {
        state = SessionNoMembership(me);
        return;
      }

      final chosen = _resolve(memberships, stored);
      if (chosen == null) {
        state = SessionNeedsTenant(me, memberships);
        return;
      }
      if (!chosen.isTenantUsable) {
        await store.clear(userId);
        if (stale()) return;
        state = SessionNeedsTenant(me, memberships, inactiveNotice: true);
        return;
      }

      if (me.activeTenant?.tenantId != chosen.tenantId) {
        me = await api.getMe(tenantId: chosen.tenantId);
        if (stale()) return;
      }
      final granted = me.activeTenant;
      if (granted == null || granted.tenantId != chosen.tenantId) {
        // An active membership the API will not open: suspended or closed.
        await store.clear(userId);
        if (stale()) return;
        state = SessionNeedsTenant(me, memberships, inactiveNotice: true);
        return;
      }

      await store.write(userId, chosen.tenantId);
      if (stale()) return;
      ref.read(activeTenantIdProvider.notifier).value = chosen.tenantId;
      state = SessionReady(
        me: me,
        memberships: memberships,
        membership: chosen,
        activeTenant: granted,
      );
    } on Object catch (error) {
      if (stale()) return;
      state = SessionFailed(error);
    }
  }

  /// Stored (or just chosen) selection that is still a membership wins; a
  /// single membership is selected automatically; otherwise the user picks.
  static Membership? _resolve(List<Membership> memberships, String? stored) {
    if (stored != null) {
      for (final m in memberships) {
        if (m.tenantId == stored) return m;
      }
    }
    return memberships.length == 1 ? memberships.single : null;
  }
}
