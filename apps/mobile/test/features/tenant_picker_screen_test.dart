import 'package:bayios_mobile/core/auth/auth_controller.dart';
import 'package:bayios_mobile/core/auth/auth_repository.dart';
import 'package:bayios_mobile/core/session/session_controller.dart';
import 'package:bayios_mobile/features/tenant/tenant_picker_screen.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import '../helpers/fakes.dart';
import '../helpers/pump.dart';

class _FakeSession extends SessionController {
  _FakeSession(this.initial);

  final SessionState initial;
  final selected = <String>[];

  @override
  SessionState build() => initial;

  @override
  Future<void> selectTenant(String tenantId) async => selected.add(tenantId);
}

void main() {
  final me = meFixture(
    memberships: [
      membershipJson('t1', name: 'Motor Yedek Parça'),
      membershipJson('t2', name: 'LED Aydınlatma', role: 'manager'),
      membershipJson('t3', name: 'Eski Şube', tenantStatus: 'suspended'),
    ],
  );

  Future<_FakeSession> pump(
    WidgetTester tester, {
    bool inactiveNotice = false,
    String? current,
  }) async {
    final fake = _FakeSession(
      SessionNeedsTenant(
        me,
        me.memberships,
        inactiveNotice: inactiveNotice,
        currentTenantId: current,
      ),
    );
    await pumpScreen(
      tester,
      const TenantPickerScreen(),
      overrides: [
        authRepositoryProvider.overrideWithValue(
          FakeAuthRepository(user: const AuthUser(id: 'user-1')),
        ),
        sessionControllerProvider.overrideWith(() => fake),
      ],
    );
    return fake;
  }

  testWidgets('lists memberships with role labels', (tester) async {
    await pump(tester);
    expect(find.text('Motor Yedek Parça'), findsOneWidget);
    expect(find.text('LED Aydınlatma'), findsOneWidget);
    expect(find.text(l10n.roleWarehouseStaff), findsOneWidget);
    expect(find.text(l10n.roleManager), findsOneWidget);
  });

  testWidgets('tapping a business selects it', (tester) async {
    final fake = await pump(tester);
    await tester.tap(find.byKey(const Key('tenant-t2')));
    await tester.pump();
    expect(fake.selected, ['t2']);
  });

  testWidgets('a suspended business is shown but cannot be chosen', (
    tester,
  ) async {
    final fake = await pump(tester);
    expect(
      find.text('${l10n.roleWarehouseStaff} · ${l10n.tenantStatusSuspended}'),
      findsOneWidget,
    );
    final tile = tester.widget<ListTile>(find.byKey(const Key('tenant-t3')));
    expect(tile.enabled, isFalse);
    await tester.tap(find.byKey(const Key('tenant-t3')));
    await tester.pump();
    expect(fake.selected, isEmpty);
  });

  testWidgets('inactive notice and current tenant marker', (tester) async {
    await pump(tester, inactiveNotice: true, current: 't1');
    expect(find.text(l10n.tenantInactiveNotice), findsOneWidget);
    expect(
      find.text('${l10n.roleWarehouseStaff} · ${l10n.tenantCurrent}'),
      findsOneWidget,
    );
  });

  testWidgets('sign out from the picker', (tester) async {
    await pump(tester);
    expect(find.byKey(const Key('tenant-picker-sign-out')), findsOneWidget);
  });

  testWidgets('touch targets ≥ 48 dp, labelled, readable', (tester) async {
    await pump(tester);
    await expectAccessibleTargets(tester);
  });
}
