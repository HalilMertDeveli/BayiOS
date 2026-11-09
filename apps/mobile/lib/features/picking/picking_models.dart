import 'package:bayios_mobile/core/api/contract.dart';

/// Picking task models (packages/contracts/openapi.json, tag "Picking").
/// Quantities and versions are `number | string` on the wire (decimal-safe
/// JSON); they are read with [_num] / [_int].

double _num(Map<String, Object?> json, String key) {
  final value = json[key];
  if (value is num) return value.toDouble();
  if (value is String) {
    final parsed = double.tryParse(value);
    if (parsed != null) return parsed;
  }
  throw FormatException('Expected number "$key"', value);
}

int _int(Map<String, Object?> json, String key) {
  final value = json[key];
  if (value is int) return value;
  if (value is num) return value.toInt();
  if (value is String) {
    final parsed = int.tryParse(value);
    if (parsed != null) return parsed;
  }
  throw FormatException('Expected integer "$key"', value);
}

DateTime? _optDate(Map<String, Object?> json, String key) {
  final value = json[key];
  return value is String ? DateTime.tryParse(value) : null;
}

/// Task statuses (`open`, `in_progress`, `completed`,
/// `completed_with_shortage`, `cancelled`).
abstract final class PickingTaskStatus {
  static const open = 'open';
  static const inProgress = 'in_progress';
  static const completed = 'completed';
  static const completedWithShortage = 'completed_with_shortage';
  static const cancelled = 'cancelled';

  static const List<String> active = [open, inProgress];

  static bool isActive(String status) => active.contains(status);
}

/// Reasons accepted by `POST …/lines/{lineId}/shortage`.
abstract final class ShortageReason {
  static const notFound = 'not_found';
  static const damaged = 'damaged';
  static const insufficient = 'insufficient';

  static const List<String> all = [notFound, damaged, insufficient];
}

/// PickingTaskSummaryResponse — one row of the work list.
class PickingTaskSummary {
  const PickingTaskSummary({
    required this.id,
    required this.type,
    required this.status,
    required this.orderId,
    required this.orderNumber,
    required this.warehouseId,
    required this.lineCount,
    required this.quantityToPick,
    required this.quantityPicked,
    required this.quantityShort,
    required this.createdAt,
    required this.version,
    this.assignedTo,
    this.assigneeName,
    this.startedAt,
    this.completedAt,
  });

  factory PickingTaskSummary.fromJson(Map<String, Object?> json) =>
      PickingTaskSummary(
        id: json.requireString('id'),
        type: json.requireString('type'),
        status: json.requireString('status'),
        orderId: json.requireString('orderId'),
        orderNumber: json.requireString('orderNumber'),
        warehouseId: json.requireString('warehouseId'),
        assignedTo: json.optString('assignedTo'),
        assigneeName: json.optString('assigneeName'),
        lineCount: _int(json, 'lineCount'),
        quantityToPick: _num(json, 'quantityToPick'),
        quantityPicked: _num(json, 'quantityPicked'),
        quantityShort: _num(json, 'quantityShort'),
        createdAt: DateTime.parse(json.requireString('createdAt')),
        startedAt: _optDate(json, 'startedAt'),
        completedAt: _optDate(json, 'completedAt'),
        version: _int(json, 'version'),
      );

  static const contract = ContractSchema('PickingTaskSummaryResponse', {
    'id': 'string',
    'type': 'string',
    'status': 'string',
    'orderId': 'string',
    'orderNumber': 'string',
    'warehouseId': 'string',
    'assignedTo': 'string?',
    'assigneeName': 'string?',
    'lineCount': 'integer',
    'quantityToPick': 'number',
    'quantityPicked': 'number',
    'quantityShort': 'number',
    'createdAt': 'string',
    'startedAt': 'string?',
    'completedAt': 'string?',
    'version': 'integer',
  });

  final String id;

  /// `pick` or `putaway`.
  final String type;
  final String status;
  final String orderId;
  final String orderNumber;
  final String warehouseId;
  final String? assignedTo;
  final String? assigneeName;
  final int lineCount;
  final double quantityToPick;
  final double quantityPicked;
  final double quantityShort;
  final DateTime createdAt;
  final DateTime? startedAt;
  final DateTime? completedAt;

  /// Row version; `W/"<version>"` is the ETag / If-Match value.
  final int version;

  bool get isActive => PickingTaskStatus.isActive(status);
  bool get isPool => assignedTo == null;

  /// Picked + short over requested, 0…1.
  double get progress => quantityToPick <= 0
      ? 0
      : ((quantityPicked + quantityShort) / quantityToPick).clamp(0, 1);

  PickingTaskSummary copyWith({
    String? status,
    int? version,
    double? quantityPicked,
    double? quantityShort,
  }) => PickingTaskSummary(
    id: id,
    type: type,
    status: status ?? this.status,
    orderId: orderId,
    orderNumber: orderNumber,
    warehouseId: warehouseId,
    assignedTo: assignedTo,
    assigneeName: assigneeName,
    lineCount: lineCount,
    quantityToPick: quantityToPick,
    quantityPicked: quantityPicked ?? this.quantityPicked,
    quantityShort: quantityShort ?? this.quantityShort,
    createdAt: createdAt,
    startedAt: startedAt,
    completedAt: completedAt,
    version: version ?? this.version,
  );
}

/// PickingTaskLineResponse — one line in shelf (pick) order.
class PickingTaskLine {
  const PickingTaskLine({
    required this.id,
    required this.sequence,
    required this.orderLineId,
    required this.variantId,
    required this.sku,
    required this.name,
    required this.unit,
    required this.quantityToPick,
    required this.quantityPicked,
    required this.quantityShort,
    required this.quantityRemaining,
    this.barcode,
    this.locationId,
    this.locationCode,
    this.locationBarcode,
    this.shortageReason,
  });

  factory PickingTaskLine.fromJson(Map<String, Object?> json) =>
      PickingTaskLine(
        id: json.requireString('id'),
        sequence: _int(json, 'sequence'),
        orderLineId: json.requireString('orderLineId'),
        variantId: json.requireString('variantId'),
        sku: json.requireString('sku'),
        name: json.requireString('name'),
        unit: json.requireString('unit'),
        barcode: json.optString('barcode'),
        locationId: json.optString('locationId'),
        locationCode: json.optString('locationCode'),
        locationBarcode: json.optString('locationBarcode'),
        quantityToPick: _num(json, 'quantityToPick'),
        quantityPicked: _num(json, 'quantityPicked'),
        quantityShort: _num(json, 'quantityShort'),
        quantityRemaining: _num(json, 'quantityRemaining'),
        shortageReason: json.optString('shortageReason'),
      );

  static const contract = ContractSchema('PickingTaskLineResponse', {
    'id': 'string',
    'sequence': 'integer',
    'orderLineId': 'string',
    'variantId': 'string',
    'sku': 'string',
    'name': 'string',
    'unit': 'string',
    'barcode': 'string?',
    'locationId': 'string?',
    'locationCode': 'string?',
    'locationBarcode': 'string?',
    'quantityToPick': 'number',
    'quantityPicked': 'number',
    'quantityShort': 'number',
    'quantityRemaining': 'number',
    'shortageReason': 'string?',
  });

  final String id;
  final int sequence;
  final String orderLineId;
  final String variantId;
  final String sku;
  final String name;
  final String unit;
  final String? barcode;

  /// Shelf; null in a warehouse without locations.
  final String? locationId;
  final String? locationCode;
  final String? locationBarcode;
  final double quantityToPick;
  final double quantityPicked;
  final double quantityShort;
  final double quantityRemaining;
  final String? shortageReason;

  bool get isOpen => quantityRemaining > 0;

  PickingTaskLine withProgress(PickLineProgress p) => PickingTaskLine(
    id: id,
    sequence: sequence,
    orderLineId: orderLineId,
    variantId: variantId,
    sku: sku,
    name: name,
    unit: unit,
    barcode: barcode,
    locationId: locationId,
    locationCode: locationCode,
    locationBarcode: locationBarcode,
    quantityToPick: p.quantityToPick,
    quantityPicked: p.quantityPicked,
    quantityShort: p.quantityShort,
    quantityRemaining: p.quantityRemaining,
    shortageReason: shortageReason,
  );
}

/// PickingTaskResponse — task + lines (+ the draft shipment once completed).
class PickingTask {
  const PickingTask({required this.task, required this.lines, this.shipmentId});

  factory PickingTask.fromJson(Map<String, Object?> json) => PickingTask(
    task: PickingTaskSummary.fromJson(
      json.optObject('task') ??
          (throw FormatException('Expected object "task"', json)),
    ),
    lines: json.objectList('lines').map(PickingTaskLine.fromJson).toList(),
    shipmentId: json.optString('shipmentId'),
  );

  static const contract = ContractSchema('PickingTaskResponse', {
    'task': 'object',
    'lines': 'array',
    'shipmentId': 'string?',
  });

  final PickingTaskSummary task;
  final List<PickingTaskLine> lines;
  final String? shipmentId;

  /// `If-Match` for the current version.
  String get etag => etagFor(task.version);

  /// Merges a scan's per-line progress (only changed lines come back).
  PickingTask applyScan(PickScanResult scan) {
    final byId = {for (final p in scan.lines) p.id: p};
    final next = [
      for (final l in lines)
        if (byId[l.id] case final progress?) l.withProgress(progress) else l,
    ];
    double sum(double Function(PickingTaskLine) f) =>
        next.fold(0, (a, l) => _round3(a + f(l)));
    return PickingTask(
      task: task.copyWith(
        status: scan.status,
        version: scan.version,
        quantityPicked: sum((l) => l.quantityPicked),
        quantityShort: sum((l) => l.quantityShort),
      ),
      lines: next,
      shipmentId: shipmentId,
    );
  }
}

double _round3(double v) => (v * 1000).roundToDouble() / 1000;

/// The weak ETag the API sends for a row version (`W/"12"`).
String etagFor(int version) => 'W/"$version"';

/// PickLineProgress — a line changed by a scan.
class PickLineProgress {
  const PickLineProgress({
    required this.id,
    required this.variantId,
    required this.sku,
    required this.quantityToPick,
    required this.quantityPicked,
    required this.quantityShort,
    required this.quantityRemaining,
    this.locationId,
  });

  factory PickLineProgress.fromJson(Map<String, Object?> json) =>
      PickLineProgress(
        id: json.requireString('id'),
        variantId: json.requireString('variantId'),
        sku: json.requireString('sku'),
        locationId: json.optString('locationId'),
        quantityToPick: _num(json, 'quantityToPick'),
        quantityPicked: _num(json, 'quantityPicked'),
        quantityShort: _num(json, 'quantityShort'),
        quantityRemaining: _num(json, 'quantityRemaining'),
      );

  static const contract = ContractSchema('PickLineProgress', {
    'id': 'string',
    'variantId': 'string',
    'sku': 'string',
    'locationId': 'string?',
    'quantityToPick': 'number',
    'quantityPicked': 'number',
    'quantityShort': 'number',
    'quantityRemaining': 'number',
  });

  final String id;
  final String variantId;
  final String sku;
  final String? locationId;
  final double quantityToPick;
  final double quantityPicked;
  final double quantityShort;
  final double quantityRemaining;
}

/// PickScanResponse.
class PickScanResult {
  const PickScanResult({
    required this.taskId,
    required this.status,
    required this.version,
    required this.lines,
    required this.openLineCount,
  });

  factory PickScanResult.fromJson(Map<String, Object?> json) => PickScanResult(
    taskId: json.requireString('taskId'),
    status: json.requireString('status'),
    version: _int(json, 'version'),
    lines: json.objectList('lines').map(PickLineProgress.fromJson).toList(),
    openLineCount: _int(json, 'openLineCount'),
  );

  static const contract = ContractSchema('PickScanResponse', {
    'taskId': 'string',
    'status': 'string',
    'version': 'integer',
    'lines': 'array',
    'openLineCount': 'integer',
  });

  final String taskId;
  final String status;
  final int version;
  final List<PickLineProgress> lines;
  final int openLineCount;
}

/// PickScanRequest body (`quantity` default 1 on the API).
class PickScanRequest {
  const PickScanRequest({
    required this.code,
    required this.quantity,
    this.location,
  });

  /// Request fields the app sends (checked against the contract too).
  static const contract = ContractSchema('PickScanRequest', {
    'code': 'string?',
    'location': 'string?',
    'quantity': 'number?',
    'lineId': 'string?',
  });

  final String code;
  final String? location;
  final double quantity;

  /// `lineId` stays null: the API matches the code to the task's lines (and
  /// the scanned shelf), exactly like the web pick screen.
  Map<String, Object?> toJson() => {
    'code': code,
    'location': location,
    'quantity': _wireNumber(quantity),
    'lineId': null,
  };
}

/// ReportShortageRequest body.
class ReportShortageRequest {
  const ReportShortageRequest({required this.reason, required this.quantity});

  static const contract = ContractSchema('ReportShortageRequest', {
    'quantity': 'number?',
    'reason': 'string?',
  });

  final String reason;
  final double quantity;

  Map<String, Object?> toJson() => {
    'quantity': _wireNumber(quantity),
    'reason': reason,
  };
}

/// Whole numbers go out as JSON integers (`2`, not `2.0`).
num _wireNumber(double value) =>
    value == value.truncateToDouble() ? value.toInt() : value;

/// FulfillmentPageInfo — keyset paging of `GET /picking-tasks`.
class FulfillmentPage {
  const FulfillmentPage({required this.hasMore, this.nextCursor});

  factory FulfillmentPage.fromJson(Map<String, Object?> json) =>
      FulfillmentPage(
        nextCursor: json.optString('nextCursor'),
        hasMore: json['hasMore'] == true,
      );

  static const contract = ContractSchema('FulfillmentPageInfo', {
    'nextCursor': 'string?',
    'hasMore': 'boolean',
  });

  final String? nextCursor;
  final bool hasMore;
}

/// One page of `GET /picking-tasks` or the `/mine` list.
class PickingTaskPage {
  const PickingTaskPage({
    required this.items,
    this.page = const FulfillmentPage(hasMore: false),
  });

  factory PickingTaskPage.fromJson(Map<String, Object?> json) =>
      PickingTaskPage(
        items: json
            .objectList('items')
            .map(PickingTaskSummary.fromJson)
            .toList(),
        page: switch (json.optObject('page')) {
          final Map<String, Object?> p => FulfillmentPage.fromJson(p),
          null => const FulfillmentPage(hasMore: false),
        },
      );

  static const pageContract = ContractSchema('PickingTaskPageResponse', {
    'items': 'array',
    'page': 'object',
  });

  static const mineContract = ContractSchema('MyPickingTasksResponse', {
    'items': 'array',
  });

  final List<PickingTaskSummary> items;
  final FulfillmentPage page;
}

/// The order behind a task, as far as the pick screen shows it
/// (OrderDetailResponse.order; prices are never read).
class PickOrderInfo {
  const PickOrderInfo({
    required this.number,
    required this.status,
    required this.hasShortage,
    this.customerName,
    this.warehouseName,
    this.requestedDeliveryDate,
    this.notes,
  });

  factory PickOrderInfo.fromJson(Map<String, Object?> json) {
    final order =
        json.optObject('order') ??
        (throw FormatException('Expected object "order"', json));
    return PickOrderInfo(
      number: order.requireString('number'),
      status: order.requireString('status'),
      customerName: order.optString('customerName'),
      warehouseName: order.optString('warehouseName'),
      requestedDeliveryDate: order.optString('requestedDeliveryDate'),
      notes: order.optString('notes'),
      hasShortage: order['hasShortage'] == true,
    );
  }

  static const detailContract = ContractSchema('OrderDetailResponse', {
    'order': 'object',
  });

  static const contract = ContractSchema('OrderResponse', {
    'number': 'string',
    'status': 'string',
    'customerName': 'string?',
    'warehouseName': 'string?',
    'requestedDeliveryDate': 'string?',
    'notes': 'string?',
    'hasShortage': 'boolean',
  });

  final String number;
  final String status;

  /// Null for roles without `customers.read` (warehouse staff).
  final String? customerName;
  final String? warehouseName;

  /// `yyyy-MM-dd`.
  final String? requestedDeliveryDate;
  final String? notes;
  final bool hasShortage;
}
