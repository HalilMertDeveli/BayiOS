import 'package:bayios_mobile/core/api/api_client.dart';
import 'package:bayios_mobile/core/api/contract.dart';
import 'package:bayios_mobile/core/api/endpoint.dart';
import 'package:bayios_mobile/core/api/idempotency.dart';
import 'package:bayios_mobile/core/providers.dart';
import 'package:bayios_mobile/features/picking/picking_models.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

/// Picking endpoints (api-strategy §12.8), contract-checked.
abstract final class PickingEndpoints {
  /// `tasks.read.own`: my active tasks, then the pool (`scope`).
  static const mine = ApiEndpoint(HttpMethod.get, '/api/v1/picking-tasks/mine');

  /// `tasks.read.all`: every task, keyset paged.
  static const list = ApiEndpoint(HttpMethod.get, '/api/v1/picking-tasks');

  /// `tasks.read.own` or `tasks.read.all`; returns the ETag.
  static const get = ApiEndpoint(HttpMethod.get, '/api/v1/picking-tasks/{id}');

  /// `tasks.claim`: take a pool task (two claims → one 409).
  static const claim = ApiEndpoint(
    HttpMethod.post,
    '/api/v1/picking-tasks/{id}/claim',
    idempotent: true,
  );

  /// `tasks.pick`: one scan (code + shelf + quantity).
  static const scan = ApiEndpoint(
    HttpMethod.post,
    '/api/v1/picking-tasks/{id}/scans',
    idempotent: true,
  );

  /// `tasks.report_shortage`.
  static const shortage = ApiEndpoint(
    HttpMethod.post,
    '/api/v1/picking-tasks/{id}/lines/{lineId}/shortage',
    idempotent: true,
  );

  /// `tasks.pick`: every line picked or short → completed.
  static const complete = ApiEndpoint(
    HttpMethod.post,
    '/api/v1/picking-tasks/{id}/complete',
    idempotent: true,
  );

  /// `orders.read` (warehouse staff: orders behind their tasks, no prices).
  static const order = ApiEndpoint(HttpMethod.get, '/api/v1/orders/{id}');

  static const List<ApiEndpoint> all = [
    mine,
    list,
    get,
    claim,
    scan,
    shortage,
    complete,
    order,
  ];

  static const List<ContractSchema> schemas = [
    PickingTaskSummary.contract,
    PickingTaskLine.contract,
    PickingTask.contract,
    PickLineProgress.contract,
    PickScanResult.contract,
    PickScanRequest.contract,
    ReportShortageRequest.contract,
    FulfillmentPage.contract,
    PickingTaskPage.pageContract,
    PickingTaskPage.mineContract,
    PickOrderInfo.detailContract,
    PickOrderInfo.contract,
  ];
}

/// Which slice of `GET /picking-tasks/mine`.
enum MyTasksScope {
  assigned('assigned'),
  pool('pool'),
  all('all');

  MyTasksScope(this.wire);

  final String wire;
}

/// Calls of the picking screens. Writes take the key of their user action
/// ([IdempotentAction]); the screen reuses it for "Tekrar gönder".
class PickingApi {
  const PickingApi(this._api);

  final ApiClient _api;

  /// API maximum page size is 200; the work list stays short.
  static const pageSize = 50;

  Future<List<PickingTaskSummary>> mine(MyTasksScope scope) async {
    final response = await _api.send(
      PickingEndpoints.mine,
      query: {'scope': scope.wire, 'limit': '$pageSize'},
      decode: (json) => PickingTaskPage.fromJson(asJsonObject(json)),
    );
    return response.data.items;
  }

  /// Every task with one of [statuses] (comma list on the wire).
  Future<PickingTaskPage> list({
    List<String> statuses = PickingTaskStatus.active,
    String? cursor,
  }) async {
    final response = await _api.send(
      PickingEndpoints.list,
      query: {
        if (statuses.isNotEmpty) 'status': statuses.join(','),
        'limit': '$pageSize',
        'cursor': cursor,
      },
      decode: (json) => PickingTaskPage.fromJson(asJsonObject(json)),
    );
    return response.data;
  }

  Future<PickingTask> get(String id) async => (await _api.send(
    PickingEndpoints.get,
    pathParams: {'id': id},
    decode: (json) => PickingTask.fromJson(asJsonObject(json)),
  )).data;

  /// Sent without If-Match (as on the web): the API's row lock decides
  /// between two claims (`picking.already_assigned`).
  Future<PickingTask> claim(String id, IdempotencyKey key) async =>
      (await _api.send(
        PickingEndpoints.claim,
        pathParams: {'id': id},
        idempotencyKey: key,
        decode: (json) => PickingTask.fromJson(asJsonObject(json)),
      )).data;

  /// Sent without If-Match on purpose (same as the web): scans come faster
  /// than the view refreshes and the API judges every one under its lock
  /// (wrong item / shelf, over-pick). The Idempotency-Key makes a resent
  /// scan count once.
  Future<PickScanResult> scan(
    String id,
    PickScanRequest request,
    IdempotencyKey key,
  ) async => (await _api.send(
    PickingEndpoints.scan,
    pathParams: {'id': id},
    idempotencyKey: key,
    body: request.toJson(),
    decode: (json) => PickScanResult.fromJson(asJsonObject(json)),
  )).data;

  /// The API checks the quantity against what is still open on the line.
  Future<PickingTask> reportShortage(
    String id,
    String lineId,
    ReportShortageRequest request,
    IdempotencyKey key,
  ) async => (await _api.send(
    PickingEndpoints.shortage,
    pathParams: {'id': id, 'lineId': lineId},
    idempotencyKey: key,
    body: request.toJson(),
    decode: (json) => PickingTask.fromJson(asJsonObject(json)),
  )).data;

  /// With If-Match: completing a task someone changed meanwhile answers
  /// 412 `concurrency.version_mismatch` and the screen reloads.
  Future<PickingTask> complete(
    String id, {
    required String ifMatch,
    required IdempotencyKey key,
  }) async => (await _api.send(
    PickingEndpoints.complete,
    pathParams: {'id': id},
    idempotencyKey: key,
    ifMatch: ifMatch,
    decode: (json) => PickingTask.fromJson(asJsonObject(json)),
  )).data;

  Future<PickOrderInfo> order(String orderId) async => (await _api.send(
    PickingEndpoints.order,
    pathParams: {'id': orderId},
    decode: (json) => PickOrderInfo.fromJson(asJsonObject(json)),
  )).data;
}

final pickingApiProvider = Provider<PickingApi>(
  (ref) => PickingApi(ref.watch(apiClientProvider)),
);
