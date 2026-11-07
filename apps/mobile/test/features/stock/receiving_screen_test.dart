import 'package:bayios_mobile/features/stock/scanner.dart';
import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mobile_scanner/mobile_scanner.dart';

import '../../helpers/fakes.dart';
import '../../helpers/pump.dart';
import 'stock_harness.dart';

void main() {
  late RoutingAdapter api;
  late FakeScanner scanner;

  setUp(() {
    api = RoutingAdapter();
    defaultRoutes(api);
    api.on(
      'GET',
      '/api/v1/inventory/balances',
      (_) => FakeReply(
        200,
        json: {
          'items': [balanceJson(onHand: 17, available: 15)],
          'page': {'nextCursor': null, 'hasMore': false, 'limit': 50},
        },
      ),
    );
    scanner = FakeScanner();
  });

  Future<void> pump(
    WidgetTester tester, {
    List<String> permissions = staffPermissions,
  }) => pumpStock(
    tester,
    stockOverrides(api, permissions: permissions, scanner: scanner),
    location: '/stok/mal-kabul',
  );

  Future<void> typeCode(WidgetTester tester, String code) async {
    await tester.enterText(find.byKey(const Key('receiving-code-field')), code);
    await tester.testTextInput.receiveAction(TextInputAction.search);
    await tester.pumpAndSettle();
  }

  testWidgets('manual entry → quantity + bin scan → post → new balance', (
    tester,
  ) async {
    await pump(tester);
    expect(find.text(l10n.receivingTitle), findsOneWidget);
    expect(find.text('Ana Depo (varsayılan)'), findsOneWidget);
    await expectAccessibleTargets(tester);

    await typeCode(tester, '8690000000011');
    expect(find.byKey(const Key('receiving-line-editor')), findsOneWidget);
    expect(find.text(l10n.receivingScanMatched), findsOneWidget);
    expect(find.text('FLT-001'), findsOneWidget);

    await tester.enterText(find.byKey(const Key('receiving-quantity')), '2');
    await tapOn(tester, find.byKey(const Key('receiving-quantity-plus')));
    await tester.pump();
    expect(find.text('3'), findsOneWidget);

    scanner.codes.add('loc-a0101');
    await tapOn(tester, find.byKey(const Key('receiving-bin-scan')));
    await tester.pumpAndSettle();
    expect(scanner.titles.last, l10n.receivingBinScanTitle);
    expect(find.text('A-01-01 · LOC-A0101'), findsOneWidget);
    await expectAccessibleTargets(tester);

    await tapOn(tester, find.byKey(const Key('receiving-add-line')));
    await tester.pumpAndSettle();
    expect(find.byKey(const Key('receiving-line-editor')), findsNothing);
    expect(find.text(l10n.receivingLineQuantityBin('3', 'A-01-01')), findsOne);
    expect(find.text(l10n.receivingSummary(1, '3')), findsOneWidget);

    await tapOn(tester, find.byKey(const Key('receiving-submit')));
    await tester.pumpAndSettle();

    final post = api.callsTo('POST', '/api/v1/inventory/receipts').single;
    expect(post.body, containsPair('warehouseId', warehouseId));
    expect((post.body! as Map)['lines'], [
      {
        'variantId': variantId,
        'locationId': binA,
        'quantity': 3,
        'unitCost': null,
      },
    ]);
    expect(find.byKey(const Key('receiving-success')), findsOneWidget);
    expect(
      find.text(l10n.receivingSuccessLine('FLT-001', '3', '17')),
      findsOne,
    );
    await expectAccessibleTargets(tester);

    // "Stokta gör" opens the stock query for the received product.
    await tapOn(tester, find.byKey(const Key('receiving-show-stock')));
    await tester.pumpAndSettle();
    expect(find.text(l10n.stockTitle), findsOneWidget);
    expect(
      api.callsTo('GET', '/api/v1/inventory/balances').single.query['q'],
      'FLT-001',
    );
  });

  testWidgets('camera scan adds the product; cancel falls back to typing', (
    tester,
  ) async {
    await pump(tester);
    // Cancelled / "Kodu elle gir": no lookup, the text field gets focus.
    await tapOn(tester, find.byKey(const Key('receiving-scan')));
    await tester.pumpAndSettle();
    expect(api.callsTo('GET', '/api/v1/variants/lookup'), isEmpty);
    final field = tester.widget<TextField>(
      find.byKey(const Key('receiving-code-field')),
    );
    expect(field.focusNode?.hasFocus, isTrue);

    scanner.codes.add('8690000000011');
    await tapOn(tester, find.byKey(const Key('receiving-scan')));
    await tester.pumpAndSettle();
    expect(scanner.titles.last, l10n.receivingScanTitle);
    expect(find.byKey(const Key('receiving-line-editor')), findsOneWidget);
  });

  testWidgets('scan mismatch: unknown code is reported, no line', (
    tester,
  ) async {
    await pump(tester);
    await typeCode(tester, '1234567890');
    expect(find.byKey(const Key('receiving-scan-not-found')), findsOneWidget);
    expect(find.text(l10n.receivingScanNotFound('1234567890')), findsOne);
    expect(find.byKey(const Key('receiving-line-editor')), findsNothing);
    expect(
      tester
          .widget<FilledButton>(find.byKey(const Key('receiving-submit')))
          .onPressed,
      isNull,
    );

    // The field kept its focus through the lookup: a keyboard-wedge
    // scanner's next read goes straight in (regression from the emulator).
    final field = tester.widget<TextField>(
      find.byKey(const Key('receiving-code-field')),
    );
    expect(field.focusNode?.hasFocus, isTrue);
    expect(field.enabled, isTrue);
    tester.testTextInput.enterText('8690000000011');
    await tester.testTextInput.receiveAction(TextInputAction.search);
    await tester.pumpAndSettle();
    expect(find.byKey(const Key('receiving-line-editor')), findsOneWidget);
  });

  testWidgets('quantity and bin are validated before adding', (tester) async {
    await pump(tester);
    await typeCode(tester, '8690000000011');
    await tester.enterText(find.byKey(const Key('receiving-quantity')), '0');

    await tapOn(tester, find.byKey(const Key('receiving-add-line')));
    await tester.pumpAndSettle();
    expect(find.text(l10n.stockQuantityPositive), findsOneWidget);
    expect(find.text(l10n.stockFieldLocationRequired), findsOneWidget);

    // A bin label of another warehouse is rejected.
    scanner.codes.add('Z-99');
    await tapOn(tester, find.byKey(const Key('receiving-bin-scan')));
    await tester.pumpAndSettle();
    expect(find.text(l10n.receivingBinNotFound('Z-99')), findsOneWidget);
    expect(find.byKey(const Key('receiving-line-editor')), findsOneWidget);
  });

  Future<void> addOneLine(WidgetTester tester) async {
    await typeCode(tester, '8690000000011');
    scanner.codes.add('B-02-03');
    await tapOn(tester, find.byKey(const Key('receiving-bin-scan')));
    await tester.pumpAndSettle();

    await tapOn(tester, find.byKey(const Key('receiving-add-line')));
    await tester.pumpAndSettle();
  }

  testWidgets('offline post: lines locked, retry sends the same key', (
    tester,
  ) async {
    api.once(
      'POST',
      '/api/v1/inventory/receipts',
      (_) => FakeReply.fail(DioExceptionType.connectionTimeout),
    );
    await pump(tester);
    await addOneLine(tester);
    await tapOn(tester, find.byKey(const Key('receiving-submit')));
    await tester.pumpAndSettle();

    expect(find.byKey(const Key('receiving-failure')), findsOneWidget);
    expect(find.text(l10n.errorTimeout), findsOneWidget);
    expect(find.text(l10n.receivingPendingHint), findsOneWidget);
    // The first line of the screen has key 1.
    final remove = find.byKey(const Key('receiving-remove-1'));
    // Built lazily below the notice: scroll the list down to it.
    await tester.scrollUntilVisible(
      remove,
      300,
      scrollable: find
          .descendant(
            of: find.byType(ListView),
            matching: find.byType(Scrollable),
          )
          .first,
    );
    expect(tester.widget<IconButton>(remove).onPressed, isNull);
    await expectAccessibleTargets(tester);

    await tapOn(tester, find.byKey(const Key('receiving-retry')));
    await tester.pumpAndSettle();
    final calls = api.callsTo('POST', '/api/v1/inventory/receipts');
    expect(calls, hasLength(2));
    expect(calls[1].idempotencyKey, calls[0].idempotencyKey);
    expect(find.byKey(const Key('receiving-success')), findsOneWidget);
  });

  testWidgets('403 from the API: clear message, no retry', (tester) async {
    api.on(
      'POST',
      '/api/v1/inventory/receipts',
      (_) => FakeReply(403, json: problem(403, 'auth.forbidden')),
    );
    await pump(tester);
    await addOneLine(tester);
    await tapOn(tester, find.byKey(const Key('receiving-submit')));
    await tester.pumpAndSettle();
    expect(find.text(l10n.receivingForbidden), findsOneWidget);
    expect(find.byKey(const Key('receiving-retry')), findsNothing);
  });

  testWidgets('409 conflict is explained', (tester) async {
    api.on(
      'POST',
      '/api/v1/inventory/receipts',
      (_) => FakeReply(409, json: problem(409, 'inventory.duplicate_source')),
    );
    await pump(tester);
    await addOneLine(tester);
    await tapOn(tester, find.byKey(const Key('receiving-submit')));
    await tester.pumpAndSettle();
    expect(find.text(l10n.stockErrorDuplicateSource), findsOneWidget);
  });

  testWidgets('without inventory.receive: no action, direct link explains', (
    tester,
  ) async {
    await pumpStock(
      tester,
      stockOverrides(api, permissions: viewerPermissions, scanner: scanner),
    );
    expect(find.text(l10n.stockTitle), findsOneWidget);
    expect(find.byKey(const Key('stock-receive')), findsNothing);

    await pump(tester, permissions: viewerPermissions);
    expect(find.byKey(const Key('receiving-no-permission')), findsOneWidget);
    expect(find.text(l10n.receivingForbidden), findsOneWidget);
    expect(api.callsTo('GET', '/api/v1/warehouses'), isEmpty);
  });

  testWidgets('with inventory.receive the stock screen offers it', (
    tester,
  ) async {
    await pumpStock(tester, stockOverrides(api, scanner: scanner));
    await tapOn(tester, find.byKey(const Key('stock-receive')));
    await tester.pumpAndSettle();
    expect(find.byKey(const Key('receiving-code-field')), findsOneWidget);
  });

  testWidgets('camera permission denied → manual entry message', (
    tester,
  ) async {
    await pumpScreen(
      tester,
      const Scaffold(
        body: ScannerErrorPanel(code: MobileScannerErrorCode.permissionDenied),
      ),
    );
    expect(find.text(l10n.stockScanPermissionDenied), findsOneWidget);
    await pumpScreen(
      tester,
      const Scaffold(
        body: ScannerErrorPanel(code: MobileScannerErrorCode.unsupported),
      ),
    );
    expect(find.text(l10n.stockScanUnsupported), findsOneWidget);
  });
}
