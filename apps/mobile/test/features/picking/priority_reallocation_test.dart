import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import '../../helpers/pump.dart';
import 'fake_picking_server.dart';
import 'picking_app.dart';

/// #98 on the handheld: task priority + customer name, server order of
/// `/mine`, inactive shelves and the re-allocating scan.
void main() {
  late FakePickingServer server;
  late RecordingAlerts alerts;

  setUp(() {
    server = FakePickingServer();
    alerts = RecordingAlerts();
  });

  Future<void> pump(WidgetTester tester) =>
      pumpPickingApp(tester, server: server, alerts: alerts);

  testWidgets('list: priority badge + customer, server order kept', (
    tester,
  ) async {
    server.mineItems = () => [
      // Server order (urgent first); the app must not re-sort.
      {
        ...server.summaryJson(),
        'id': 'task-u',
        'orderNumber': 'SO-2002',
        'priority': 'urgent',
        'customerName': 'Kaya Otomotiv',
      },
      {
        ...server.summaryJson(),
        'id': 'task-n',
        'orderNumber': 'SO-1000',
        'priority': 'normal',
        'customerName': null,
        'requestedDeliveryDate': null,
      },
      {
        ...server.summaryJson(),
        'id': 'task-l',
        'orderNumber': 'SO-0999',
        'priority': 'low',
      },
    ];
    await pump(tester);
    await openTasks(tester);

    expect(find.byKey(const Key('task-priority-urgent')), findsOneWidget);
    expect(find.text(l10n.tasksPriorityUrgent), findsOneWidget);
    expect(find.text(l10n.tasksPriorityLow), findsOneWidget);
    // "normal" carries no badge.
    expect(find.byKey(const Key('task-priority-normal')), findsNothing);
    expect(
      find.text('Kaya Otomotiv · ${l10n.tasksDeliveryDate('12 Kas')}'),
      findsOneWidget,
    );
    expect(find.byKey(const Key('task-customer-task-n')), findsNothing);

    final y = [
      for (final id in ['task-u', 'task-n', 'task-l'])
        tester.getTopLeft(find.byKey(Key('task-$id'))).dy,
    ];
    expect(y[0] < y[1] && y[1] < y[2], isTrue, reason: 'server order');
    await expectAccessibleTargets(tester);
  });

  testWidgets('detail: priority, customer, inactive shelf warning', (
    tester,
  ) async {
    server
      ..priority = 'high'
      ..lines.first.locationIsActive = false;
    await pump(tester);
    await openTask(tester);

    expect(find.text(l10n.tasksPriorityHigh), findsOneWidget);
    expect(find.byKey(const Key('pick-customer')), findsOneWidget);
    expect(find.textContaining('Yıldız Oto'), findsWidgets);
    // Current line (A-01-01) is on a deactivated shelf.
    expect(find.text(l10n.pickingShelfInactive), findsWidgets);
    await scrollPickTo(tester, find.byKey(const Key('pick-line-BRK-002')));
    expect(find.byKey(const Key('pick-line-inactive-BRK-002')), findsNothing);
    await expectAccessibleTargets(tester);
  });

  testWidgets('scan on another shelf with free stock → task refreshed', (
    tester,
  ) async {
    server.freeShelves.add(const FakeShelf('loc-c', 'C-03-01', 'LOC-C0301'));
    await pump(tester);
    await openTask(tester);
    final readsBefore = server.requests
        .where((r) => r.method == 'GET' && r.path == FakePickingServer.taskPath)
        .length;

    await enterCode(tester, 'LOC-C0301');
    await enterCode(tester, '8690001');

    expect(server.appliedScans, 1);
    final reads = server.requests
        .where((r) => r.method == 'GET' && r.path == FakePickingServer.taskPath)
        .length;
    expect(reads, readsBefore + 1, reason: 'reloaded after re-allocation');
    expect(feedbackText(tester), contains(l10n.pickingScanReallocated));
    await scrollPickTo(tester, find.byKey(const Key('pick-line-FLT-001')));
    expect(
      find.descendant(
        of: find.byKey(const Key('pick-line-FLT-001')),
        matching: find.text('C-03-01'),
      ),
      findsOneWidget,
    );
    expect(alerts.events.last, 'accepted');
  });

  testWidgets('a normal scan does not reload the task', (tester) async {
    await pump(tester);
    await openTask(tester);
    final before = server.requests.length;
    await enterCode(tester, 'LOC-A0101');
    await enterCode(tester, '8690001');
    final after = server.requests.sublist(before);
    expect(
      after.where(
        (r) => r.method == 'GET' && r.path == FakePickingServer.taskPath,
      ),
      isEmpty,
    );
    expect(feedbackText(tester), isNot(contains(l10n.pickingScanReallocated)));
  });
}
