import 'package:bayios_mobile/core/session/access.dart';
import 'package:bayios_mobile/core/session/me.dart';
import 'package:bayios_mobile/features/picking/task_list_screen.dart';
import 'package:bayios_mobile/features/picking/tasks_providers.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import '../../helpers/fakes.dart';
import '../../helpers/pump.dart';
import 'fake_picking_server.dart';
import 'picking_app.dart';

Access _access(List<String> permissions) => Access([
  for (final p in permissions)
    PermissionGrant(permission: p, scope: PermissionScope.all),
]);

void main() {
  late FakePickingServer server;

  setUp(() => server = FakePickingServer());

  group('tabs follow the permissions', () {
    test('tasks.read.own → assigned + pool; tasks.read.all → all', () {
      expect(tabsFor(_access(staffPermissions)), [
        TaskTab.assigned,
        TaskTab.pool,
      ]);
      expect(tabsFor(_access(ownerPermissions)), [
        TaskTab.assigned,
        TaskTab.pool,
        TaskTab.all,
      ]);
      expect(tabsFor(_access(viewerPermissions)), [TaskTab.all]);
      expect(tabsFor(_access(['tenant.read'])), isEmpty);
    });

    testWidgets('no task permission → no "Görevler" destination', (
      tester,
    ) async {
      await pumpPickingApp(
        tester,
        server: server,
        permissions: const ['tenant.read'],
      );
      expect(find.byKey(const Key('nav-tasks')), findsNothing);
      expect(server.requests, isEmpty);
    });

    testWidgets('staff see Bana atanan + Havuz, not Tümü', (tester) async {
      await pumpPickingApp(tester, server: server);
      await openTasks(tester);
      expect(find.byKey(const Key('tasks-tab-assigned')), findsOneWidget);
      expect(find.byKey(const Key('tasks-tab-pool')), findsOneWidget);
      expect(find.byKey(const Key('tasks-tab-all')), findsNothing);
    });

    testWidgets('a viewer gets the read-only "all" list only', (tester) async {
      await pumpPickingApp(
        tester,
        server: server,
        permissions: viewerPermissions,
        role: 'viewer',
      );
      await openTasks(tester);
      expect(find.byType(TabBar), findsNothing);
      expect(find.byKey(const Key('task-task-1')), findsOneWidget);
      expect(
        server.requests.where((r) => r.path == '/api/v1/picking-tasks'),
        isNotEmpty,
      );
      expect(
        server.requests.where((r) => r.path.endsWith('/mine')),
        isEmpty,
        reason: 'no tasks.read.own',
      );
    });
  });

  testWidgets('real API list: card, query, then open the task', (tester) async {
    await pumpPickingApp(tester, server: server);
    await openTasks(tester);

    final mine = server.requests.lastWhere((r) => r.path.endsWith('/mine'));
    expect(mine.queryParameters, {'scope': 'assigned', 'limit': '50'});
    expect(mine.headers['X-Tenant-Id'], tenantId);
    expect(find.text(l10n.tasksOrder('SO-1001')), findsOneWidget);
    expect(find.textContaining(l10n.tasksAssignedToMe), findsOneWidget);
    expect(find.text(l10n.tasksStatusOpen), findsOneWidget);
    await expectAccessibleTargets(tester);

    await tester.tap(find.byKey(const Key('task-task-1')));
    await tester.pumpAndSettle();
    expect(find.text(l10n.pickingTitle('SO-1001')), findsOneWidget);
  });

  testWidgets('empty state, then pull-to-refresh shows new work', (
    tester,
  ) async {
    var items = <Map<String, Object?>>[];
    server.mineItems = () => items;
    await pumpPickingApp(tester, server: server);
    await openTasks(tester);
    expect(find.byKey(const Key('tasks-empty')), findsOneWidget);
    expect(find.text(l10n.tasksEmptyAssigned), findsOneWidget);
    await expectAccessibleTargets(tester);

    items = [server.summaryJson()];
    await tester.fling(
      find.byKey(const Key('tasks-list-assigned')),
      const Offset(0, 400),
      1000,
    );
    await tester.pumpAndSettle();
    expect(find.byKey(const Key('task-task-1')), findsOneWidget);
  });

  testWidgets('error state with "Tekrar dene"', (tester) async {
    // First 503 for the dashboard's /mine, second for the list's.
    server.overrides['GET /api/v1/picking-tasks/mine'] = [
      () => FakeReply(503),
      () => FakeReply(503),
    ];
    await pumpPickingApp(tester, server: server);
    await openTasks(tester);
    expect(find.text(l10n.errorServer), findsOneWidget);
    expect(find.text(l10n.retry), findsOneWidget);
    await expectAccessibleTargets(tester);

    await tester.tap(find.text(l10n.retry));
    await tester.pumpAndSettle();
    expect(find.byKey(const Key('task-task-1')), findsOneWidget);
  });

  testWidgets('pool tab', (tester) async {
    server
      ..assignedTo = null
      ..mineItems = () => [server.summaryJson()];
    await pumpPickingApp(tester, server: server);
    await openTasks(tester);
    await tester.tap(find.byKey(const Key('tasks-tab-pool')));
    await tester.pumpAndSettle();
    final pool = server.requests.lastWhere((r) => r.path.endsWith('/mine'));
    expect(pool.queryParameters['scope'], 'pool');
    expect(find.textContaining(l10n.tasksInPool), findsWidgets);
  });

  testWidgets('"all" tab pages with the keyset cursor', (tester) async {
    server
      ..listHasMore = true
      ..listPage = (cursor) => [
        {...server.summaryJson(), 'id': 'task-2', 'orderNumber': 'SO-1002'},
      ];
    await pumpPickingApp(
      tester,
      server: server,
      permissions: ownerPermissions,
      role: 'owner',
    );
    await openTasks(tester);
    await tester.tap(find.byKey(const Key('tasks-tab-all')));
    await tester.pumpAndSettle();
    final list = server.requests.lastWhere(
      (r) => r.path == '/api/v1/picking-tasks',
    );
    expect(list.queryParameters['status'], 'open,in_progress');

    await tester.scrollUntilVisible(
      find.byKey(const Key('tasks-load-more')),
      200,
      scrollable: find.descendant(
        of: find.byKey(const Key('tasks-list-all')),
        matching: find.byType(Scrollable),
      ),
    );
    await tester.tap(find.byKey(const Key('tasks-load-more')));
    await tester.pumpAndSettle();
    expect(server.requests.last.queryParameters['cursor'], 'c2');
    expect(find.text(l10n.tasksOrder('SO-1002')), findsOneWidget);
    expect(find.byKey(const Key('tasks-load-more')), findsNothing);
  });
}
