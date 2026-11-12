// Design screenshots of every screen, light and dark, at 375×812 (2×) with
// the bundled Inter Tight and the kit icons. A tool, not a check: skipped
// unless run with
//
//   flutter test --dart-define=BAYIOS_SCREENSHOTS=true test/screenshots
//
// PNGs: docs/design/screenshots/mobile/<screen>-<light|dark>.png.
import 'package:bayios_mobile/app/app.dart';
import 'package:bayios_mobile/app/router.dart';
import 'package:bayios_mobile/core/api/api_client.dart';
import 'package:bayios_mobile/core/auth/auth_controller.dart';
import 'package:bayios_mobile/core/auth/auth_repository.dart';
import 'package:bayios_mobile/core/providers.dart';
import 'package:bayios_mobile/core/session/active_tenant_store.dart';
import 'package:bayios_mobile/core/session/me.dart';
import 'package:bayios_mobile/core/session/session_controller.dart';
import 'package:bayios_mobile/features/picking/picking_api.dart';
import 'package:bayios_mobile/features/picking/scan_alerts.dart';
import 'package:bayios_mobile/features/stock/adjustment_screen.dart';
import 'package:bayios_mobile/features/stock/scanner.dart';
import 'package:bayios_mobile/features/stock/stock_errors.dart';
import 'package:bayios_mobile/features/stock/stock_models.dart';
import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:google_fonts/google_fonts.dart';

import '../features/picking/fake_picking_server.dart' as picking;
import '../features/stock/stock_harness.dart' as stock;
import '../features/warehouse/warehouse_harness.dart' as warehouse;
import '../helpers/fakes.dart';
import 'screenshot_support.dart';

const _allPermissions = [
  'tenant.read',
  'tasks.read.own',
  'tasks.read.all',
  'tasks.claim',
  'tasks.pick',
  'tasks.pack',
  'tasks.report_shortage',
  'orders.read',
  'catalog.read',
  'inventory.read',
  'inventory.receive',
  'inventory.adjust',
  'inventory.transfer.receive',
  'inventory.count.perform',
  'inventory.transfer',
  'inventory.relocate',
];

/// Session fixed to [state] (gate screens) or ready with every permission.
class _Session extends SessionController {
  _Session(this.fixed);

  final SessionState? fixed;

  @override
  SessionState build() {
    if (fixed case final state?) return state;
    final me = meFixture(
      memberships: [
        membershipJson(stock.tenantId, name: 'Yıldız Oto Yedek Parça'),
        membershipJson('t2', name: 'Kuzey Dağıtım', role: 'manager'),
      ],
      activeTenant: activeTenantJson(
        stock.tenantId,
        permissions: _allPermissions,
      ),
    );
    return SessionReady(
      me: me,
      memberships: me.memberships,
      membership: me.memberships.first,
      activeTenant: me.activeTenant!,
    );
  }
}

class _Scanner implements BarcodeScanner {
  @override
  Future<String?> scan(BuildContext context, {required String title}) async =>
      null;
}

Map<String, Object?> _task(
  picking.FakePickingServer server, {
  required String id,
  required String number,
  String status = 'open',
  String? assignedTo = picking.userId,
  String priority = 'normal',
  String? customer,
  int lines = 3,
  num toPick = 12,
  num picked = 0,
  num short = 0,
}) => {
  ...server.summaryJson(),
  'id': id,
  'orderNumber': number,
  'status': status,
  'assignedTo': assignedTo,
  'assigneeName': assignedTo == null ? null : 'Ayşe Depocu',
  'priority': priority,
  'customerName': customer,
  'lineCount': lines,
  'quantityToPick': toPick,
  'quantityPicked': picked,
  'quantityShort': short,
};

picking.FakePickingServer _pickingServer({bool empty = false}) {
  final server = picking.FakePickingServer()..priority = 'high';
  List<Map<String, Object?>> tasks() => empty
      ? const []
      : [
          {
            ...server.summaryJson(),
            'status': 'in_progress',
            'quantityPicked': 1,
          },
          _task(
            server,
            id: 'task-2',
            number: 'SO-1002',
            priority: 'urgent',
            customer: 'Kuzey Otomotiv',
            picked: 6,
          ),
          _task(
            server,
            id: 'task-3',
            number: 'SO-1003',
            customer: 'Ege Filtre Ltd.',
            lines: 5,
            toPick: 20,
            picked: 20,
            status: 'completed',
          ),
          _task(
            server,
            id: 'task-4',
            number: 'SO-1004',
            assignedTo: null,
            customer: 'Marmara Yedek',
          ),
        ];
  server
    ..mineItems = tasks
    ..listItems = tasks;
  return server;
}

stock.RoutingAdapter _api({bool offline = false}) {
  final api = stock.RoutingAdapter();
  warehouse.warehouseRoutes(api);
  if (offline) {
    api.on(
      'GET',
      '/api/v1/inventory/balances',
      (_) => FakeReply.fail(DioExceptionType.connectionError),
    );
  } else {
    api.on(
      'GET',
      '/api/v1/inventory/balances',
      (_) => FakeReply(
        200,
        json: {
          'items': [
            stock.balanceJson(),
            stock.balanceJson(
              id: 'b-2',
              location: stock.binB,
              onHand: '3.5',
              reserved: 0,
              available: '3.5',
              minQuantity: 20,
              belowMinimum: true,
            ),
          ],
          'page': {'nextCursor': null, 'hasMore': false, 'limit': 50},
        },
      ),
    );
  }
  api
    ..on(
      'GET',
      '/api/v1/stock-transfers',
      (_) => FakeReply(
        200,
        json: {
          'items': [
            warehouse.transferSummaryJson(),
            warehouse.transferSummaryJson(
              id: 'tr-2',
              number: 'TRF-2026-0008',
              status: 'partially_received',
              received: 4,
              overdue: true,
            ),
          ],
          'page': warehouse.pageInfo(),
        },
      ),
    )
    ..on(
      'GET',
      '/api/v1/stock-transfers/${warehouse.transferId}',
      (_) => FakeReply(200, json: warehouse.transferJson(receivedA: 2)),
    )
    ..on(
      'GET',
      '/api/v1/stock-counts',
      (_) => FakeReply(
        200,
        json: {
          'items': [
            warehouse.countSummaryJson(counted: 1),
            warehouse.countSummaryJson(id: 'sc-2', lines: 14, counted: 9),
          ],
          'page': warehouse.pageInfo(),
        },
      ),
    )
    ..on(
      'GET',
      '/api/v1/stock-counts/${warehouse.countId}',
      (_) => FakeReply(200, json: warehouse.countJson(countedA: 11)),
    );
  return api;
}

/// One screen to capture.
class _Shot {
  const _Shot(
    this.name,
    this.location, {
    this.session,
    this.extra,
    this.scrollToEnd = false,
  });

  final String name;
  final String location;

  /// Gate screens: the session state that routes there.
  final SessionState? session;

  /// Route `extra` (the adjustment target).
  final Object? extra;

  /// Scroll the page to its end before the capture.
  final bool scrollToEnd;
}

final Me _me = meFixture(
  memberships: [
    membershipJson(stock.tenantId, name: 'Yıldız Oto Yedek Parça'),
    membershipJson('t2', name: 'Kuzey Dağıtım', role: 'manager'),
    membershipJson(
      't3',
      name: 'Eski Şube',
      role: 'viewer',
      tenantStatus: 'suspended',
    ),
  ],
);

final List<_Shot> _shots = [
  const _Shot('sign-in', AppRoutes.signIn, session: SessionSignedOut()),
  const _Shot('session-loading', AppRoutes.loading, session: SessionLoading()),
  _Shot(
    'tenant-picker',
    AppRoutes.tenantPicker,
    session: SessionNeedsTenant(
      _me,
      _me.memberships,
      currentTenantId: stock.tenantId,
    ),
  ),
  _Shot(
    'no-membership',
    AppRoutes.noMembership,
    session: SessionNoMembership(_me),
  ),
  const _Shot('dashboard', '/pano'),
  const _Shot('tasks', '/gorevler'),
  const _Shot('tasks-empty', '/gorevler'),
  const _Shot('picking', '/gorevler/task-1'),
  const _Shot('stock-idle', '/stok'),
  const _Shot('stock-query', '/stok?q=FLT-001'),
  const _Shot('stock-error', '/stok?q=FLT-001'),
  const _Shot('receiving', '/stok/mal-kabul'),
  _Shot(
    'adjustment',
    '/stok/duzeltme',
    extra: AdjustmentTarget(
      variant: VariantRef.fromSummary(stock.summaryJson()),
      balance: StockBalance.fromJson(stock.balanceJson()),
      warehouse: Warehouse.fromJson(stock.warehouseJson()),
      location: StorageLocation.fromJson(
        stock.locationJson(stock.binA, 'A-01-01'),
      ),
    ),
  ),
  const _Shot('warehouse', '/depo'),
  const _Shot('transfers', '/depo/transferler'),
  const _Shot('transfer-receive', '/depo/transferler/${warehouse.transferId}'),
  const _Shot('counts', '/depo/sayimlar'),
  const _Shot('count', '/depo/sayimlar/${warehouse.countId}'),
  const _Shot('relocation', '/depo/raf-tasima'),
  const _Shot('profile', '/profil'),
  const _Shot('profile-about', '/profil', scrollToEnd: true),
];

Future<void> _pumpShot(
  WidgetTester tester,
  _Shot shot,
  Brightness brightness,
) async {
  usePhoneViewport(tester);
  tester.platformDispatcher.platformBrightnessTestValue = brightness;
  addTearDown(tester.platformDispatcher.clearPlatformBrightnessTestValue);

  final server = _pickingServer(empty: shot.name == 'tasks-empty');
  final api = _api(offline: shot.name == 'stock-error');
  final auth = FakeAuthRepository(
    user: shot.session is SessionSignedOut
        ? null
        : const AuthUser(
            id: picking.userId,
            email: 'ayse@ornek.test',
            fullName: 'Ayşe Depocu',
          ),
  );
  final dio = Dio(BaseOptions(baseUrl: 'http://api.test'))
    ..httpClientAdapter = api;
  await tester.pumpWidget(
    RepaintBoundary(
      key: const Key('shot'),
      child: ProviderScope(
        retry: (_, _) => null,
        overrides: [
          authRepositoryProvider.overrideWithValue(auth),
          sessionControllerProvider.overrideWith(() => _Session(shot.session)),
          identityApiProvider.overrideWithValue(
            FakeIdentityApi((_) async => _me),
          ),
          activeTenantStoreProvider.overrideWithValue(
            MemoryActiveTenantStore(),
          ),
          apiClientProvider.overrideWithValue(
            ApiClient(
              dio: dio,
              tokens: auth,
              activeTenantId: () => stock.tenantId,
              clientVersion: 'test',
              clientPlatform: 'android',
            ),
          ),
          pickingApiProvider.overrideWithValue(picking.pickingApiFor(server)),
          scanAlertsProvider.overrideWithValue(picking.RecordingAlerts()),
          barcodeScannerProvider.overrideWithValue(_Scanner()),
          stockDelayProvider.overrideWithValue((_) async {}),
        ],
        child: const BayiOsApp(),
      ),
    ),
  );
  await tester.pump();
  final container = ProviderScope.containerOf(
    tester.element(find.byType(MaterialApp)),
  );
  container.read(routerProvider).go(shot.location, extra: shot.extra);
  await tester.pump();
  await settle(tester);
  if (shot.scrollToEnd) {
    await tester.drag(find.byType(Scrollable).first, const Offset(0, -2000));
    await settle(tester);
  }
}

void main() {
  setUpAll(() async {
    await loadManifestFonts();
    // Inter Tight from assets/google_fonts (every weight the theme uses).
    for (final weight in [
      FontWeight.w400,
      FontWeight.w500,
      FontWeight.w600,
      FontWeight.w700,
    ]) {
      GoogleFonts.interTight(fontWeight: weight);
    }
    await GoogleFonts.pendingFonts();
  });

  for (final brightness in Brightness.values) {
    final mode = brightness == Brightness.dark ? 'dark' : 'light';
    for (final shot in _shots) {
      testWidgets('${shot.name}-$mode', skip: !screenshotsEnabled, (
        tester,
      ) async {
        await _pumpShot(tester, shot, brightness);
        await capture(tester, const Key('shot'), '${shot.name}-$mode');
      });
    }
  }
}
