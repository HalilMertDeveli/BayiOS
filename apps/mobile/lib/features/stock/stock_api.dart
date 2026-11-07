import 'package:bayios_mobile/core/api/api_client.dart';
import 'package:bayios_mobile/core/api/api_exception.dart';
import 'package:bayios_mobile/core/api/contract.dart';
import 'package:bayios_mobile/core/api/endpoint.dart';
import 'package:bayios_mobile/core/api/idempotency.dart';
import 'package:bayios_mobile/core/providers.dart';
import 'package:bayios_mobile/features/stock/stock_models.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

/// API operations of the stock feature (stock query, goods receipt,
/// adjustment). Stock only changes through the ledger endpoints
/// (`POST /inventory/receipts`, `POST /inventory/adjustments`); the app never
/// sets a quantity.
abstract final class StockEndpoints {
  static const balances = ApiEndpoint(
    HttpMethod.get,
    '/api/v1/inventory/balances',
  );
  static const warehouses = ApiEndpoint(HttpMethod.get, '/api/v1/warehouses');
  static const locations = ApiEndpoint(
    HttpMethod.get,
    '/api/v1/warehouses/{id}/locations',
  );
  static const variantLookup = ApiEndpoint(
    HttpMethod.get,
    '/api/v1/variants/lookup',
  );
  static const variants = ApiEndpoint(HttpMethod.get, '/api/v1/variants');
  static const variantBatch = ApiEndpoint(
    HttpMethod.get,
    '/api/v1/variants/batch',
  );
  static const createReceipt = ApiEndpoint(
    HttpMethod.post,
    '/api/v1/inventory/receipts',
    idempotent: true,
  );
  static const createAdjustment = ApiEndpoint(
    HttpMethod.post,
    '/api/v1/inventory/adjustments',
    idempotent: true,
  );

  static const List<ApiEndpoint> all = [
    balances,
    warehouses,
    locations,
    variantLookup,
    variants,
    variantBatch,
    createReceipt,
    createAdjustment,
  ];

  static const List<ContractSchema> schemas = [
    Warehouse.contract,
    Warehouse.listContract,
    StorageLocation.contract,
    StorageLocation.listContract,
    VariantRef.variantContract,
    VariantRef.variantPageContract,
    VariantRef.summaryContract,
    VariantRef.summaryListContract,
    StockBalance.contract,
    StockBalance.pageContract,
    InventoryPage.contract,
    StockMovement.contract,
    ReceiptResult.contract,
  ];
}

/// `GET /variants/batch` accepts 1–100 ids.
const variantBatchLimit = 100;

/// Balance rows per stock query (one page; the screen says when there are
/// more and asks for a narrower search).
const balancePageSize = 50;

class StockApi {
  const StockApi(this._api);

  final ApiClient _api;

  /// Balances matching a free-text term (SKU / name contains, exact
  /// barcode). 422 `inventory.search_too_broad` when it matches too much.
  Future<BalancePage> balances({
    String? query,
    String? variantId,
    int limit = balancePageSize,
  }) async {
    final response = await _api.send(
      StockEndpoints.balances,
      query: {'q': query, 'variantId': variantId, 'limit': '$limit'},
      decode: (json) => BalancePage.fromJson(asJsonObject(json)),
    );
    return response.data;
  }

  /// Warehouses, default first (≤ 1000, unpaged).
  Future<List<Warehouse>> warehouses() async {
    final response = await _api.send(
      StockEndpoints.warehouses,
      decode: (json) =>
          asJsonObject(json)
              .objectList('items')
              .map(Warehouse.fromJson)
              .toList(),
    );
    return response.data;
  }

  /// Bins of a warehouse in pick order (active only unless
  /// [includeInactive]: old balances may sit in a deactivated bin).
  Future<List<StorageLocation>> locations(
    String warehouseId, {
    bool includeInactive = false,
  }) async {
    final response = await _api.send(
      StockEndpoints.locations,
      pathParams: {'id': warehouseId},
      query: {if (includeInactive) 'includeInactive': 'true'},
      decode: (json) =>
          asJsonObject(json)
              .objectList('items')
              .map(StorageLocation.fromJson)
              .toList(),
    );
    return response.data;
  }

  /// SKU, names and barcode for up to [variantBatchLimit] ids; foreign or
  /// unknown ids are simply absent.
  Future<List<VariantRef>> variantsByIds(List<String> ids) async {
    if (ids.isEmpty) return const [];
    final response = await _api.send(
      StockEndpoints.variantBatch,
      query: {'ids': ids.take(variantBatchLimit).join(',')},
      decode: (json) =>
          asJsonObject(json)
              .objectList('items')
              .map(VariantRef.fromSummary)
              .toList(),
    );
    return response.data;
  }

  /// Exact match for a scanned or typed code: barcode first
  /// (`GET /variants/lookup`), then SKU (`GET /variants?sku=`) — the same
  /// order as the web scanner. Null when neither matches.
  Future<VariantRef?> findVariantByCode(String code) async {
    final value = code.trim();
    if (value.isEmpty) return null;
    try {
      final response = await _api.send(
        StockEndpoints.variantLookup,
        query: {'barcode': value},
        decode: (json) => VariantRef.fromVariant(asJsonObject(json)),
      );
      return response.data;
    } on ApiException catch (error) {
      if (error.status != 404 && error.status != 400) rethrow;
    }
    final response = await _api.send(
      StockEndpoints.variants,
      query: {'sku': value, 'limit': '2'},
      decode: (json) =>
          asJsonObject(json)
              .objectList('items')
              .map(VariantRef.fromVariant)
              .toList(),
    );
    return response.data.length == 1 ? response.data.single : null;
  }

  /// Goods receipt: one `receipt` movement per line (ledger).
  Future<ReceiptResult> createReceipt(
    Map<String, Object?> body,
    IdempotencyKey key,
  ) async {
    final response = await _api.send(
      StockEndpoints.createReceipt,
      idempotencyKey: key,
      body: body,
      decode: (json) => ReceiptResult.fromJson(asJsonObject(json)),
    );
    return response.data;
  }

  /// Manual adjustment (`inventory.adjust`): one movement, reason required.
  Future<StockMovement> createAdjustment(
    Map<String, Object?> body,
    IdempotencyKey key,
  ) async {
    final response = await _api.send(
      StockEndpoints.createAdjustment,
      idempotencyKey: key,
      body: body,
      decode: (json) => StockMovement.fromJson(asJsonObject(json)),
    );
    return response.data;
  }
}

final stockApiProvider = Provider<StockApi>(
  (ref) => StockApi(ref.watch(apiClientProvider)),
);
