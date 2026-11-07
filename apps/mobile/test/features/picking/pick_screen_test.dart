import 'package:bayios_mobile/core/api/api_client.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import '../../helpers/pump.dart';
import 'fake_picking_server.dart';
import 'picking_app.dart';

void main() {
  late FakePickingServer server;
  late RecordingAlerts alerts;
  late FakeScanner scanner;

  setUp(() {
    server = FakePickingServer();
    alerts = RecordingAlerts();
    scanner = FakeScanner();
  });

  Future<void> open(
    WidgetTester tester, {
    List<String> permissions = staffPermissions,
    String role = 'warehouse_staff',
  }) async {
    await pumpPickingApp(
      tester,
      server: server,
      permissions: permissions,
      role: role,
      alerts: alerts,
      scanner: scanner,
    );
    await openTask(tester);
  }

  testWidgets('detail: lines in shelf order with bins and quantities', (
    tester,
  ) async {
    await open(tester);
    expect(find.text(l10n.pickingTitle('SO-1001')), findsOneWidget);
    expect(find.byKey(const Key('pick-current-location')), findsOneWidget);
    expect(
      tester.widget<Text>(find.byKey(const Key('pick-current-location'))).data,
      'A-01-01',
    );
    expect(find.text(l10n.pickingStepLocation('A-01-01')), findsOneWidget);
    expect(find.byKey(const Key('pick-camera')), findsOneWidget);
    // The order header (no prices) loads alongside.
    await scrollPickTo(tester, find.byKey(const Key('pick-order')));
    expect(find.textContaining('Merkez Depo'), findsOneWidget);
    expect(find.textContaining('10.11.2026'), findsOneWidget);
    await scrollPickTo(tester, find.byKey(const Key('pick-line-FLT-001')));
    expect(
      find.descendant(
        of: find.byKey(const Key('pick-line-FLT-001')),
        matching: find.text('A-01-01'),
      ),
      findsOneWidget,
    );
    await scrollPickTo(tester, find.byKey(const Key('pick-line-BRK-002')));
    final second = find.byKey(const Key('pick-line-BRK-002'));
    expect(
      find.descendant(of: second, matching: find.text('B-02-03')),
      findsOneWidget,
    );
    expect(
      find.descendant(
        of: second,
        matching: find.text(l10n.pickingLineQuantity('0', '1', 'adet')),
      ),
      findsOneWidget,
    );
    expect(find.byKey(const Key('pick-focus-BRK-002')), findsOneWidget);
    await expectAccessibleTargets(tester);
  });

  testWidgets('manual entry: right shelf + right product is counted', (
    tester,
  ) async {
    await open(tester);
    await enterCode(tester, 'LOC-A0101');
    expect(find.text(l10n.pickingStepItem('FLT-001')), findsOneWidget);
    expect(find.text(l10n.pickingShelfChip('A-01-01')), findsOneWidget);

    await enterCode(tester, '8690001');
    expect(server.appliedScans, 1);
    expect(
      feedbackText(tester),
      contains(l10n.pickingScanAccepted('FLT-001', '1')),
    );
    expect(find.text(l10n.pickingRemaining('1', 'adet')), findsOneWidget);
    expect(alerts.events, ['accepted', 'accepted']);
    await expectAccessibleTargets(tester);
  });

  testWidgets('wrong product: clear warning + alert, line not counted', (
    tester,
  ) async {
    await open(tester);
    await enterCode(tester, 'LOC-A0101');
    await enterCode(tester, '8690999');

    expect(server.appliedScans, 0);
    final text = feedbackText(tester);
    expect(text, contains(l10n.pickingRejectWrongItem));
    expect(text, contains(l10n.pickingErrorWrongItem));
    expect(find.text(l10n.pickingRemaining('2', 'adet')), findsOneWidget);
    expect(alerts.events.last, 'rejected');
    expect(find.byKey(const Key('pick-resend')), findsNothing);
  });

  testWidgets('wrong bin: warning, the shelf must be scanned again', (
    tester,
  ) async {
    await open(tester);
    await enterCode(tester, 'LOC-B0203'); // line 2's shelf → focus line 2
    expect(find.text(l10n.pickingStepItem('BRK-002')), findsOneWidget);
    await enterCode(tester, '8690001'); // line 1's product on line 2's shelf
    expect(server.appliedScans, 0);
    expect(feedbackText(tester), contains(l10n.pickingRejectWrongLocation));
    expect(alerts.events.last, 'rejected');
    expect(find.byKey(const Key('pick-step')), findsOneWidget);
    expect(find.text(l10n.pickingStepLocation('B-02-03')), findsOneWidget);
  });

  testWidgets('camera scan goes through the same flow', (tester) async {
    scanner.codes.addAll(['LOC-A0101', '8690001']);
    await open(tester);
    await tester.tap(find.byKey(const Key('pick-camera')));
    await tester.pumpAndSettle();
    await tester.tap(find.byKey(const Key('pick-camera')));
    await tester.pumpAndSettle();
    expect(scanner.titles, [
      l10n.pickingScanTitleShelf,
      l10n.pickingScanTitleItem,
    ]);
    expect(server.appliedScans, 1);
  });

  testWidgets('camera unavailable → manual entry gets the focus', (
    tester,
  ) async {
    await open(tester);
    await tester.tap(find.byKey(const Key('pick-camera')));
    await tester.pumpAndSettle();
    final field = tester.widget<TextField>(
      find.byKey(const Key('pick-manual-code')),
    );
    expect(field.focusNode?.hasFocus, isTrue);
  });

  testWidgets('quantity: invalid input is shown and nothing is sent', (
    tester,
  ) async {
    await open(tester);
    await enterCode(tester, 'LOC-A0101');
    await tester.enterText(find.byKey(const Key('pick-qty')), '5');
    await enterCode(tester, '8690001');
    expect(server.writes('/scans'), isEmpty);
    expect(find.text(l10n.pickingQuantityErrorOverOpen('2')), findsWidgets);

    await tester.enterText(find.byKey(const Key('pick-qty')), 'abc');
    await enterCode(tester, '8690001');
    expect(find.text(l10n.pickingQuantityErrorInvalid), findsWidgets);
    expect(server.writes('/scans'), isEmpty);

    await tester.tap(find.byKey(const Key('pick-qty-plus')));
    await tester.pump();
    await tester.tap(find.byKey(const Key('pick-qty-plus')));
    await tester.pump();
    expect(
      tester
          .widget<TextField>(find.byKey(const Key('pick-qty')))
          .controller!
          .text,
      '2',
    );
    await enterCode(tester, '8690001');
    expect(server.lines.first.picked, 2);
  });

  testWidgets('lost answer: "Tekrar gönder" resends with the same key', (
    tester,
  ) async {
    await open(tester);
    await enterCode(tester, 'LOC-A0101');
    server.dropNextAnswer = true;
    await enterCode(tester, '8690001');
    expect(find.byKey(const Key('pick-resend')), findsOneWidget);
    expect(feedbackText(tester), contains(l10n.pickingScanNotSaved));
    await expectAccessibleTargets(tester);

    await tester.tap(find.byKey(const Key('pick-resend')));
    await tester.pumpAndSettle();
    final scans = server.writes('/scans');
    expect(scans, hasLength(2));
    expect(
      scans[1].headers[ApiClient.idempotencyHeader],
      scans[0].headers[ApiClient.idempotencyHeader],
    );
    expect(server.appliedScans, 1);
    expect(find.text(l10n.pickingRemaining('1', 'adet')), findsOneWidget);
    expect(find.byKey(const Key('pick-resend')), findsNothing);
  });

  testWidgets('shortage + complete with shortage', (tester) async {
    await open(tester);
    await enterCode(tester, 'LOC-A0101');
    await tester.enterText(find.byKey(const Key('pick-qty')), '2');
    await enterCode(tester, '8690001');
    // Line 2: not on the shelf → report a shortage.
    await tester.tap(find.byKey(const Key('pick-shortage')));
    await tester.pumpAndSettle();
    expect(find.text(l10n.pickingShortageTitle('BRK-002')), findsOneWidget);
    await expectAccessibleTargets(tester);

    await tester.tap(find.byKey(const Key('shortage-submit')));
    await tester.pumpAndSettle();
    expect(find.byKey(const Key('shortage-reason-error')), findsOneWidget);
    expect(server.writes('/shortage'), isEmpty);

    await tester.tap(find.byKey(const Key('shortage-reason-not_found')));
    await tester.tap(find.byKey(const Key('shortage-submit')));
    await tester.pumpAndSettle();
    expect(server.writes('/shortage'), hasLength(1));
    expect(server.lines.last.short, 1);
    expect(server.lines.last.shortageReason, 'not_found');

    await tester.tap(find.byKey(const Key('pick-complete')));
    await tester.pumpAndSettle();
    expect(find.text(l10n.pickingCompleteShortTitle), findsOneWidget);
    await tester.tap(find.byKey(const Key('pick-complete-confirm')));
    await tester.pumpAndSettle();

    final complete = server.writes('/complete').single;
    expect(complete.headers['If-Match'], 'W/"5"');
    expect(server.status, 'completed_with_shortage');
    expect(find.byKey(const Key('pick-done')), findsOneWidget);
    expect(find.text(l10n.pickingDoneShipment), findsOneWidget);
    expect(find.byKey(const Key('pick-camera')), findsNothing);
  });

  testWidgets('412 on complete: conflict shown, task reloaded', (tester) async {
    server.lines.first.picked = 2;
    server.lines.last.picked = 1;
    await open(tester);
    server.version = 7; // changed by someone else after loading
    await tester.tap(find.byKey(const Key('pick-complete')));
    await tester.pumpAndSettle();
    expect(find.text(l10n.pickingConflictTitle), findsOneWidget);
    expect(find.text(l10n.pickingConflictBody), findsOneWidget);
    expect(server.status, 'open', reason: 'not completed');
  });

  group('permission-gated actions', () {
    testWidgets('pool task: staff with tasks.claim can claim', (tester) async {
      server.assignedTo = null;
      await open(tester);
      expect(find.byKey(const Key('pick-readonly')), findsOneWidget);
      expect(find.byKey(const Key('pick-camera')), findsNothing);
      expect(find.byKey(const Key('pick-claim')), findsOneWidget);
      await expectAccessibleTargets(tester);

      await tester.tap(find.byKey(const Key('pick-claim')));
      await tester.pumpAndSettle();
      expect(server.assignedTo, userId);
      expect(find.byKey(const Key('pick-camera')), findsOneWidget);
    });

    testWidgets("someone else's task is read-only for staff", (tester) async {
      server.assignedTo = otherUserId;
      await open(tester);
      expect(
        find.text(l10n.pickingOtherAssigneeBody('Mehmet Toplayıcı')),
        findsOneWidget,
      );
      expect(find.byKey(const Key('pick-camera')), findsNothing);
      expect(find.byKey(const Key('pick-claim')), findsNothing);
      expect(find.byKey(const Key('pick-manual-code')), findsNothing);
    });

    testWidgets('owner (tasks.assign) may pick any task, no claim', (
      tester,
    ) async {
      server.assignedTo = otherUserId;
      await open(tester, permissions: ownerPermissions, role: 'owner');
      expect(find.byKey(const Key('pick-camera')), findsOneWidget);
      expect(find.byKey(const Key('pick-claim')), findsNothing);
    });

    testWidgets('viewer: read-only, no scan and no shortage', (tester) async {
      await open(tester, permissions: viewerPermissions, role: 'viewer');
      expect(find.text(l10n.pickingNoPickPermission), findsOneWidget);
      expect(find.byKey(const Key('pick-camera')), findsNothing);
      expect(find.byKey(const Key('pick-shortage')), findsNothing);
    });

    testWidgets('no tasks.report_shortage → no "Eksik bildir"', (tester) async {
      await open(
        tester,
        permissions: [
          for (final p in staffPermissions)
            if (p != 'tasks.report_shortage') p,
        ],
      );
      expect(find.byKey(const Key('pick-camera')), findsOneWidget);
      expect(find.byKey(const Key('pick-shortage')), findsNothing);
    });
  });

  testWidgets('unknown task: not-found message, back to tasks', (tester) async {
    server.overrides['GET ${FakePickingServer.taskPath}'] = [
      () => FakePickingServer.problem(404, 'resource.not_found'),
    ];
    await open(tester);
    expect(find.byKey(const Key('pick-not-found')), findsOneWidget);
    await tester.tap(find.text(l10n.pickingBackToTasks));
    await tester.pumpAndSettle();
    expect(find.byKey(const Key('task-task-1')), findsOneWidget);
  });
}
