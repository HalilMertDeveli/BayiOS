import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import '../../helpers/fakes.dart';
import '../../helpers/pump.dart';
import 'stock_harness.dart';

FakeReply balancesReply(
  List<Map<String, Object?>> items, {
  bool hasMore = false,
}) => FakeReply(
  200,
  json: {
    'items': items,
    'page': {
      'nextCursor': hasMore ? 'c2' : null,
      'hasMore': hasMore,
      'limit': 50,
    },
  },
);

void main() {
  late RoutingAdapter api;
  late FakeScanner scanner;

  setUp(() {
    api = RoutingAdapter();
    defaultRoutes(api);
    api.on(
      'GET',
      '/api/v1/inventory/balances',
      (_) => balancesReply([
        balanceJson(),
        balanceJson(
          id: 'b-2',
          location: binB,
          onHand: '3.5',
          reserved: 0,
          available: '3.5',
          minQuantity: 20,
          belowMinimum: true,
        ),
      ]),
    );
    scanner = FakeScanner();
  });

  Future<void> pump(
    WidgetTester tester, {
    List<String> permissions = staffPermissions,
    String location = '/stok',
  }) => pumpStock(
    tester,
    stockOverrides(api, permissions: permissions, scanner: scanner),
    location: location,
  );

  Future<void> search(WidgetTester tester, String term) async {
    await tester.enterText(find.byKey(const Key('stock-search-field')), term);
    await tester.testTextInput.receiveAction(TextInputAction.search);
    await tester.pumpAndSettle();
  }

  testWidgets('idle → search → balances per warehouse / bin with minimum', (
    tester,
  ) async {
    await pump(tester);
    expect(find.byKey(const Key('stock-idle')), findsOneWidget);
    await expectAccessibleTargets(tester);

    await search(tester, ' FLT ');
    final call = api.callsTo('GET', '/api/v1/inventory/balances').single;
    expect(call.query['q'], 'FLT');
    expect(call.headers['X-Tenant-Id'], tenantId);
    expect(
      api.callsTo('GET', '/api/v1/variants/batch').single.query['ids'],
      variantId,
    );

    expect(find.text('FLT-001'), findsOneWidget);
    expect(find.text('Yağ filtresi · 8690000000011'), findsOneWidget);
    expect(find.text('Ana Depo · A-01-01'), findsOneWidget);
    expect(find.text('Ana Depo · B-02-03'), findsOneWidget);
    expect(find.text(l10n.stockTotals('15,5', '13,5')), findsOneWidget);
    expect(find.text('3,5'), findsNWidgets(2));
    expect(find.text(l10n.stockMinimum('20')), findsOneWidget);
    expect(find.byKey(const Key('stock-critical-b-2')), findsOneWidget);
    expect(find.byKey(const Key('stock-critical-b-1')), findsNothing);
    await expectAccessibleTargets(tester);
  });

  testWidgets('camera scan searches by the barcode', (tester) async {
    await pump(tester);
    scanner.codes.add('8690000000011');
    await tapOn(tester, find.byKey(const Key('stock-scan')));
    expect(scanner.titles.single, l10n.stockScanTitle);
    expect(
      api.callsTo('GET', '/api/v1/inventory/balances').single.query['q'],
      '8690000000011',
    );
    expect(find.text('FLT-001'), findsOneWidget);
  });

  testWidgets('/stok?q= searches on open', (tester) async {
    await pump(tester, location: '/stok?q=FLT-001');
    expect(
      api.callsTo('GET', '/api/v1/inventory/balances').single.query['q'],
      'FLT-001',
    );
    expect(find.byKey(const Key('stock-group-$variantId')), findsOneWidget);
  });

  testWidgets('no balance: unknown code vs known product without stock', (
    tester,
  ) async {
    api.on('GET', '/api/v1/inventory/balances', (_) => balancesReply([]));
    await pump(tester);
    await search(tester, 'yok-boyle');
    expect(find.text(l10n.stockNoResults('yok-boyle')), findsOneWidget);

    await search(tester, '8690000000011');
    expect(find.text(l10n.stockNoBalanceForVariant), findsOneWidget);
    expect(find.text('FLT-001'), findsOneWidget);
  });

  testWidgets('more rows than a page asks for a narrower search', (
    tester,
  ) async {
    api.on(
      'GET',
      '/api/v1/inventory/balances',
      (_) => balancesReply([balanceJson()], hasMore: true),
    );
    await pump(tester);
    await search(tester, 'F');
    expect(find.text(l10n.stockMoreResults), findsOneWidget);
  });

  testWidgets('offline: message + retry; too broad: message only', (
    tester,
  ) async {
    api.once(
      'GET',
      '/api/v1/inventory/balances',
      (_) => FakeReply.fail(DioExceptionType.connectionError),
    );
    await pump(tester);
    await search(tester, 'FLT');
    expect(find.text(l10n.errorNetwork), findsOneWidget);
    await expectAccessibleTargets(tester);
    await tapOn(tester, find.byKey(const Key('stock-retry')));
    expect(find.text('FLT-001'), findsOneWidget);

    api.once(
      'GET',
      '/api/v1/inventory/balances',
      (_) => FakeReply(422, json: problem(422, 'inventory.search_too_broad')),
    );
    await search(tester, 'a');
    expect(find.text(l10n.stockErrorSearchTooBroad), findsOneWidget);
    expect(find.byKey(const Key('stock-retry')), findsNothing);
  });

  group('adjustment (inventory.adjust)', () {
    testWidgets('hidden without the permission', (tester) async {
      await pump(tester);
      await search(tester, 'FLT');
      expect(find.byKey(const Key('stock-adjust-b-1')), findsNothing);
      expect(find.text(l10n.stockAdjustAction), findsNothing);
    });

    testWidgets('direct link without the permission explains', (tester) async {
      await pump(tester, location: '/stok/duzeltme');
      expect(find.byKey(const Key('adjust-no-permission')), findsOneWidget);
    });

    Future<void> openAdjust(WidgetTester tester) async {
      await pump(tester, permissions: managerPermissions);
      await search(tester, 'FLT');
      await tapOn(tester, find.byKey(const Key('stock-adjust-b-1')));
      expect(find.text(l10n.stockAdjustTitle), findsOneWidget);
    }

    Future<void> fill(WidgetTester tester) async {
      await tapOn(tester, find.byKey(const Key('adjust-type-adjustment_out')));
      await tester.enterText(find.byKey(const Key('adjust-quantity')), '2');
      await tester.enterText(
        find.byKey(const Key('adjust-reason')),
        'Kırık ambalaj',
      );
      await tester.pump();
    }

    testWidgets('validates, posts the ledger movement, refreshes', (
      tester,
    ) async {
      api.on(
        'POST',
        '/api/v1/inventory/adjustments',
        (_) => FakeReply(
          201,
          json: movementJson(type: 'adjustment_out', balanceAfter: 10),
        ),
      );
      await openAdjust(tester);
      expect(find.text('Ana Depo · A-01-01'), findsOneWidget);
      await expectAccessibleTargets(tester);

      await tapOn(tester, find.byKey(const Key('adjust-submit')));
      expect(find.text(l10n.stockAdjustTypeRequired), findsOneWidget);
      expect(find.text(l10n.stockQuantityRequired), findsOneWidget);
      expect(find.text(l10n.stockReasonRequired), findsOneWidget);
      expect(api.callsTo('POST', '/api/v1/inventory/adjustments'), isEmpty);

      await fill(tester);
      await tapOn(tester, find.byKey(const Key('adjust-submit')));
      final post = api.callsTo('POST', '/api/v1/inventory/adjustments').single;
      expect(post.idempotencyKey, isNotEmpty);
      expect(post.body, {
        'variantId': variantId,
        'warehouseId': warehouseId,
        'locationId': binA,
        'type': 'adjustment_out',
        'quantity': 2,
        'reason': 'Kırık ambalaj',
        'confirmNegativeStock': false,
      });
      // Back on the stock screen, which reloaded the balances.
      expect(find.text(l10n.stockAdjustSaved('10')), findsOneWidget);
      expect(api.callsTo('GET', '/api/v1/inventory/balances'), hasLength(2));
    });

    testWidgets('403 from the API shows a clear message', (tester) async {
      api.on(
        'POST',
        '/api/v1/inventory/adjustments',
        (_) => FakeReply(403, json: problem(403, 'auth.forbidden')),
      );
      await openAdjust(tester);
      await fill(tester);
      await tapOn(tester, find.byKey(const Key('adjust-submit')));
      expect(find.byKey(const Key('adjust-error')), findsOneWidget);
      expect(find.text(l10n.stockAdjustForbidden), findsOneWidget);
      expect(find.text(l10n.stockAdjustTitle), findsOneWidget);
    });

    testWidgets('offline: retry resends with the same key', (tester) async {
      api
        ..once(
          'POST',
          '/api/v1/inventory/adjustments',
          (_) => FakeReply.fail(DioExceptionType.connectionError),
        )
        ..on(
          'POST',
          '/api/v1/inventory/adjustments',
          (_) => FakeReply(201, json: movementJson(type: 'adjustment_out')),
        );
      await openAdjust(tester);
      await fill(tester);
      await tapOn(tester, find.byKey(const Key('adjust-submit')));
      expect(find.text(l10n.stockAdjustPendingHint), findsOneWidget);
      await tapOn(tester, find.byKey(const Key('adjust-submit')));
      final calls = api.callsTo('POST', '/api/v1/inventory/adjustments');
      expect(calls, hasLength(2));
      expect(calls[1].idempotencyKey, calls[0].idempotencyKey);
      expect(find.text(l10n.stockTitle), findsOneWidget);
    });

    testWidgets('negative stock (warn policy) needs confirmation', (
      tester,
    ) async {
      api
        ..once(
          'POST',
          '/api/v1/inventory/adjustments',
          (_) => FakeReply(
            409,
            json: problem(
              409,
              'inventory.insufficient_stock',
              extra: {'confirmationRequired': true, 'shortages': <Object>[]},
            ),
          ),
        )
        ..on(
          'POST',
          '/api/v1/inventory/adjustments',
          (_) => FakeReply(201, json: movementJson(type: 'adjustment_out')),
        );
      await openAdjust(tester);
      await fill(tester);
      await tapOn(tester, find.byKey(const Key('adjust-submit')));
      expect(find.text(l10n.stockAdjustNegativeTitle), findsOneWidget);
      await tapOn(tester, find.byKey(const Key('adjust-negative-confirm')));
      final calls = api.callsTo('POST', '/api/v1/inventory/adjustments');
      expect(calls, hasLength(2));
      expect((calls[0].body! as Map)['confirmNegativeStock'], isFalse);
      expect((calls[1].body! as Map)['confirmNegativeStock'], isTrue);
    });
  });
}
