import 'package:bayios_mobile/app/app.dart';
import 'package:bayios_mobile/core/ui/app_theme.dart';
import 'package:bayios_mobile/features/picking/scan_alerts.dart';
import 'package:bayios_mobile/features/warehouse/warehouse_feature.dart';
import 'package:bayios_mobile/l10n/app_localizations.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_riverpod/misc.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:go_router/go_router.dart';

import '../../helpers/fakes.dart';
import '../picking/fake_picking_server.dart' show RecordingAlerts;
import '../stock/stock_harness.dart';

export '../picking/fake_picking_server.dart' show RecordingAlerts;
export '../stock/stock_harness.dart';

const transferId = 'tr-1';
const countId = 'sc-1';
const sourceWarehouseId = 'w-north';
const lineA = 'tl-a';
const lineB = 'tl-b';

/// warehouse_staff: receive transfers, count and move between shelves
/// (`inventory.relocate`, #98).
const staffWarehousePermissions = [
  'tenant.read',
  'catalog.read',
  'inventory.read',
  'inventory.receive',
  'inventory.transfer.receive',
  'inventory.count.perform',
  'inventory.relocate',
];

/// manager: everything above plus transfer and count management.
const List<String> managerWarehousePermissions = [
  ...staffWarehousePermissions,
  'inventory.transfer',
  'inventory.count.manage',
  'inventory.adjust',
];

/// Overrides of [stockOverrides] plus recorded scan alerts.
List<Override> warehouseOverrides(
  RoutingAdapter adapter, {
  List<String> permissions = staffWarehousePermissions,
  FakeScanner? scanner,
  RecordingAlerts? alerts,
  List<Duration>? waits,
}) => [
  ...stockOverrides(
    adapter,
    permissions: permissions,
    scanner: scanner,
    waits: waits,
  ),
  scanAlertsProvider.overrideWithValue(alerts ?? RecordingAlerts()),
];

/// Pumps the warehouse feature's routes at [location] with a real router.
Future<GoRouter> pumpWarehouse(
  WidgetTester tester,
  List<Override> overrides, {
  String location = '/depo',
}) async {
  tester.view
    ..physicalSize = const Size(1080, 2340)
    ..devicePixelRatio = 2.625;
  addTearDown(tester.view.reset);
  final router = GoRouter(
    initialLocation: location,
    routes: [
      GoRoute(
        path: warehouseFeature.path,
        builder: warehouseFeature.builder,
        routes: warehouseFeature.routes,
      ),
    ],
  );
  addTearDown(router.dispose);
  await tester.pumpWidget(
    ProviderScope(
      retry: (_, _) => null,
      overrides: overrides,
      child: MaterialApp.router(
        theme: buildAppTheme(),
        locale: appLocale,
        supportedLocales: AppLocalizations.supportedLocales,
        localizationsDelegates: AppLocalizations.localizationsDelegates,
        routerConfig: router,
      ),
    ),
  );
  await tester.pumpAndSettle();
  return router;
}

/// Types [code] into the field with [key] and presses Enter (what a
/// keyboard-wedge scanner does).
Future<void> typeInto(WidgetTester tester, String key, String code) async {
  final finder = find.byKey(Key(key));
  await scrollTo(tester, finder);
  await tester.enterText(finder, code);
  await tester.testTextInput.receiveAction(TextInputAction.search);
  await tester.pumpAndSettle();
}

// ------------------------------------------------------------- JSON data

Map<String, Object?> pageInfo({String? next}) => {
  'nextCursor': next,
  'hasMore': next != null,
  'limit': 50,
};

Map<String, Object?> transferSummaryJson({
  String id = transferId,
  String number = 'TRF-2026-0007',
  String status = 'in_transit',
  String destination = warehouseId,
  String source = sourceWarehouseId,
  num quantity = 10,
  num received = 0,
  bool overdue = false,
  int version = 3,
}) => {
  'id': id,
  'number': number,
  'sourceWarehouseId': source,
  'sourceWarehouseName': source == warehouseId ? 'Ana Depo' : 'Kuzey Depo',
  'destinationWarehouseId': destination,
  'destinationWarehouseName': destination == warehouseId
      ? 'Ana Depo'
      : 'Kuzey Depo',
  'status': status,
  'note': null,
  'lineCount': 2,
  'quantity': quantity,
  'receivedQuantity': received,
  'inTransitQuantity': quantity - received,
  'returnedQuantity': 0,
  'lostQuantity': 0,
  'discrepancyQuantity': quantity - received,
  'hasDiscrepancy': false,
  'isOverdue': overdue,
  'dispatchedAt': '2026-11-08T08:00:00Z',
  'lastReceivedAt': null,
  'completedAt': null,
  'createdAt': '2026-11-08T07:00:00Z',
  'version': version,
};

Map<String, Object?> transferLineJson({
  String id = lineA,
  int lineNo = 1,
  String variant = variantId,
  String sku = 'FLT-001',
  num quantity = 6,
  num received = 0,
  String? planned = binA,
  String? plannedCode = 'A-01-01',
}) => {
  'id': id,
  'lineNo': lineNo,
  'variantId': variant,
  'sku': sku,
  'sourceLocationId': 'src-1',
  'sourceLocationCode': 'K-01',
  'destinationLocationId': planned,
  'destinationLocationCode': plannedCode,
  'quantity': quantity,
  'receivedQuantity': received,
  'inTransitQuantity': quantity - received,
  'returnedQuantity': 0,
  'lostQuantity': 0,
  'discrepancyQuantity': quantity - received,
};

/// A transfer with line A (FLT-001, 6) and line B (BLT-002, 4).
Map<String, Object?> transferJson({
  num receivedA = 0,
  num receivedB = 0,
  String? status,
  int version = 3,
}) {
  final received = receivedA + receivedB;
  return {
    'transfer': transferSummaryJson(
      received: received,
      status:
          status ??
          (received == 0
              ? 'in_transit'
              : received >= 10
              ? 'received'
              : 'partially_received'),
      version: version,
    ),
    'createdBy': null,
    'dispatchedBy': null,
    'closeResolution': null,
    'closeReason': null,
    'closedBy': null,
    'cancelledAt': null,
    'cancelReason': null,
    'lines': [
      transferLineJson(received: receivedA),
      transferLineJson(
        id: lineB,
        lineNo: 2,
        variant: 'v-2',
        sku: 'BLT-002',
        quantity: 4,
        received: receivedB,
        planned: null,
        plannedCode: null,
      ),
    ],
  };
}

Map<String, Object?> countSummaryJson({
  String id = countId,
  String status = 'open',
  bool blind = false,
  bool hidden = false,
  int lines = 2,
  int counted = 0,
  int version = 5,
}) => {
  'id': id,
  'number': 'SAY-2026-0003',
  'warehouseId': warehouseId,
  'warehouseName': 'Ana Depo',
  'scope': 'locations',
  'isBlind': blind,
  'quantitiesHidden': hidden,
  'status': status,
  'note': null,
  'lineCount': lines,
  'countedLineCount': counted,
  'snapshotAt': '2026-11-09T07:00:00Z',
  'submittedAt': null,
  'appliedAt': null,
  'createdAt': '2026-11-09T07:00:00Z',
  'version': version,
};

Map<String, Object?> countLineJson({
  String id = 'cl-1',
  String variant = variantId,
  String sku = 'FLT-001',
  String? location = binA,
  String? locationCode = 'A-01-01',
  num? expected = 12,
  num? counted,
  String? countedAt,
}) => {
  'id': id,
  'variantId': variant,
  'sku': sku,
  'locationId': location,
  'locationCode': locationCode,
  'expectedQuantity': expected,
  'countedQuantity': counted,
  'difference': counted == null || expected == null ? null : counted - expected,
  'snapshotAt': '2026-11-09T07:00:00Z',
  'countedAt': countedAt,
  'status': counted == null ? 'pending' : 'counted',
  'movedSinceSnapshot': false,
  'appliedDelta': null,
};

Map<String, Object?> countJson({
  num? countedA,
  num? countedB,
  String status = 'open',
  bool hidden = false,
  int version = 5,
}) {
  final counted = [countedA, countedB].whereType<num>().length;
  return {
    'count': countSummaryJson(
      status: status,
      blind: hidden,
      hidden: hidden,
      counted: counted,
      version: version,
    ),
    'locationIds': [binA],
    'variantIds': <String>[],
    'differenceCount': null,
    'movedCount': null,
    'createdBy': null,
    'submittedBy': null,
    'appliedBy': null,
    'cancelledAt': null,
    'cancelReason': null,
    'lines': [
      countLineJson(
        expected: hidden ? null : 12,
        counted: countedA,
        countedAt: countedA == null ? null : '2026-11-09T08:00:0${version}Z',
      ),
      countLineJson(
        id: 'cl-2',
        variant: 'v-2',
        sku: 'BLT-002',
        expected: hidden ? null : 4,
        counted: countedB,
        countedAt: countedB == null ? null : '2026-11-09T07:30:00Z',
      ),
    ],
  };
}

/// Warehouses (Ana Depo with shelves + Kuzey Depo) and Ana Depo's shelves.
void warehouseRoutes(RoutingAdapter api) {
  defaultRoutes(api);
  api.on(
    'GET',
    '/api/v1/warehouses',
    (_) => FakeReply(
      200,
      json: {
        'items': [
          warehouseJson(),
          warehouseJson(
            id: sourceWarehouseId,
            name: 'Kuzey Depo',
            isDefault: false,
          ),
        ],
      },
    ),
  );
}
