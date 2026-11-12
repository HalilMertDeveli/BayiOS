import 'dart:async';

import 'package:bayios_mobile/app/feature.dart';
import 'package:bayios_mobile/app/features.dart';
import 'package:bayios_mobile/core/auth/auth_controller.dart';
import 'package:bayios_mobile/core/auth/auth_repository.dart';
import 'package:bayios_mobile/core/providers.dart';
import 'package:bayios_mobile/core/push/push_registration.dart';
import 'package:bayios_mobile/core/push/push_service.dart';
import 'package:bayios_mobile/core/session/active_tenant_store.dart';
import 'package:bayios_mobile/core/theme/app_icon.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:go_router/go_router.dart';

import '../helpers/fakes.dart';
import '../helpers/pump.dart';
import '../helpers/push_fakes.dart';

const _task = '0193a000-0000-7000-8000-000000000001';
const _t1 = '0193a000-0000-7000-8000-0000000000a1';
const _t2 = '0193a000-0000-7000-8000-0000000000a2';
const _t3 = '0193a000-0000-7000-8000-0000000000a3';

final _home = AppFeature(
  id: 'home',
  path: '/pano',
  label: (_) => 'Pano',
  icon: AppIcons.document,
  builder: (_, _) => const Scaffold(body: Text('home-screen')),
);

final _tasks = AppFeature(
  id: 'tasks',
  path: '/gorevler',
  label: (_) => 'Görevler',
  icon: AppIcons.document,
  isVisible: (access) => access.canSome('tasks.read.own'),
  builder: (_, _) => const Scaffold(body: Text('tasks-screen')),
  routes: [
    GoRoute(
      path: ':id',
      builder: (_, state) =>
          Scaffold(body: Text('task ${state.pathParameters['id']}')),
    ),
  ],
);

/// Tapping a task notification opens `/gorevler/:id` — only after auth and
/// the tenant are restored, in the task's business (switching to it when the
/// user belongs to it), never for a business they do not belong to.
void main() {
  late FakeAuthRepository auth;
  late FakeIdentityApi identity;
  late MemoryActiveTenantStore store;
  late FakePushService push;

  setUp(() {
    auth = FakeAuthRepository(user: const AuthUser(id: 'user-1'));
    store = MemoryActiveTenantStore();
    push = FakePushService();
    identity = FakeIdentityApi(
      (tenantId) async => meFixture(
        memberships: [
          membershipJson(_t1, name: 'Birinci Depo'),
          membershipJson(_t2, name: 'İkinci Depo'),
        ],
        activeTenant: tenantId == null
            ? null
            : activeTenantJson(
                tenantId,
                permissions: ['tenant.read', 'tasks.read.own'],
              ),
      ),
    );
  });

  Future<void> pump(WidgetTester tester, {bool settle = true}) async {
    await store.write('user-1', _t1);
    await pumpApp(
      tester,
      overrides: [
        appEnvProvider.overrideWithValue(testEnv),
        authRepositoryProvider.overrideWithValue(auth),
        identityApiProvider.overrideWithValue(identity),
        activeTenantStoreProvider.overrideWithValue(store),
        appFeaturesProvider.overrideWithValue([_home, _tasks]),
        pushServiceProvider.overrideWithValue(push),
        deviceApiProvider.overrideWithValue(FakeDeviceApi()),
      ],
    );
    if (settle) await tester.pumpAndSettle();
  }

  testWidgets('tap in the background opens the task', (tester) async {
    await pump(tester);
    expect(find.text('home-screen'), findsOneWidget);

    push.tap(
      const PushTap(
        type: 'picking_task_assigned',
        taskId: _task,
        tenantId: _t1,
      ),
    );
    await tester.pumpAndSettle();
    expect(find.text('task $_task'), findsOneWidget);
  });

  testWidgets('cold start from a notification waits for the session', (
    tester,
  ) async {
    push.initial = const PushTap(
      type: 'picking_task_assigned',
      taskId: _task,
      tenantId: _t1,
    );
    final release = Completer<void>();
    final ok = identity.respond;
    identity.respond = (tenantId) async {
      await release.future;
      return await ok(tenantId);
    };
    await pump(tester, settle: false);
    await tester.pump();
    await tester.pump();
    expect(find.text('task $_task'), findsNothing, reason: 'session loading');

    release.complete();
    await tester.pumpAndSettle();
    expect(find.text('task $_task'), findsOneWidget);
  });

  testWidgets('a task of another business of the user switches to it first', (
    tester,
  ) async {
    await pump(tester);
    push.tap(
      const PushTap(
        type: 'picking_task_assigned',
        taskId: _task,
        tenantId: _t2,
      ),
    );
    await tester.pumpAndSettle();
    expect(find.text('task $_task'), findsOneWidget);
    expect(identity.calls.last, _t2);
    expect(await store.read('user-1'), _t2);
  });

  testWidgets('a business the user does not belong to is ignored', (
    tester,
  ) async {
    await pump(tester);
    push.tap(
      const PushTap(
        type: 'picking_task_assigned',
        taskId: _task,
        tenantId: _t3,
      ),
    );
    await tester.pumpAndSettle();
    expect(find.text('home-screen'), findsOneWidget);
    expect(identity.calls, isNot(contains(_t3)));
  });

  testWidgets('signed out: the tap waits for sign-in', (tester) async {
    auth.user = null;
    await pump(tester);
    expect(find.byKey(const Key('sign-in-submit')), findsOneWidget);
    push.tap(
      const PushTap(
        type: 'picking_task_assigned',
        taskId: _task,
        tenantId: _t1,
      ),
    );
    await tester.pumpAndSettle();
    expect(find.byKey(const Key('sign-in-submit')), findsOneWidget);

    await tester.enterText(
      find.byKey(const Key('sign-in-email')),
      'depo@ornek.test',
    );
    await tester.enterText(find.byKey(const Key('sign-in-password')), 'pw-1');
    await tester.tap(find.byKey(const Key('sign-in-submit')));
    await tester.pumpAndSettle();
    expect(find.text('task $_task'), findsOneWidget);
  });

  testWidgets('foreground notification: banner with "Aç" opens the task', (
    tester,
  ) async {
    await pump(tester);
    push.foreground(
      const ForegroundPush(
        title: 'Yeni toplama görevi',
        body: 'Size bir toplama görevi atandı. Açmak için dokunun.',
        tap: PushTap(
          type: 'picking_task_assigned',
          taskId: _task,
          tenantId: _t1,
        ),
      ),
    );
    await tester.pumpAndSettle();
    expect(find.byKey(const Key('push-foreground')), findsOneWidget);
    expect(find.textContaining('Yeni toplama görevi'), findsOneWidget);

    await tester.tap(find.text(l10n.pushOpen));
    await tester.pumpAndSettle();
    expect(find.text('task $_task'), findsOneWidget);
  });

  test('notification data: only known types with valid ids', () {
    expect(
      PushTap.fromData({
        'type': 'picking_task_assigned',
        'taskId': _task,
        'tenantId': _t1,
      }),
      const PushTap(
        type: 'picking_task_assigned',
        taskId: _task,
        tenantId: _t1,
      ),
    );
    expect(PushTap.fromData({'type': 'other', 'taskId': _task}), isNull);
    expect(
      PushTap.fromData({'type': 'picking_task_assigned', 'taskId': '../x'}),
      isNull,
    );
    expect(
      PushTap.fromData({
        'type': 'picking_task_assigned',
        'taskId': _task,
        'tenantId': 'x',
      })?.tenantId,
      isNull,
    );
  });
}
