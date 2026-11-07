import 'package:bayios_mobile/core/api/api_exception.dart';
import 'package:bayios_mobile/core/auth/auth_controller.dart';
import 'package:bayios_mobile/core/auth/auth_repository.dart';
import 'package:bayios_mobile/core/providers.dart';
import 'package:bayios_mobile/core/session/active_tenant_store.dart';
import 'package:bayios_mobile/core/session/me.dart';
import 'package:bayios_mobile/core/session/session_controller.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';

import '../../helpers/fakes.dart';

void main() {
  late FakeAuthRepository auth;
  late MemoryActiveTenantStore store;
  late FakeIdentityApi identity;

  ProviderContainer create() => ProviderContainer.test(
    overrides: [
      authRepositoryProvider.overrideWithValue(auth),
      activeTenantStoreProvider.overrideWithValue(store),
      identityApiProvider.overrideWithValue(identity),
    ],
  );

  /// Lets the session controller finish its asynchronous `/me` loads.
  Future<SessionState> settle(ProviderContainer c) async {
    c.read(sessionControllerProvider); // build it (providers are lazy)
    for (var i = 0; i < 10; i++) {
      await Future<void>.delayed(Duration.zero);
    }
    final state = c.read(sessionControllerProvider);
    return state;
  }

  /// `/me` for a user with [memberships]; `activeTenant` follows the header
  /// for usable tenants.
  Future<Me> Function(String?) meWith(
    List<Map<String, Object?>> memberships, {
    Set<String> unusable = const {},
  }) =>
      (tenantId) async => meFixture(
        memberships: memberships,
        activeTenant:
            tenantId != null &&
                !unusable.contains(tenantId) &&
                memberships.any((m) => m['tenantId'] == tenantId)
            ? activeTenantJson(
                tenantId,
                role:
                    memberships.firstWhere(
                          (m) => m['tenantId'] == tenantId,
                        )['role']!
                        as String,
              )
            : null,
      );

  setUp(() {
    auth = FakeAuthRepository(user: const AuthUser(id: 'user-1'));
    store = MemoryActiveTenantStore();
    identity = FakeIdentityApi(meWith([membershipJson('t1')]));
  });

  test('signed out → SessionSignedOut, no API call', () async {
    auth.user = null;
    final c = create();
    expect(await settle(c), isA<SessionSignedOut>());
    expect(identity.calls, isEmpty);
  });

  test('a single membership is selected automatically', () async {
    final c = create();
    expect(c.read(sessionControllerProvider), isA<SessionLoading>());

    final state = await settle(c);
    expect(state, isA<SessionReady>());
    final ready = state as SessionReady;
    expect(ready.tenantId, 't1');
    expect(ready.role, 'warehouse_staff');
    expect(ready.access.can('tasks.pick'), isTrue);
    expect(ready.access.canSome('orders.approve'), isFalse);
    // First /me without a tenant, then for the chosen one (permissions).
    expect(identity.calls, [null, 't1']);
    expect(c.read(activeTenantIdProvider), 't1');
    expect(store.values['user-1'], 't1');
  });

  test('a stored selection is used and asked for directly', () async {
    identity.respond = meWith([
      membershipJson('t1'),
      membershipJson('t2', name: 'İkinci', role: 'manager'),
    ]);
    store.values['user-1'] = 't2';
    final c = create();

    final ready = await settle(c) as SessionReady;
    expect(ready.tenantId, 't2');
    expect(ready.role, 'manager');
    expect(identity.calls, ['t2']);
  });

  test('several memberships without a selection → picker → select', () async {
    identity.respond = meWith([
      membershipJson('t1'),
      membershipJson('t2', name: 'İkinci'),
      membershipJson('t3', status: 'disabled'),
    ]);
    final c = create();

    final picking = await settle(c) as SessionNeedsTenant;
    expect(picking.memberships.map((m) => m.tenantId), ['t1', 't2']);
    expect(c.read(activeTenantIdProvider), isNull);

    await c.read(sessionControllerProvider.notifier).selectTenant('t2');
    final ready = c.read(sessionControllerProvider) as SessionReady;
    expect(ready.tenantName, 'İkinci');
    expect(c.read(activeTenantIdProvider), 't2');
    expect(store.values['user-1'], 't2');
  });

  test('no active membership → SessionNoMembership', () async {
    identity.respond = meWith([membershipJson('t1', status: 'disabled')]);
    final c = create();
    expect(await settle(c), isA<SessionNoMembership>());
  });

  test('a suspended business is not opened', () async {
    identity.respond = meWith([
      membershipJson('t1', tenantStatus: 'suspended'),
    ]);
    store.values['user-1'] = 't1';
    final c = create();

    final state = await settle(c) as SessionNeedsTenant;
    expect(state.inactiveNotice, isTrue);
    expect(store.values, isEmpty);
    expect(c.read(activeTenantIdProvider), isNull);
  });

  test('membership the API will not open (no activeTenant)', () async {
    identity.respond = meWith([membershipJson('t1')], unusable: {'t1'});
    final c = create();
    final state = await settle(c) as SessionNeedsTenant;
    expect(state.inactiveNotice, isTrue);
  });

  test('a failed load offers retry', () async {
    var fail = true;
    identity.respond = (tenantId) async {
      if (fail) throw ApiException.network();
      return await meWith([membershipJson('t1')])(tenantId);
    };
    final c = create();
    final failed = await settle(c) as SessionFailed;
    expect((failed.error as ApiException).kind, ApiErrorKind.network);

    fail = false;
    await c.read(sessionControllerProvider.notifier).reload();
    expect(c.read(sessionControllerProvider), isA<SessionReady>());
  });

  test('403 tenant.access_denied forgets the selection and reloads', () async {
    identity.respond = meWith([membershipJson('t1'), membershipJson('t2')]);
    store.values['user-1'] = 't1';
    final c = create();
    expect(await settle(c), isA<SessionReady>());

    await c.read(sessionControllerProvider.notifier).tenantAccessDenied();
    expect(c.read(sessionControllerProvider), isA<SessionNeedsTenant>());
    expect(store.values, isEmpty);
    expect(c.read(activeTenantIdProvider), isNull);
  });

  test('403 tenant.inactive shows the picker with a notice', () async {
    final c = create();
    expect(await settle(c), isA<SessionReady>());
    c.read(sessionControllerProvider.notifier).tenantInactive();
    final state = c.read(sessionControllerProvider) as SessionNeedsTenant;
    expect(state.inactiveNotice, isTrue);
    expect(c.read(activeTenantIdProvider), isNull);
  });

  test('switch tenant keeps the current one marked', () async {
    identity.respond = meWith([membershipJson('t1'), membershipJson('t2')]);
    store.values['user-1'] = 't1';
    final c = create();
    expect(await settle(c), isA<SessionReady>());
    c.read(sessionControllerProvider.notifier).switchTenant();
    final state = c.read(sessionControllerProvider) as SessionNeedsTenant;
    expect(state.currentTenantId, 't1');
  });

  test('sign-out clears the tenant', () async {
    final c = create();
    expect(await settle(c), isA<SessionReady>());
    await c.read(authControllerProvider.notifier).signOut();
    expect(await settle(c), isA<SessionSignedOut>());
    expect(c.read(activeTenantIdProvider), isNull);
  });
}
