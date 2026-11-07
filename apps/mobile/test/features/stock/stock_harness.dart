import 'dart:convert';
import 'dart:typed_data';

import 'package:bayios_mobile/app/app.dart';
import 'package:bayios_mobile/core/api/api_client.dart';
import 'package:bayios_mobile/core/auth/auth_controller.dart';
import 'package:bayios_mobile/core/auth/auth_repository.dart';
import 'package:bayios_mobile/core/providers.dart';
import 'package:bayios_mobile/core/session/session_controller.dart';
import 'package:bayios_mobile/core/ui/app_theme.dart';
import 'package:bayios_mobile/features/stock/scanner.dart';
import 'package:bayios_mobile/features/stock/stock_errors.dart';
import 'package:bayios_mobile/features/stock/stock_feature.dart';
import 'package:bayios_mobile/l10n/app_localizations.dart';
import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_riverpod/misc.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:go_router/go_router.dart';

import '../../helpers/fakes.dart';

const tenantId = 't1';
const warehouseId = 'w-main';
const binA = 'loc-a';
const binB = 'loc-b';
const variantId = 'v-1';

/// One recorded call.
class Call {
  Call(this.method, this.path, this.query, this.headers, this.body);

  final String method;
  final String path;
  final Map<String, dynamic> query;
  final Map<String, dynamic> headers;
  final Object? body;

  String? get idempotencyKey => headers['Idempotency-Key'] as String?;
}

typedef ApiRoute = FakeReply Function(Call call);

/// dio adapter answering by `METHOD path` (routes may be swapped mid-test)
/// and recording every request with its decoded JSON body.
class RoutingAdapter implements HttpClientAdapter {
  final routes = <String, ApiRoute>{};

  /// Replies used once, before [routes], for the same `METHOD path`.
  final queued = <String, List<ApiRoute>>{};
  final calls = <Call>[];

  void on(String method, String path, ApiRoute route) =>
      routes['$method $path'] = route;

  void once(String method, String path, ApiRoute route) =>
      (queued['$method $path'] ??= []).add(route);

  List<Call> callsTo(String method, String path) => [
    for (final c in calls)
      if (c.method == method && c.path == path) c,
  ];

  @override
  Future<ResponseBody> fetch(
    RequestOptions options,
    Stream<Uint8List>? requestStream,
    Future<void>? cancelFuture,
  ) async {
    final data = options.data;
    final call = Call(
      options.method,
      options.uri.path,
      options.queryParameters,
      options.headers,
      data is String && data.isNotEmpty ? jsonDecode(data) : null,
    );
    calls.add(call);
    final key = '${call.method} ${call.path}';
    final queue = queued[key];
    final route = (queue != null && queue.isNotEmpty)
        ? queue.removeAt(0)
        : routes[key];
    if (route == null) throw StateError('No route for $key');
    final reply = route(call);
    final error = reply.error;
    if (error != null) {
      throw DioException(requestOptions: options, type: error);
    }
    return ResponseBody.fromString(
      reply.body,
      reply.status,
      headers: reply.headers,
    );
  }

  @override
  void close({bool force = false}) {}
}

/// Session fixed to one tenant with [permissions].
class FixedSession extends SessionController {
  FixedSession(this.permissions);

  final List<String> permissions;

  @override
  SessionState build() {
    final me = meFixture(
      memberships: [membershipJson(tenantId)],
      activeTenant: activeTenantJson(tenantId, permissions: permissions),
    );
    return SessionReady(
      me: me,
      memberships: me.memberships,
      membership: me.memberships.single,
      activeTenant: me.activeTenant!,
    );
  }
}

/// Scanner returning queued codes (null = cancelled / manual entry).
class FakeScanner implements BarcodeScanner {
  final codes = <String?>[];
  final titles = <String>[];

  @override
  Future<String?> scan(BuildContext context, {required String title}) async {
    titles.add(title);
    return codes.isEmpty ? null : codes.removeAt(0);
  }
}

const staffPermissions = [
  'tenant.read',
  'catalog.read',
  'inventory.read',
  'inventory.receive',
];
const List<String> managerPermissions = [
  ...staffPermissions,
  'inventory.adjust',
];
const viewerPermissions = ['tenant.read', 'catalog.read', 'inventory.read'];

/// Provider overrides wiring a real [ApiClient] to [adapter].
List<Override> stockOverrides(
  RoutingAdapter adapter, {
  List<String> permissions = staffPermissions,
  FakeScanner? scanner,
  List<Duration>? waits,
}) {
  final auth = FakeAuthRepository(user: const AuthUser(id: 'user-1'));
  final dio = Dio(BaseOptions(baseUrl: 'http://api.test'))
    ..httpClientAdapter = adapter;
  return [
    authRepositoryProvider.overrideWithValue(auth),
    sessionControllerProvider.overrideWith(() => FixedSession(permissions)),
    apiClientProvider.overrideWithValue(
      ApiClient(
        dio: dio,
        tokens: auth,
        activeTenantId: () => tenantId,
        clientVersion: 'test',
        clientPlatform: 'android',
      ),
    ),
    barcodeScannerProvider.overrideWithValue(scanner ?? FakeScanner()),
    stockDelayProvider.overrideWithValue((d) async => waits?.add(d)),
  ];
}

// ------------------------------------------------------------- JSON data

Map<String, Object?> warehouseJson({
  String id = warehouseId,
  String name = 'Ana Depo',
  bool usesLocations = true,
  bool isDefault = true,
  String status = 'active',
}) => {
  'id': id,
  'code': id.toUpperCase(),
  'name': name,
  'usesLocations': usesLocations,
  'isDefault': isDefault,
  'status': status,
  'version': 1,
  'createdAt': '2026-11-01T10:00:00Z',
  'updatedAt': null,
};

Map<String, Object?> locationJson(
  String id,
  String code, {
  String? barcode,
  String warehouse = warehouseId,
}) => {
  'id': id,
  'warehouseId': warehouse,
  'code': code,
  'barcode': barcode,
  'pickSequence': 1,
  'isActive': true,
  'version': 1,
  'createdAt': '2026-11-01T10:00:00Z',
  'updatedAt': null,
};

Map<String, Object?> variantJson({
  String id = variantId,
  String sku = 'FLT-001',
  String? barcode = '8690000000011',
  String? name = 'Yağ filtresi',
  String status = 'active',
}) => {
  'id': id,
  'productId': 'p-$id',
  'sku': sku,
  'barcode': barcode,
  'name': name,
  'optionValues': <String, String>{},
  'unitId': 'u-1',
  'status': status,
  'attributes': <String, Object?>{},
  'version': 1,
  'createdAt': '2026-11-01T10:00:00Z',
  'createdBy': null,
  'updatedAt': null,
  'updatedBy': null,
};

Map<String, Object?> summaryJson({
  String id = variantId,
  String sku = 'FLT-001',
  String productName = 'Yağ filtresi',
}) => {
  'id': id,
  'productId': 'p-$id',
  'sku': sku,
  'name': null,
  'productName': productName,
  'barcode': '8690000000011',
  'status': 'active',
};

Map<String, Object?> balanceJson({
  String id = 'b-1',
  String variant = variantId,
  String warehouse = warehouseId,
  String? location = binA,
  Object onHand = 12,
  Object reserved = 2,
  Object available = 10,
  Object? minQuantity,
  bool belowMinimum = false,
}) => {
  'id': id,
  'variantId': variant,
  'warehouseId': warehouse,
  'locationId': location,
  'onHand': onHand,
  'reserved': reserved,
  'available': available,
  'version': 3,
  'updatedAt': '2026-11-02T10:00:00Z',
  'minQuantity': minQuantity,
  'belowMinimum': belowMinimum,
};

Map<String, Object?> movementJson({
  String id = 'm-1',
  String type = 'receipt',
  num quantity = 5,
  num balanceAfter = 17,
  String? location = binA,
}) => {
  'id': id,
  'variantId': variantId,
  'warehouseId': warehouseId,
  'locationId': location,
  'type': type,
  'quantity': quantity,
  'unitCost': null,
  'sourceType': 'receipt',
  'sourceId': 'r-1',
  'sourceLineId': null,
  'reason': null,
  'balanceAfter': balanceAfter,
  'correlationId': null,
  'occurredAt': '2026-11-08T10:00:00Z',
  'createdBy': 'user-1',
};

Map<String, Object?> problem(int status, String code, {Object? extra}) => {
  'type': 'about:blank',
  'title': code,
  'status': status,
  'code': code,
  if (extra is Map<String, Object?>) ...extra,
};

/// Routes for a warehouse with two bins and one known product.
void defaultRoutes(RoutingAdapter api) {
  api
    ..on(
      'GET',
      '/api/v1/warehouses',
      (_) => FakeReply(
        200,
        json: {
          'items': [warehouseJson()],
        },
      ),
    )
    ..on(
      'GET',
      '/api/v1/warehouses/$warehouseId/locations',
      (_) => FakeReply(
        200,
        json: {
          'items': [
            locationJson(binA, 'A-01-01', barcode: 'LOC-A0101'),
            locationJson(binB, 'B-02-03'),
          ],
        },
      ),
    )
    ..on('GET', '/api/v1/variants/lookup', (call) {
      final barcode = call.query['barcode'];
      return barcode == '8690000000011'
          ? FakeReply(200, json: variantJson())
          : FakeReply(404, json: problem(404, 'resource.not_found'));
    })
    ..on('GET', '/api/v1/variants', (call) {
      final sku = call.query['sku'];
      return FakeReply(
        200,
        json: {
          'items': [if (sku == 'FLT-001') variantJson()],
          'page': {'nextCursor': null, 'hasMore': false, 'limit': 2},
        },
      );
    })
    ..on(
      'GET',
      '/api/v1/variants/batch',
      (_) => FakeReply(
        200,
        json: {
          'items': [summaryJson()],
        },
      ),
    )
    ..on(
      'POST',
      '/api/v1/inventory/receipts',
      (_) => FakeReply(
        201,
        json: {
          'receiptId': 'r-1',
          'warehouseId': warehouseId,
          'movements': [movementJson()],
        },
      ),
    );
}

/// Pumps the stock feature's routes (`/stok`, `/stok/mal-kabul`,
/// `/stok/duzeltme`) at [location] with a real router.
Future<GoRouter> pumpStock(
  WidgetTester tester,
  List<Override> overrides, {
  String location = '/stok',
}) async {
  // A phone in portrait (411 × 891 dp), not the default 800 × 600.
  tester.view
    ..physicalSize = const Size(1080, 2340)
    ..devicePixelRatio = 2.625;
  addTearDown(tester.view.reset);
  final router = GoRouter(
    initialLocation: location,
    routes: [
      GoRoute(
        path: stockFeature.path,
        builder: stockFeature.builder,
        routes: stockFeature.routes,
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

/// Scrolls [finder] into view, taps it and settles.
Future<void> tapOn(WidgetTester tester, Finder finder) async {
  if (finder.evaluate().isEmpty) {
    // Not built yet by the lazy list: scroll down until it is.
    await tester.scrollUntilVisible(
      finder,
      200,
      scrollable: find
          .descendant(
            of: find.byType(ListView),
            matching: find.byType(Scrollable),
          )
          .first,
    );
  }
  await tester.ensureVisible(finder);
  await tester.pumpAndSettle();
  await tester.tap(finder);
  await tester.pumpAndSettle();
}
