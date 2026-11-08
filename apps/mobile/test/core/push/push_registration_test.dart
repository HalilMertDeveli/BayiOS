import 'package:bayios_mobile/core/auth/auth_controller.dart';
import 'package:bayios_mobile/core/auth/auth_repository.dart';
import 'package:bayios_mobile/core/providers.dart';
import 'package:bayios_mobile/core/push/push_registration.dart';
import 'package:bayios_mobile/core/push/push_service.dart';
import 'package:bayios_mobile/core/session/active_tenant_store.dart';
import 'package:bayios_mobile/core/session/session_controller.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';

import '../../helpers/fakes.dart';
import '../../helpers/push_fakes.dart';

void main() {
  late FakeAuthRepository auth;
  late FakeIdentityApi identity;
  late MemoryActiveTenantStore store;
  late FakePushService push;
  late FakeDeviceApi devices;

  setUp(() {
    auth = FakeAuthRepository(user: const AuthUser(id: 'user-1'));
    store = MemoryActiveTenantStore();
    push = FakePushService();
    devices = FakeDeviceApi();
    identity = FakeIdentityApi(
      (tenantId) async => meFixture(
        memberships: [membershipJson('t1'), membershipJson('t2')],
        activeTenant: tenantId == null ? null : activeTenantJson(tenantId),
      ),
    );
  });

  ProviderContainer create({PushService? service}) {
    final c = ProviderContainer.test(
      overrides: [
        appEnvProvider.overrideWithValue(testEnv),
        authRepositoryProvider.overrideWithValue(auth),
        identityApiProvider.overrideWithValue(identity),
        activeTenantStoreProvider.overrideWithValue(store),
        pushServiceProvider.overrideWithValue(service ?? push),
        deviceApiProvider.overrideWithValue(devices),
      ],
    )..listen(pushRegistrationProvider, (_, _) {});
    return c;
  }

  Future<void> settle() async {
    for (var i = 0; i < 20; i++) {
      await Future<void>.delayed(Duration.zero);
    }
  }

  Future<ProviderContainer> readyIn(String tenantId) async {
    await store.write('user-1', tenantId);
    final c = create();
    await settle();
    expect(c.read(sessionControllerProvider), isA<SessionReady>());
    return c;
  }

  test('without Firebase config nothing is asked or registered', () async {
    await store.write('user-1', 't1');
    final c = create(service: const NoopPushService());
    await settle();
    expect(c.read(pushRegistrationProvider), isA<PushUnavailable>());
    expect(devices.registers, isEmpty);
    await c.read(authControllerProvider.notifier).signOut();
    expect(devices.unregisters, isEmpty);
    expect(auth.signOutCalls, 1);
  });

  test('after tenant selection: permission asked once, token registered '
      'in that tenant', () async {
    final c = await readyIn('t1');
    expect(push.permissionRequests, 1);
    final call = devices.registers.single;
    expect(
      (call.token, call.tenantId, call.platform),
      ('fcm-token-1', 't1', 'android'),
    );
    final state = c.read(pushRegistrationProvider);
    expect(state, isA<PushRegistered>());
    expect((state as PushRegistered).deviceId, 'dev-t1-fcm-token-1');

    // A reload of the same business changes nothing.
    await c.read(sessionControllerProvider.notifier).reload();
    await settle();
    expect(devices.registers, hasLength(1));
    expect(push.permissionRequests, 1);
  });

  test('permission denied: no registration', () async {
    push.permission = PushPermission.denied;
    final c = await readyIn('t1');
    expect(devices.registers, isEmpty);
    expect(c.read(pushRegistrationProvider), isA<PushPermissionDenied>());
  });

  test(
    'token refresh: new token registered, old registration removed',
    () async {
      await readyIn('t1');
      push.refreshToken('fcm-token-2');
      await settle();
      expect(devices.registers.map((r) => r.token), [
        'fcm-token-1',
        'fcm-token-2',
      ]);
      expect(devices.unregisters.single, (
        id: 'dev-t1-fcm-token-1',
        tenantId: 't1',
      ));
    },
  );

  test('tenant switch: registered in the new business, removed from the '
      'old one (with its tenant header)', () async {
    final c = await readyIn('t1');
    c.read(sessionControllerProvider.notifier).switchTenant();
    await settle();
    expect(devices.unregisters, isEmpty, reason: 'still in t1 while picking');
    await c.read(sessionControllerProvider.notifier).selectTenant('t2');
    await settle();
    expect(devices.registers.map((r) => r.tenantId), ['t1', 't2']);
    expect(devices.unregisters.single, (
      id: 'dev-t1-fcm-token-1',
      tenantId: 't1',
    ));
    expect((c.read(pushRegistrationProvider) as PushRegistered).tenantId, 't2');
  });

  test('offline registration is retried with the same key when the API is '
      'reachable again', () async {
    devices.failNextRegisters = 1;
    final c = await readyIn('t1');
    expect(c.read(pushRegistrationProvider), isA<PushRegistrationFailed>());

    c.read(networkStatusProvider.notifier).markUnreachable();
    c.read(networkStatusProvider.notifier).markReachable();
    await settle();
    expect(devices.registers, hasLength(2));
    expect(devices.registers[1].key, devices.registers[0].key);
    expect(c.read(pushRegistrationProvider), isA<PushRegistered>());
  });

  test('sign-out removes the registration before the session ends and '
      'invalidates the FCM token', () async {
    final c = await readyIn('t1');
    auth.onSignOut = () => devices.events.add('auth signOut');
    await c.read(authControllerProvider.notifier).signOut();
    await settle();
    expect(
      devices.events,
      containsAllInOrder(<String>['unregister t1', 'auth signOut']),
    );
    expect(devices.unregisters.single, (
      id: 'dev-t1-fcm-token-1',
      tenantId: 't1',
    ));
    expect(push.deleteTokenCalls, 1);
    expect(c.read(authControllerProvider), isA<SignedOut>());
    expect(c.read(pushRegistrationProvider), isA<PushIdle>());
  });
}
