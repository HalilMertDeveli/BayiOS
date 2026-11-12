import 'package:bayios_mobile/app/feature.dart';
import 'package:bayios_mobile/app/features.dart';
import 'package:bayios_mobile/core/api/api_exception.dart';
import 'package:bayios_mobile/core/auth/auth_controller.dart';
import 'package:bayios_mobile/core/providers.dart';
import 'package:bayios_mobile/core/session/active_tenant_store.dart';
import 'package:bayios_mobile/core/theme/app_icon.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';

import '../helpers/fakes.dart';
import '../helpers/pump.dart';

/// A feature that only `tasks.pick` holders see (role-aware navigation).
final _pickingFeature = AppFeature(
  id: 'picking',
  path: '/gorevler',
  label: (_) => 'Görevler',
  icon: AppIcons.document,
  isVisible: (access) => access.canSome('tasks.pick'),
  builder: (_, _) => const Scaffold(body: Text('picking-screen')),
);

void main() {
  late FakeAuthRepository auth;
  late FakeIdentityApi identity;
  late MemoryActiveTenantStore store;
  late List<String> permissions;

  setUp(() {
    auth = FakeAuthRepository();
    store = MemoryActiveTenantStore();
    permissions = ['tenant.read', 'tasks.pick'];
    identity = FakeIdentityApi(
      (tenantId) async => meFixture(
        memberships: [membershipJson('t1', name: 'Demo Motor Depo')],
        activeTenant: tenantId == 't1'
            ? activeTenantJson('t1', permissions: permissions)
            : null,
      ),
    );
  });

  Future<void> pump(WidgetTester tester, {List<AppFeature>? features}) =>
      pumpApp(
        tester,
        overrides: [
          authRepositoryProvider.overrideWithValue(auth),
          identityApiProvider.overrideWithValue(identity),
          activeTenantStoreProvider.overrideWithValue(store),
          if (features != null) appFeaturesProvider.overrideWithValue(features),
        ],
      );

  Future<void> signIn(WidgetTester tester) async {
    await tester.enterText(
      find.byKey(const Key('sign-in-email')),
      'depo@ornek.test',
    );
    await tester.enterText(find.byKey(const Key('sign-in-password')), 'pw-1');
    await tester.tap(find.byKey(const Key('sign-in-submit')));
    await tester.pumpAndSettle();
  }

  testWidgets('sign in → tenant auto-selected → dashboard → sign out', (
    tester,
  ) async {
    await pump(tester);
    await tester.pumpAndSettle();
    expect(find.byKey(const Key('sign-in-submit')), findsOneWidget);

    await signIn(tester);

    expect(find.text(l10n.dashboardTitle), findsOneWidget);
    expect(find.text('Demo Motor Depo'), findsOneWidget);
    expect(find.text(l10n.roleWarehouseStaff), findsOneWidget);
    expect(find.text(l10n.dashboardWelcome('Ayşe Depocu')), findsOneWidget);
    await expectAccessibleTargets(tester);

    await tester.tap(find.byKey(const Key('nav-profile')));
    await tester.pumpAndSettle();
    expect(find.widgetWithText(AppBar, l10n.profileTitle), findsOneWidget);
    await expectAccessibleTargets(tester);

    await tester.tap(find.byKey(const Key('profile-sign-out')));
    await tester.pumpAndSettle();
    expect(find.byKey(const Key('sign-in-submit')), findsOneWidget);
    expect(auth.signOutCalls, 1);
  });

  testWidgets('navigation follows the API permissions, not role names', (
    tester,
  ) async {
    final features = [dashboardFeatureFor(), _pickingFeature, profileFor()];
    await pump(tester, features: features);
    await tester.pumpAndSettle();
    await signIn(tester);
    expect(find.byKey(const Key('nav-picking')), findsOneWidget);

    await tester.tap(find.byKey(const Key('nav-picking')));
    await tester.pumpAndSettle();
    expect(find.text('picking-screen'), findsOneWidget);
  });

  testWidgets('without the permission the destination is hidden', (
    tester,
  ) async {
    permissions = ['tenant.read'];
    final features = [dashboardFeatureFor(), _pickingFeature, profileFor()];
    await pump(tester, features: features);
    await tester.pumpAndSettle();
    await signIn(tester);
    expect(find.byKey(const Key('nav-picking')), findsNothing);
    expect(find.byKey(const Key('nav-dashboard')), findsOneWidget);
  });

  testWidgets('failed /me → retry pattern → dashboard', (tester) async {
    var fail = true;
    final ok = identity.respond;
    identity.respond = (tenantId) async {
      if (fail) throw ApiException.network();
      return await ok(tenantId);
    };
    await pump(tester);
    await tester.pumpAndSettle();
    await signIn(tester);

    expect(find.text(l10n.errorNetwork), findsOneWidget);
    fail = false;
    await tester.tap(find.text(l10n.retry));
    await tester.pumpAndSettle();
    expect(find.text(l10n.dashboardTitle), findsOneWidget);
  });

  testWidgets('offline banner appears and clears', (tester) async {
    await pump(tester);
    await tester.pumpAndSettle();
    final container = ProviderScope.containerOf(
      tester.element(find.byType(MaterialApp)),
    );

    container.read(networkStatusProvider.notifier).markUnreachable();
    await tester.pumpAndSettle();
    expect(find.text(l10n.offlineBanner), findsOneWidget);
    await expectAccessibleTargets(tester);

    container.read(networkStatusProvider.notifier).markReachable();
    await tester.pumpAndSettle();
    expect(find.text(l10n.offlineBanner), findsNothing);
  });

  testWidgets('several businesses → picker → dashboard', (tester) async {
    identity.respond = (tenantId) async => meFixture(
      memberships: [
        membershipJson('t1', name: 'Birinci'),
        membershipJson('t2', name: 'İkinci', role: 'manager'),
      ],
      activeTenant: tenantId == null
          ? null
          : activeTenantJson(
              tenantId,
              role: tenantId == 't2' ? 'manager' : 'warehouse_staff',
            ),
    );
    await pump(tester);
    await tester.pumpAndSettle();
    await signIn(tester);

    expect(find.text(l10n.tenantPickerTitle), findsOneWidget);
    await tester.tap(find.byKey(const Key('tenant-t2')));
    await tester.pumpAndSettle();
    expect(find.text('İkinci'), findsOneWidget);
    expect(find.text(l10n.roleManager), findsOneWidget);
    expect(identity.calls.last, 't2');
  });
}

AppFeature dashboardFeatureFor() =>
    appFeatures.firstWhere((f) => f.id == 'dashboard');

AppFeature profileFor() => appFeatures.firstWhere((f) => f.id == 'profile');
