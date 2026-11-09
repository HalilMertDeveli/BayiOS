import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import '../../helpers/fakes.dart';
import '../../helpers/pump.dart';
import 'warehouse_harness.dart';

void main() {
  late RoutingAdapter api;
  late RecordingAlerts alerts;

  const detailPath = '/api/v1/stock-counts/$countId';
  const entriesPath = '/api/v1/stock-counts/$countId/entries';
  const submitPath = '/api/v1/stock-counts/$countId/submit';

  setUp(() {
    api = RoutingAdapter();
    alerts = RecordingAlerts();
    warehouseRoutes(api);
    api
      ..on(
        'GET',
        '/api/v1/stock-counts',
        (_) => FakeReply(
          200,
          json: {
            'items': [countSummaryJson()],
            'page': pageInfo(),
          },
        ),
      )
      ..on('GET', detailPath, (_) => FakeReply(200, json: countJson()))
      ..on('POST', entriesPath, (call) {
        final entry = ((call.body! as Map)['entries'] as List).single as Map;
        return FakeReply(
          200,
          json: countJson(countedA: entry['quantity'] as num, version: 6),
        );
      })
      ..on(
        'POST',
        submitPath,
        (_) => FakeReply(
          200,
          json: countJson(countedA: 11, status: 'submitted', version: 7),
        ),
      );
  });

  Future<void> pump(
    WidgetTester tester, {
    String location = '/depo/sayimlar/$countId',
    List<String> permissions = staffWarehousePermissions,
  }) => pumpWarehouse(
    tester,
    warehouseOverrides(api, permissions: permissions, alerts: alerts),
    location: location,
  );

  Map<String, Object?> sentEntry(Call call) =>
      ((call.body! as Map)['entries'] as List).single as Map<String, Object?>;

  testWidgets('list of open counts → shelf + product scan (add mode)', (
    tester,
  ) async {
    await pump(tester, location: '/depo/sayimlar');
    expect(
      api.callsTo('GET', '/api/v1/stock-counts').single.query['status'],
      'open',
    );
    expect(find.text('SAY-2026-0003'), findsOneWidget);
    expect(find.text(l10n.countProgress(0, 2)), findsOneWidget);
    await expectAccessibleTargets(tester);

    await tapOn(tester, find.byKey(const Key('count-$countId')));
    expect(find.byKey(const Key('count-no-lines')), findsOneWidget);
    await expectAccessibleTargets(tester);

    await typeInto(tester, 'count-shelf-field', 'LOC-A0101');
    await typeInto(tester, 'count-code-field', '8690000000011');

    final post = api.callsTo('POST', entriesPath).single;
    expect(sentEntry(post), {
      'code': '8690000000011',
      'locationCode': 'LOC-A0101',
      'quantity': 1,
      'mode': 'add',
    });
    expect(post.idempotencyKey, isNotEmpty);
    expect(post.headers.containsKey('If-Match'), isFalse);
    expect(alerts.events, ['accepted']);
    expect(
      find.text(l10n.countAcceptedLine('FLT-001', 'A-01-01', '1')),
      findsOne,
    );
    expect(find.text(l10n.countLinesTitle(1)), findsOneWidget);
    await scrollTo(tester, find.byKey(const Key('count-line-cl-1')));
    expect(find.byKey(const Key('count-line-cl-1')), findsOneWidget);
    // Not blind: the system quantity and the difference are shown.
    expect(find.text(l10n.countLineSystem('12', '-11')), findsOneWidget);
    await expectAccessibleTargets(tester);
  });

  testWidgets('blind count: system quantities are never shown', (tester) async {
    api.on(
      'GET',
      detailPath,
      (_) => FakeReply(200, json: countJson(countedA: 3, hidden: true)),
    );
    await pump(tester);
    expect(find.byKey(const Key('count-blind-hint')), findsOneWidget);
    expect(find.text(l10n.countBlind), findsOneWidget);
    await scrollTo(tester, find.byKey(const Key('count-line-cl-1')));
    expect(find.byKey(const Key('count-line-cl-1')), findsOneWidget);
    expect(find.text(l10n.countLineCounted('3')), findsOneWidget);
    expect(find.byKey(const Key('count-line-system-cl-1')), findsNothing);
    expect(find.textContaining('Sistem'), findsNothing);
  });

  testWidgets('set mode sends the absolute quantity (0 allowed)', (
    tester,
  ) async {
    await pump(tester);
    await tapOn(tester, find.text(l10n.countModeSet));
    expect(find.text(l10n.countQuantitySetLabel), findsOneWidget);
    await tester.enterText(find.byKey(const Key('count-quantity')), '0');
    await typeInto(tester, 'count-shelf-field', 'A-01-01');
    await typeInto(tester, 'count-code-field', 'FLT-001');
    expect(sentEntry(api.callsTo('POST', entriesPath).single), {
      'code': 'FLT-001',
      'locationCode': 'A-01-01',
      'quantity': 0,
      'mode': 'set',
    });
  });

  testWidgets('add mode rejects 0 on the device', (tester) async {
    await pump(tester);
    await tester.enterText(find.byKey(const Key('count-quantity')), '0');
    await typeInto(tester, 'count-shelf-field', 'A-01-01');
    await typeInto(tester, 'count-code-field', 'FLT-001');
    expect(api.callsTo('POST', entriesPath), isEmpty);
    expect(find.text(l10n.stockQuantityPositive), findsOneWidget);
  });

  for (final (code, message) in [
    ('count.unknown_location', () => l10n.docErrorUnknownLocationCount),
    ('count.unknown_code', () => l10n.docErrorUnknownCode),
    ('count.out_of_scope', () => l10n.docErrorOutOfScope),
  ]) {
    testWidgets('$code → Turkish message', (tester) async {
      api.once(
        'POST',
        entriesPath,
        (_) => FakeReply(422, json: problem(422, code)),
      );
      await pump(tester);
      await typeInto(tester, 'count-shelf-field', 'Z-99');
      await typeInto(tester, 'count-code-field', 'FLT-001');
      expect(find.byKey(const Key('count-rejected')), findsOneWidget);
      expect(find.text(message()), findsOneWidget);
      expect(alerts.events, ['rejected']);
    });
  }

  testWidgets('lost entry answer → resend with the same key', (tester) async {
    api.once(
      'POST',
      entriesPath,
      (_) => FakeReply.fail(DioExceptionType.receiveTimeout),
    );
    await pump(tester);
    await typeInto(tester, 'count-shelf-field', 'A-01-01');
    await typeInto(tester, 'count-code-field', 'FLT-001');
    expect(find.byKey(const Key('count-pending')), findsOneWidget);
    // "Sayımı bitir" waits until the entry's outcome is known.
    expect(
      tester
          .widget<FilledButton>(find.byKey(const Key('count-submit')))
          .onPressed,
      isNull,
    );
    await tapOn(tester, find.byKey(const Key('count-resend')));
    final posts = api.callsTo('POST', entriesPath);
    expect(posts, hasLength(2));
    expect(posts[1].idempotencyKey, posts[0].idempotencyKey);
    expect(find.text(l10n.docEntryReplayed), findsOneWidget);
  });

  testWidgets('submit with If-Match after confirming → submitted', (
    tester,
  ) async {
    await pump(tester);
    await tapOn(tester, find.byKey(const Key('count-submit')));
    expect(find.text(l10n.countSubmitConfirmUncounted(2)), findsOneWidget);
    await expectAccessibleTargets(tester);
    await tapOn(tester, find.byKey(const Key('count-submit-confirm')));

    final post = api.callsTo('POST', submitPath).single;
    expect(post.headers['If-Match'], 'W/"5"');
    expect(post.idempotencyKey, isNotEmpty);
    expect(find.byKey(const Key('count-submitted')), findsOneWidget);
    expect(find.byKey(const Key('count-code-field')), findsNothing);
    expect(find.byKey(const Key('count-submit')), findsNothing);
  });

  testWidgets('submit 412 → reloaded and explained', (tester) async {
    api.once(
      'POST',
      submitPath,
      (_) => FakeReply(412, json: problem(412, 'concurrency.version_mismatch')),
    );
    await pump(tester);
    await tapOn(tester, find.byKey(const Key('count-submit')));
    await tapOn(tester, find.byKey(const Key('count-submit-confirm')));
    expect(api.callsTo('GET', detailPath), hasLength(2));
    expect(find.byKey(const Key('count-submit-error')), findsOneWidget);
    expect(find.text(l10n.docErrorVersionMismatch), findsOneWidget);
    // Not "pending": the user submits again on the reloaded count.
    expect(find.byKey(const Key('count-submit-resend')), findsNothing);
    await tapOn(tester, find.byKey(const Key('count-submit')));
    await tapOn(tester, find.byKey(const Key('count-submit-confirm')));
    final posts = api.callsTo('POST', submitPath);
    expect(posts, hasLength(2));
    expect(posts[1].idempotencyKey, isNot(posts[0].idempotencyKey));
    expect(find.byKey(const Key('count-submitted')), findsOneWidget);
  });

  testWidgets('count.nothing_counted → Turkish message', (tester) async {
    api.once(
      'POST',
      submitPath,
      (_) => FakeReply(409, json: problem(409, 'count.nothing_counted')),
    );
    await pump(tester);
    await tapOn(tester, find.byKey(const Key('count-submit')));
    await tapOn(tester, find.byKey(const Key('count-submit-confirm')));
    expect(find.text(l10n.docErrorNothingCounted), findsOneWidget);
  });

  testWidgets('without inventory.count.perform the screen says so', (
    tester,
  ) async {
    await pump(tester, permissions: viewerPermissions);
    expect(find.text(l10n.countForbidden), findsOneWidget);
    expect(api.callsTo('GET', detailPath), isEmpty);
  });
}
