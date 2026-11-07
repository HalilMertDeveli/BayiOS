import 'package:bayios_mobile/core/auth/auth_controller.dart';
import 'package:bayios_mobile/core/auth/auth_repository.dart';
import 'package:bayios_mobile/core/providers.dart';
import 'package:bayios_mobile/core/session/active_tenant_store.dart';
import 'package:bayios_mobile/features/picking/camera_scanner.dart';
import 'package:bayios_mobile/features/picking/picking_api.dart';
import 'package:bayios_mobile/features/picking/scan_alerts.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import '../../helpers/fakes.dart';
import '../../helpers/pump.dart';
import 'fake_picking_server.dart';

/// Effective permissions of the seeded roles (roles-permissions.md §3.6),
/// as `GET /me` would return them.
const staffPermissions = [
  'tenant.read',
  'tasks.read.own',
  'tasks.claim',
  'tasks.pick',
  'tasks.pack',
  'tasks.report_shortage',
  'orders.read',
];
const ownerPermissions = [
  'tenant.read',
  'tasks.read.all',
  'tasks.read.own',
  'tasks.assign',
  'tasks.pick',
  'tasks.pack',
  'tasks.report_shortage',
  'orders.read',
];
const viewerPermissions = ['tenant.read', 'tasks.read.all', 'orders.read'];

/// Scanner stand-in: returns the queued outcomes in order.
class FakeScanner {
  final outcomes = <ScanOutcome?>[];
  final shelfFlags = <bool>[];

  Future<ScanOutcome?> call(BuildContext context, {required bool shelf}) async {
    shelfFlags.add(shelf);
    return outcomes.isEmpty ? null : outcomes.removeAt(0);
  }
}

/// A phone-sized screen (411 × 891 dp) for the warehouse layouts.
void usePhoneSize(WidgetTester tester) {
  tester.view
    ..physicalSize = const Size(1080, 2340)
    ..devicePixelRatio = 2.625;
  addTearDown(tester.view.reset);
}

/// The whole app, signed in, with the fake picking API.
Future<void> pumpPickingApp(
  WidgetTester tester, {
  required FakePickingServer server,
  List<String> permissions = staffPermissions,
  String role = 'warehouse_staff',
  RecordingAlerts? alerts,
  FakeScanner? scanner,
  String? fullName = 'Ayşe Depocu',
  AuthUser authUser = const AuthUser(id: userId, email: 'ayse@ornek.test'),
}) async {
  usePhoneSize(tester);
  final identity = FakeIdentityApi(
    (tenant) async => meFixture(
      fullName: fullName,
      memberships: [membershipJson(tenantId, role: role)],
      activeTenant: tenant == tenantId
          ? activeTenantJson(tenantId, role: role, permissions: permissions)
          : null,
    ),
  );
  final fakeScanner = scanner ?? FakeScanner();
  await pumpApp(
    tester,
    overrides: [
      authRepositoryProvider.overrideWithValue(
        FakeAuthRepository(user: authUser),
      ),
      identityApiProvider.overrideWithValue(identity),
      activeTenantStoreProvider.overrideWithValue(MemoryActiveTenantStore()),
      pickingApiProvider.overrideWithValue(pickingApiFor(server)),
      scanAlertsProvider.overrideWithValue(alerts ?? RecordingAlerts()),
      codeScannerProvider.overrideWithValue(fakeScanner.call),
    ],
  );
  await tester.pumpAndSettle();
}

Future<void> openTasks(WidgetTester tester) async {
  await tester.tap(find.byKey(const Key('nav-tasks')));
  await tester.pumpAndSettle();
}

Future<void> openTask(WidgetTester tester) async {
  await openTasks(tester);
  await tester.tap(find.byKey(const Key('task-task-1')));
  await tester.pumpAndSettle();
}

/// Types [code] into the manual entry and sends it.
Future<void> enterCode(WidgetTester tester, String code) async {
  final submit = find.byKey(const Key('pick-manual-submit'));
  await tester.enterText(find.byKey(const Key('pick-manual-code')), code);
  await tester.tap(submit);
  await tester.pumpAndSettle();
}

String feedbackText(WidgetTester tester) {
  final box = find.byKey(const Key('pick-feedback'));
  return tester
      .widgetList<Text>(find.descendant(of: box, matching: find.byType(Text)))
      .map((t) => t.data ?? '')
      .join(' ');
}

/// Scrolls the pick screen until [finder] is built and visible.
Future<void> scrollPickTo(WidgetTester tester, Finder finder) =>
    tester.scrollUntilVisible(
      finder,
      200,
      scrollable: find
          .descendant(
            of: find.byKey(const Key('pick-scroll')),
            matching: find.byType(Scrollable),
          )
          .first,
    );
