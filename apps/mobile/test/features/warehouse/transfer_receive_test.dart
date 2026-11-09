import 'package:bayios_mobile/features/stock/stock_widgets.dart';
import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import '../../helpers/fakes.dart';
import '../../helpers/pump.dart';
import 'warehouse_harness.dart';

void main() {
  late RoutingAdapter api;
  late RecordingAlerts alerts;
  late List<Duration> waits;

  const receivePath = '/api/v1/stock-transfers/$transferId/receive';
  const detailPath = '/api/v1/stock-transfers/$transferId';

  setUp(() {
    api = RoutingAdapter();
    alerts = RecordingAlerts();
    waits = [];
    warehouseRoutes(api);
    api
      ..on(
        'GET',
        '/api/v1/stock-transfers',
        (_) => FakeReply(
          200,
          json: {
            'items': [
              transferSummaryJson(),
              // Outgoing from the chosen warehouse: not for the receiver.
              transferSummaryJson(
                id: 'tr-out',
                number: 'TRF-2026-0009',
                source: warehouseId,
                destination: sourceWarehouseId,
              ),
            ],
            'page': pageInfo(),
          },
        ),
      )
      ..on('GET', detailPath, (_) => FakeReply(200, json: transferJson()))
      ..on('POST', receivePath, (call) {
        final line = ((call.body! as Map)['lines'] as List).single as Map;
        return FakeReply(
          200,
          json: {
            'transfer': transferJson(receivedA: line['quantity'] as num),
            'movementIds': ['m-1', 'm-2'],
          },
        );
      });
  });

  Future<void> pump(
    WidgetTester tester, {
    String location = '/depo/transferler/$transferId',
    List<String> permissions = staffWarehousePermissions,
  }) => pumpWarehouse(
    tester,
    warehouseOverrides(
      api,
      permissions: permissions,
      alerts: alerts,
      waits: waits,
    ),
    location: location,
  );

  Map<String, Object?> sentLine(Call call) =>
      ((call.body! as Map)['lines'] as List).single as Map<String, Object?>;

  testWidgets('hub → incoming list (destination = default warehouse)', (
    tester,
  ) async {
    await pump(tester, location: '/depo');
    expect(find.byKey(const Key('warehouse-transfers')), findsOneWidget);
    expect(find.byKey(const Key('warehouse-counts')), findsOneWidget);
    // warehouse_staff may move between shelves (inventory.relocate, #98).
    expect(find.byKey(const Key('warehouse-relocation')), findsOneWidget);
    await expectAccessibleTargets(tester);

    await tapOn(tester, find.byKey(const Key('warehouse-transfers')));
    final list = api.callsTo('GET', '/api/v1/stock-transfers').single;
    expect(list.query['status'], 'in_transit,partially_received');
    expect(list.query['warehouseId'], warehouseId);
    expect(find.text('TRF-2026-0007'), findsOneWidget);
    expect(find.text('TRF-2026-0009'), findsNothing);
    expect(find.text(l10n.transferStatusInTransit), findsOneWidget);
    expect(find.text(l10n.transferRoute('Kuzey Depo', 'Ana Depo')), findsOne);
    await expectAccessibleTargets(tester);

    await tapOn(tester, find.byKey(const Key('transfer-$transferId')));
    expect(find.byKey(const Key('transfer-receive')), findsOneWidget);
    expect(find.text('FLT-001'), findsOneWidget);
    expect(find.text('BLT-002'), findsOneWidget);
  });

  testWidgets('shelf → quantity → product scan receives (partial)', (
    tester,
  ) async {
    final scanner = FakeScanner();
    await pumpWarehouse(
      tester,
      warehouseOverrides(api, alerts: alerts, scanner: scanner),
      location: '/depo/transferler/$transferId',
    );
    expect(
      find.text(l10n.transferReceivedSummary('0', '10', '10')),
      findsOneWidget,
    );
    await expectAccessibleTargets(tester);

    // Shelf by camera, quantity 2 with "+", product typed (wedge scanner).
    scanner.codes.add('A-01-01');
    await tapOn(tester, find.byKey(const Key('transfer-shelf-scan')));
    expect(scanner.titles.single, l10n.docShelfScanTitle);
    expect(find.text(l10n.docShelfCurrent('A-01-01')), findsOneWidget);
    await tapOn(tester, find.byKey(const Key('transfer-plus')));
    await typeInto(tester, 'transfer-code-field', '8690000000011');

    final post = api.callsTo('POST', receivePath).single;
    expect(sentLine(post), {
      'code': '8690000000011',
      'quantity': 2,
      'locationCode': 'A-01-01',
    });
    expect(post.idempotencyKey, isNotEmpty);
    // Scans go without If-Match: a parallel receipt must not 412 them.
    expect(post.headers.containsKey('If-Match'), isFalse);
    expect(alerts.events, ['accepted']);
    expect(find.byKey(const Key('transfer-accepted')), findsOneWidget);
    expect(
      find.text(l10n.transferAcceptedLine('FLT-001', '+2', '2')),
      findsOne,
    );
    expect(
      find.text(l10n.transferReceivedSummary('2', '10', '8')),
      findsOneWidget,
    );
    expect(find.text(l10n.transferStatusPartial), findsOneWidget);
    // The shelf stays for the next product; the quantity is back to 1.
    expect(find.text(l10n.docShelfCurrent('A-01-01')), findsOneWidget);
    final quantity = tester.widget<TextField>(
      find.byKey(const Key('transfer-quantity')),
    );
    expect(quantity.controller!.text, '1');
    await expectAccessibleTargets(tester);
  });

  testWidgets('a product scan without a shelf is stopped on the device', (
    tester,
  ) async {
    await pump(tester);
    await typeInto(tester, 'transfer-code-field', 'FLT-001');
    expect(api.callsTo('POST', receivePath), isEmpty);
    expect(find.byKey(const Key('transfer-shelf-missing')), findsOneWidget);
    expect(alerts.events, ['rejected']);
  });

  for (final (code, message) in [
    ('transfer.wrong_item', () => l10n.docErrorWrongItem),
    ('transfer.over_receipt', () => l10n.docErrorOverReceipt),
    ('transfer.unknown_code', () => l10n.docErrorUnknownCode),
    ('transfer.unknown_location', () => l10n.docErrorUnknownLocationTransfer),
  ]) {
    testWidgets('$code → Turkish message, nothing received', (tester) async {
      api.once(
        'POST',
        receivePath,
        (_) => FakeReply(422, json: problem(422, code)),
      );
      await pump(tester);
      await typeInto(tester, 'transfer-shelf-field', 'A-01-01');
      await typeInto(tester, 'transfer-code-field', 'XYZ-999');

      expect(find.byKey(const Key('transfer-rejected')), findsOneWidget);
      expect(find.text(l10n.docEntryRejectedCode('XYZ-999')), findsOneWidget);
      expect(find.text(message()), findsOneWidget);
      expect(alerts.events, ['rejected']);
      final summary = find.text(l10n.transferReceivedSummary('0', '10', '10'));
      await scrollUpTo(tester, summary);
      expect(summary, findsOneWidget);
      // A refused scan is not "pending": the next scan is a new action.
      expect(find.byKey(const Key('transfer-resend')), findsNothing);
      await typeInto(tester, 'transfer-code-field', '8690000000011');
      final posts = api.callsTo('POST', receivePath);
      expect(posts, hasLength(2));
      expect(posts[1].idempotencyKey, isNot(posts[0].idempotencyKey));
      expect(find.byKey(const Key('transfer-accepted')), findsOneWidget);
    });
  }

  testWidgets('lost answer → "Tekrar gönder" resends with the same key', (
    tester,
  ) async {
    api.once(
      'POST',
      receivePath,
      (_) => FakeReply.fail(DioExceptionType.connectionError),
    );
    await pump(tester);
    await typeInto(tester, 'transfer-shelf-field', 'A-01-01');
    await typeInto(tester, 'transfer-code-field', '8690000000011');

    expect(find.byKey(const Key('transfer-pending')), findsOneWidget);
    expect(find.text(l10n.docEntryUnknown), findsOneWidget);
    // Scanning is locked until the outcome is settled.
    final field = tester.widget<TextField>(
      find.byKey(const Key('transfer-code-field')),
    );
    expect(field.enabled, isFalse);
    await expectAccessibleTargets(tester);

    await tapOn(tester, find.byKey(const Key('transfer-resend')));
    final posts = api.callsTo('POST', receivePath);
    expect(posts, hasLength(2));
    expect(posts[1].idempotencyKey, posts[0].idempotencyKey);
    expect(posts[1].body, posts[0].body);
    expect(find.text(l10n.docEntryReplayed), findsOneWidget);
    expect(alerts.events, ['rejected', 'accepted']);
  });

  testWidgets('transfer.busy → one automatic retry with the same key', (
    tester,
  ) async {
    api.once(
      'POST',
      receivePath,
      (_) => FakeReply(
        409,
        json: problem(409, 'transfer.busy'),
        headers: {'Retry-After': '1'},
      ),
    );
    await pump(tester);
    await typeInto(tester, 'transfer-shelf-field', 'A-01-01');
    await typeInto(tester, 'transfer-code-field', '8690000000011');

    final posts = api.callsTo('POST', receivePath);
    expect(posts, hasLength(2));
    expect(posts[1].idempotencyKey, posts[0].idempotencyKey);
    expect(waits, [const Duration(seconds: 1)]);
    expect(find.byKey(const Key('transfer-accepted')), findsOneWidget);
  });

  testWidgets('transfer changed meanwhile (closed) → reloaded, explained', (
    tester,
  ) async {
    api.once(
      'POST',
      receivePath,
      (_) => FakeReply(409, json: problem(409, 'transfer.invalid_transition')),
    );
    await pump(tester);
    api.on(
      'GET',
      detailPath,
      (_) => FakeReply(200, json: transferJson(status: 'closed')),
    );
    await typeInto(tester, 'transfer-shelf-field', 'A-01-01');
    await typeInto(tester, 'transfer-code-field', '8690000000011');

    expect(api.callsTo('GET', detailPath), hasLength(2));
    expect(find.byKey(const Key('transfer-stale')), findsOneWidget);
    expect(find.text(l10n.docErrorTransferChanged), findsOneWidget);
    expect(find.byKey(const Key('transfer-not-receivable')), findsOneWidget);
    expect(find.byKey(const Key('transfer-code-field')), findsNothing);
  });

  testWidgets('412 is handled like a changed transfer (reload)', (
    tester,
  ) async {
    api.once(
      'POST',
      receivePath,
      (_) => FakeReply(412, json: problem(412, 'concurrency.version_mismatch')),
    );
    await pump(tester);
    await typeInto(tester, 'transfer-shelf-field', 'A-01-01');
    await typeInto(tester, 'transfer-code-field', '8690000000011');
    expect(api.callsTo('GET', detailPath), hasLength(2));
    expect(find.text(l10n.docErrorVersionMismatch), findsOneWidget);
    expect(find.byKey(const Key('transfer-resend')), findsNothing);
  });

  testWidgets('load failure offers retry; no permission → explained', (
    tester,
  ) async {
    api.once(
      'GET',
      detailPath,
      (_) => FakeReply.fail(DioExceptionType.connectionError),
    );
    await pump(tester);
    expect(find.byKey(const Key('transfer-load-error')), findsOneWidget);
    await tapOn(tester, find.byKey(const Key('stock-retry')));
    expect(find.byKey(const Key('transfer-receive')), findsOneWidget);
    expect(find.byType(CodeEntryField), findsWidgets);
  });

  testWidgets('without inventory.transfer.receive the screen says so', (
    tester,
  ) async {
    await pump(tester, permissions: viewerPermissions);
    expect(find.byKey(const Key('warehouse-no-permission')), findsOneWidget);
    expect(find.text(l10n.transferForbidden), findsOneWidget);
    expect(api.callsTo('GET', detailPath), isEmpty);
  });
}
