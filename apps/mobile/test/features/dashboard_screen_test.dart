import 'package:bayios_mobile/core/auth/auth_repository.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import '../helpers/fakes.dart';
import '../helpers/pump.dart';
import 'picking/fake_picking_server.dart';
import 'picking/picking_app.dart';

String _count(WidgetTester tester, String key) => tester
    .widgetList<Text>(
      find.descendant(of: find.byKey(Key(key)), matching: find.byType(Text)),
    )
    .first
    .data!;

void main() {
  late FakePickingServer server;

  setUp(() => server = FakePickingServer());

  testWidgets('my open tasks, counts and quick links from the real API', (
    tester,
  ) async {
    final pool = {
      ...server.summaryJson(),
      'id': 'task-9',
      'orderNumber': 'SO-1009',
      'assignedTo': null,
      'assigneeName': null,
    };
    server.mineItems = () => [
      {...server.summaryJson(), 'status': 'in_progress'},
      pool,
    ];
    await pumpPickingApp(tester, server: server);

    final mine = server.requests.singleWhere((r) => r.path.endsWith('/mine'));
    expect(mine.queryParameters['scope'], 'all');
    expect(find.text(l10n.dashboardWelcome('Ayşe Depocu')), findsOneWidget);
    expect(_count(tester, 'dashboard-count-assigned'), '1');
    expect(_count(tester, 'dashboard-count-in-progress'), '1');
    expect(_count(tester, 'dashboard-count-pool'), '1');
    expect(find.byKey(const Key('task-task-1')), findsOneWidget);
    expect(find.byKey(const Key('task-task-9')), findsNothing);
    await expectAccessibleTargets(tester);

    await tester.tap(find.byKey(const Key('task-task-1')));
    await tester.pumpAndSettle();
    expect(find.text(l10n.pickingTitle('SO-1001')), findsOneWidget);
  });

  testWidgets('quick link opens the pool tab', (tester) async {
    await pumpPickingApp(tester, server: server);
    await tester.scrollUntilVisible(
      find.byKey(const Key('dashboard-link-pool')),
      200,
    );
    await tester.tap(find.byKey(const Key('dashboard-link-pool')));
    await tester.pumpAndSettle();
    expect(
      DefaultTabController.of(tester.element(find.byType(TabBar))).index,
      1,
    );
    expect(server.requests.last.queryParameters['scope'], 'pool');
  });

  testWidgets('no assigned task: says so and points at the pool', (
    tester,
  ) async {
    server.mineItems = () => [
      {...server.summaryJson(), 'assignedTo': null, 'assigneeName': null},
    ];
    await pumpPickingApp(tester, server: server);
    expect(find.byKey(const Key('dashboard-no-tasks')), findsOneWidget);
    expect(
      find.textContaining(l10n.dashboardNoTasksPoolHint(1)),
      findsOneWidget,
    );
  });

  testWidgets('task load failure: inline retry, rest of the dashboard ok', (
    tester,
  ) async {
    server.overrides['GET /api/v1/picking-tasks/mine'] = [() => FakeReply(503)];
    await pumpPickingApp(tester, server: server);
    expect(find.byKey(const Key('dashboard-tasks-error')), findsOneWidget);
    expect(find.byKey(const Key('dashboard-tenant')), findsOneWidget);
    await expectAccessibleTargets(tester);

    await tester.tap(find.byKey(const Key('dashboard-tasks-retry')));
    await tester.pumpAndSettle();
    expect(find.byKey(const Key('dashboard-count-assigned')), findsOneWidget);
  });

  testWidgets('tasks.read.all only (viewer): active count, no /mine', (
    tester,
  ) async {
    await pumpPickingApp(
      tester,
      server: server,
      permissions: viewerPermissions,
      role: 'viewer',
    );
    expect(_count(tester, 'dashboard-count-active'), '1');
    expect(server.requests.where((r) => r.path.endsWith('/mine')), isEmpty);
    expect(find.byKey(const Key('dashboard-link-pool')), findsNothing);
    expect(find.byKey(const Key('dashboard-link-all')), findsOneWidget);
  });

  testWidgets('no task permission: no task section, no task calls', (
    tester,
  ) async {
    await pumpPickingApp(
      tester,
      server: server,
      permissions: const ['tenant.read'],
    );
    expect(find.byKey(const Key('dashboard-count-assigned')), findsNothing);
    expect(find.byKey(const Key('dashboard-link-mine')), findsNothing);
    expect(server.requests, isEmpty);
  });

  testWidgets('quick actions follow the permissions (stock and warehouse)', (
    tester,
  ) async {
    await pumpPickingApp(
      tester,
      server: server,
      permissions: [
        ...staffPermissions,
        'inventory.read',
        'inventory.receive',
        'inventory.count.perform',
      ],
    );
    for (final key in [
      'dashboard-link-mine',
      'dashboard-link-pool',
      'dashboard-link-receiving',
      'dashboard-link-stock',
      'dashboard-link-counts',
    ]) {
      await tester.scrollUntilVisible(find.byKey(Key(key)), 200);
      expect(find.byKey(Key(key)), findsOneWidget, reason: key);
    }
    expect(find.byKey(const Key('dashboard-link-transfers')), findsNothing);
    expect(find.byKey(const Key('dashboard-link-relocation')), findsNothing);
    expect(find.byKey(const Key('dashboard-link-all')), findsNothing);
    await expectAccessibleTargets(tester);
  });

  testWidgets('header shows business and role on the brand colour', (
    tester,
  ) async {
    await pumpPickingApp(tester, server: server);
    expect(
      find.descendant(
        of: find.byKey(const Key('dashboard-tenant')),
        matching: find.text('Demo Depo'),
      ),
      findsOneWidget,
    );
    expect(
      find.descendant(
        of: find.byKey(const Key('dashboard-role')),
        matching: find.text(l10n.roleWarehouseStaff),
      ),
      findsOneWidget,
    );
  });

  group('greeting when /me has no fullName', () {
    testWidgets('uses the Supabase metadata name', (tester) async {
      await pumpPickingApp(
        tester,
        server: server,
        fullName: null,
        authUser: const AuthUser(
          id: userId,
          email: 'ayse@ornek.test',
          fullName: 'Ayşe Metadata',
        ),
      );
      expect(find.text(l10n.dashboardWelcome('Ayşe Metadata')), findsOneWidget);
    });

    testWidgets('else the e-mail local part', (tester) async {
      await pumpPickingApp(tester, server: server, fullName: null);
      // meFixture's e-mail is depo@ornek.test.
      expect(find.text(l10n.dashboardWelcome('depo')), findsOneWidget);
    });
  });
}
