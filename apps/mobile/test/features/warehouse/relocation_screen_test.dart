import 'package:bayios_mobile/features/warehouse/warehouse_errors.dart';
import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import '../../helpers/fakes.dart';
import '../../helpers/pump.dart';
import 'warehouse_harness.dart';

void main() {
  late RoutingAdapter api;
  late RecordingAlerts alerts;
  late FakeScanner scanner;

  const movePath = '/api/v1/inventory/relocations';

  setUp(() {
    api = RoutingAdapter();
    alerts = RecordingAlerts();
    scanner = FakeScanner();
    warehouseRoutes(api);
    api
      ..on(
        'GET',
        '/api/v1/inventory/balances',
        (_) => FakeReply(
          200,
          json: {
            'items': [balanceJson()],
            'page': {'nextCursor': null, 'hasMore': false, 'limit': 1},
          },
        ),
      )
      ..on(
        'POST',
        movePath,
        (_) => FakeReply(
          201,
          json: {
            'transferId': 'rl-1',
            'movements': [
              movementJson(type: 'transfer_out', quantity: -3, balanceAfter: 9),
              movementJson(
                id: 'm-2',
                type: 'transfer_in',
                quantity: 3,
                balanceAfter: 3,
                location: binB,
              ),
            ],
          },
        ),
      );
  });

  Future<void> pump(
    WidgetTester tester, {
    List<String> permissions = managerWarehousePermissions,
    String location = '/depo/raf-tasima',
  }) => pumpWarehouse(
    tester,
    warehouseOverrides(
      api,
      permissions: permissions,
      alerts: alerts,
      scanner: scanner,
    ),
    location: location,
  );

  Future<void> scanUpToTarget(WidgetTester tester) async {
    await typeInto(tester, 'relocation-source-field', 'LOC-A0101');
    await typeInto(tester, 'relocation-product-field', '8690000000011');
  }

  testWidgets(
    'source shelf → product → quantity → target shelf → move (one post)',
    (tester) async {
      await pump(tester);
      await expectAccessibleTargets(tester);
      expect(
        tester
            .widget<FilledButton>(find.byKey(const Key('relocation-submit')))
            .onPressed,
        isNull,
      );

      // Source shelf by its label barcode (lower case typed is fine).
      await typeInto(tester, 'relocation-source-field', 'loc-a0101');
      expect(find.text('A-01-01'), findsOneWidget);
      await typeInto(tester, 'relocation-product-field', '8690000000011');
      final balance = api.callsTo('GET', '/api/v1/inventory/balances').single;
      expect(balance.query, {
        'variantId': variantId,
        'warehouseId': warehouseId,
        'locationId': binA,
        'limit': '1',
      });
      expect(find.text(l10n.relocationAvailable('10')), findsOneWidget);

      await tapOn(tester, find.byKey(const Key('relocation-plus')));
      await tapOn(tester, find.byKey(const Key('relocation-plus')));
      // Target by camera.
      scanner.codes.add('B-02-03');
      await tapOn(tester, find.byKey(const Key('relocation-target-scan')));
      expect(find.text('B-02-03'), findsOneWidget);
      await expectAccessibleTargets(tester);

      await tapOn(tester, find.byKey(const Key('relocation-submit')));
      final post = api.callsTo('POST', movePath).single;
      expect(post.body, {
        'warehouseId': warehouseId,
        'reference': null,
        'lines': [
          {
            'variantId': variantId,
            'fromLocationId': binA,
            'toLocationId': binB,
            'quantity': 3,
          },
        ],
      });
      expect(post.idempotencyKey, isNotEmpty);
      expect(find.byKey(const Key('relocation-done')), findsOneWidget);
      expect(
        find.text(
          l10n.relocationDoneLine('FLT-001', '3', 'A-01-01', 'B-02-03'),
        ),
        findsOneWidget,
      );
      // Next move from the same shelf: product step again.
      expect(find.byKey(const Key('relocation-product-field')), findsOne);
      expect(alerts.events.last, 'accepted');
      await expectAccessibleTargets(tester);
    },
  );

  testWidgets('unknown shelf, same shelf, too much: refused on the device', (
    tester,
  ) async {
    await pump(tester);
    await typeInto(tester, 'relocation-source-field', 'X-99');
    expect(find.text(l10n.relocationShelfNotFound('X-99')), findsOneWidget);
    expect(alerts.events, ['rejected']);

    await scanUpToTarget(tester);
    await typeInto(tester, 'relocation-target-field', 'A-01-01');
    expect(find.text(l10n.docFieldSameShelf), findsOneWidget);

    await typeInto(tester, 'relocation-target-field', 'B-02-03');
    await tester.enterText(find.byKey(const Key('relocation-quantity')), '11');
    await tester.pumpAndSettle();
    await tapOn(tester, find.byKey(const Key('relocation-submit')));
    expect(find.text(l10n.relocationOverAvailable('10')), findsOneWidget);
    expect(api.callsTo('POST', movePath), isEmpty);

    // "Tümünü taşı" fills the available quantity.
    await tapOn(tester, find.byKey(const Key('relocation-take-all')));
    final field = tester.widget<TextField>(
      find.byKey(const Key('relocation-quantity')),
    );
    expect(field.controller!.text, '10');
  });

  testWidgets('insufficient available from the API → Turkish message', (
    tester,
  ) async {
    api.once(
      'POST',
      movePath,
      (_) => FakeReply(
        409,
        json: problem(409, 'inventory.insufficient_available'),
      ),
    );
    await pump(tester);
    await scanUpToTarget(tester);
    await typeInto(tester, 'relocation-target-field', 'B-02-03');
    await tapOn(tester, find.byKey(const Key('relocation-submit')));
    expect(find.byKey(const Key('relocation-failure')), findsOneWidget);
    expect(find.text(l10n.stockErrorInsufficientAvailable), findsOneWidget);
    expect(find.byKey(const Key('relocation-resend')), findsNothing);
  });

  testWidgets('lost answer → resend with the same key, controls locked', (
    tester,
  ) async {
    api.once(
      'POST',
      movePath,
      (_) => FakeReply.fail(DioExceptionType.connectionError),
    );
    await pump(tester);
    await scanUpToTarget(tester);
    await typeInto(tester, 'relocation-target-field', 'B-02-03');
    await tapOn(tester, find.byKey(const Key('relocation-submit')));
    expect(find.byKey(const Key('relocation-resend')), findsOneWidget);
    expect(
      tester
          .widget<FilledButton>(find.byKey(const Key('relocation-submit')))
          .onPressed,
      isNull,
    );
    // The move cannot be edited while its outcome is unknown.
    expect(find.byKey(const Key('relocation-change-target')), findsNothing);
    expect(find.byKey(const Key('relocation-change-source')), findsNothing);

    await tapOn(tester, find.byKey(const Key('relocation-resend')));
    final posts = api.callsTo('POST', movePath);
    expect(posts, hasLength(2));
    expect(posts[1].idempotencyKey, posts[0].idempotencyKey);
    expect(posts[1].body, posts[0].body);
    expect(find.byKey(const Key('relocation-done')), findsOneWidget);
  });

  testWidgets(
    'gated on ${WarehousePermissions.relocate} (contract); staff → hidden',
    (tester) async {
      await pump(tester, permissions: staffWarehousePermissions);
      expect(find.text(l10n.relocationForbidden), findsOneWidget);
      expect(api.calls, isEmpty);

      await pump(
        tester,
        permissions: staffWarehousePermissions,
        location: '/depo',
      );
      expect(find.byKey(const Key('warehouse-relocation')), findsNothing);
    },
  );

  testWidgets('warehouse without shelves → explained', (tester) async {
    api.on(
      'GET',
      '/api/v1/warehouses',
      (_) => FakeReply(
        200,
        json: {
          'items': [warehouseJson(usesLocations: false)],
        },
      ),
    );
    await pump(tester);
    expect(find.byKey(const Key('relocation-no-warehouse')), findsOneWidget);
  });
}
