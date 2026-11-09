import 'package:bayios_mobile/core/api/api_client.dart';
import 'package:bayios_mobile/core/api/contract.dart';
import 'package:bayios_mobile/core/api/endpoint.dart';
import 'package:bayios_mobile/core/api/idempotency.dart';
import 'package:bayios_mobile/core/providers.dart';
import 'package:bayios_mobile/features/stock/stock_api.dart';
import 'package:bayios_mobile/features/stock/stock_models.dart';
import 'package:bayios_mobile/features/warehouse/warehouse_models.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

/// API operations of the warehouse handheld screens (#168). Dispatching,
/// closing and cancelling transfers and starting / applying counts stay on
/// the web; the handheld only receives, counts and moves between shelves.
abstract final class WarehouseEndpoints {
  static const transfers = ApiEndpoint(
    HttpMethod.get,
    '/api/v1/stock-transfers',
  );
  static const transfer = ApiEndpoint(
    HttpMethod.get,
    '/api/v1/stock-transfers/{id}',
  );
  static const receive = ApiEndpoint(
    HttpMethod.post,
    '/api/v1/stock-transfers/{id}/receive',
    idempotent: true,
  );
  static const counts = ApiEndpoint(HttpMethod.get, '/api/v1/stock-counts');
  static const count = ApiEndpoint(HttpMethod.get, '/api/v1/stock-counts/{id}');
  static const countEntries = ApiEndpoint(
    HttpMethod.post,
    '/api/v1/stock-counts/{id}/entries',
    idempotent: true,
  );
  static const countSubmit = ApiEndpoint(
    HttpMethod.post,
    '/api/v1/stock-counts/{id}/submit',
    idempotent: true,
  );
  static const relocation = ApiEndpoint(
    HttpMethod.post,
    '/api/v1/inventory/relocations',
    idempotent: true,
  );

  static const List<ApiEndpoint> all = [
    transfers,
    transfer,
    receive,
    counts,
    count,
    countEntries,
    countSubmit,
    relocation,
    // Shared with the stock feature: warehouses / shelves / product lookup /
    // available stock at the source shelf.
    StockEndpoints.warehouses,
    StockEndpoints.locations,
    StockEndpoints.variantLookup,
    StockEndpoints.variants,
    StockEndpoints.balances,
  ];

  static const List<ContractSchema> schemas = [
    DocumentPage.contract,
    TransferSummary.contract,
    TransferSummary.pageContract,
    TransferLine.contract,
    TransferDetail.contract,
    TransferDetail.actionContract,
    CountSummary.contract,
    CountSummary.pageContract,
    CountLine.contract,
    CountDetail.contract,
    RelocationResult.contract,
    ...WarehouseRequests.all,
  ];
}

/// Rows per list page (the API allows up to 100).
const documentPageSize = 50;

/// `If-Match` value of a document version.
String versionTag(int version) => 'W/"$version"';

class WarehouseApi {
  const WarehouseApi(this._api);

  final ApiClient _api;

  /// Transfers in [statuses] that touch [warehouseId] (source or
  /// destination; the caller keeps the incoming ones).
  Future<TransferPage> transfers({
    required List<String> statuses,
    String? warehouseId,
    String? cursor,
  }) async {
    final response = await _api.send(
      WarehouseEndpoints.transfers,
      query: {
        'status': statuses.join(','),
        'warehouseId': warehouseId,
        'limit': '$documentPageSize',
        'cursor': cursor,
      },
      decode: (json) => TransferPage.fromJson(asJsonObject(json)),
    );
    return response.data;
  }

  Future<TransferDetail> transfer(String id) async {
    final response = await _api.send(
      WarehouseEndpoints.transfer,
      pathParams: {'id': id},
      decode: (json) => TransferDetail.fromJson(asJsonObject(json)),
    );
    return response.data;
  }

  /// One received quantity by scanned product code and shelf code. Sent
  /// without If-Match (like a pick scan): the API validates every receipt
  /// under the transfer lock, so a parallel receipt does not turn a valid
  /// scan into a 412.
  Future<TransferDetail> receive(
    String id,
    Map<String, Object?> body,
    IdempotencyKey key,
  ) async {
    final response = await _api.send(
      WarehouseEndpoints.receive,
      pathParams: {'id': id},
      idempotencyKey: key,
      body: body,
      decode: (json) => TransferDetail.fromAction(asJsonObject(json)),
    );
    return response.data;
  }

  Future<CountPage> counts({
    required List<String> statuses,
    String? cursor,
  }) async {
    final response = await _api.send(
      WarehouseEndpoints.counts,
      query: {
        'status': statuses.join(','),
        'limit': '$documentPageSize',
        'cursor': cursor,
      },
      decode: (json) => CountPage.fromJson(asJsonObject(json)),
    );
    return response.data;
  }

  Future<CountDetail> count(String id) async {
    final response = await _api.send(
      WarehouseEndpoints.count,
      pathParams: {'id': id},
      decode: (json) => CountDetail.fromJson(asJsonObject(json)),
    );
    return response.data;
  }

  /// Count entries (no If-Match, same reason as [receive]).
  Future<CountDetail> enterCount(
    String id,
    Map<String, Object?> body,
    IdempotencyKey key,
  ) async {
    final response = await _api.send(
      WarehouseEndpoints.countEntries,
      pathParams: {'id': id},
      idempotencyKey: key,
      body: body,
      decode: (json) => CountDetail.fromJson(asJsonObject(json)),
    );
    return response.data;
  }

  /// "Sayımı bitir" with `If-Match`: a count changed elsewhere (recount,
  /// cancel) answers 412 and the screen reloads.
  Future<CountDetail> submitCount(
    String id,
    int version,
    IdempotencyKey key,
  ) async {
    final response = await _api.send(
      WarehouseEndpoints.countSubmit,
      pathParams: {'id': id},
      idempotencyKey: key,
      ifMatch: versionTag(version),
      decode: (json) => CountDetail.fromJson(asJsonObject(json)),
    );
    return response.data;
  }

  /// Shelf-to-shelf move in one warehouse (ledger transfer_out + transfer_in).
  Future<RelocationResult> relocate(
    Map<String, Object?> body,
    IdempotencyKey key,
  ) async {
    final response = await _api.send(
      WarehouseEndpoints.relocation,
      idempotencyKey: key,
      body: body,
      decode: (json) => RelocationResult.fromJson(asJsonObject(json)),
    );
    return response.data;
  }

  /// Available stock of [variantId] on one shelf (null = no balance row).
  Future<StockBalance?> shelfBalance({
    required String variantId,
    required String warehouseId,
    required String locationId,
  }) async {
    final response = await _api.send(
      StockEndpoints.balances,
      query: {
        'variantId': variantId,
        'warehouseId': warehouseId,
        'locationId': locationId,
        'limit': '1',
      },
      decode: (json) => BalancePage.fromJson(asJsonObject(json)),
    );
    return response.data.items.firstOrNull;
  }
}

final warehouseApiProvider = Provider<WarehouseApi>(
  (ref) => WarehouseApi(ref.watch(apiClientProvider)),
);
