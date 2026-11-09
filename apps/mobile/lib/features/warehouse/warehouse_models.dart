import 'package:bayios_mobile/core/api/contract.dart';
import 'package:bayios_mobile/features/stock/quantity.dart';
import 'package:bayios_mobile/features/stock/stock_models.dart';
import 'package:flutter/foundation.dart';

/// Hand-written models of the stock-document endpoints (#168): two-stage
/// transfers (`/stock-transfers`), stock counts (`/stock-counts`) and the
/// instant shelf-to-shelf move (`/inventory/relocations`). Contract-checked
/// by test/contract/contract_test.dart.

/// Transfer statuses (`StockTransferSummaryResponse.status`).
abstract final class TransferStatus {
  static const inTransit = 'in_transit';
  static const partiallyReceived = 'partially_received';
  static const received = 'received';

  /// What a receiver can still take in.
  static const List<String> receivable = [inTransit, partiallyReceived];
}

/// Count statuses (`StockCountSummaryResponse.status`).
abstract final class CountStatus {
  static const open = 'open';
  static const submitted = 'submitted';
}

/// `FulfillmentPageInfo` — keyset paging of the document lists.
@immutable
class DocumentPage {
  const DocumentPage({required this.hasMore, this.nextCursor});

  factory DocumentPage.fromJson(Map<String, Object?> json) => DocumentPage(
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

int _readInt(Object? value) => switch (value) {
  final int n => n,
  final num n => n.toInt(),
  final String s => int.tryParse(s) ?? 0,
  _ => 0,
};

/// StockTransferSummaryResponse.
@immutable
class TransferSummary {
  const TransferSummary({
    required this.id,
    required this.number,
    required this.sourceWarehouseId,
    required this.destinationWarehouseId,
    required this.status,
    required this.lineCount,
    required this.quantity,
    required this.receivedQuantity,
    required this.inTransitQuantity,
    required this.isOverdue,
    required this.version,
    this.sourceWarehouseName,
    this.destinationWarehouseName,
    this.note,
    this.dispatchedAt,
  });

  factory TransferSummary.fromJson(Map<String, Object?> json) =>
      TransferSummary(
        id: json.requireString('id'),
        number: json.requireString('number'),
        sourceWarehouseId: json.requireString('sourceWarehouseId'),
        sourceWarehouseName: json.optString('sourceWarehouseName'),
        destinationWarehouseId: json.requireString('destinationWarehouseId'),
        destinationWarehouseName: json.optString('destinationWarehouseName'),
        status: json.requireString('status'),
        note: json.optString('note'),
        lineCount: _readInt(json['lineCount']),
        quantity: readQuantity(json['quantity']),
        receivedQuantity: readQuantity(json['receivedQuantity']),
        inTransitQuantity: readQuantity(json['inTransitQuantity']),
        isOverdue: json['isOverdue'] == true,
        dispatchedAt: DateTime.tryParse(json.optString('dispatchedAt') ?? ''),
        version: _readInt(json['version']),
      );

  static const contract = ContractSchema('StockTransferSummaryResponse', {
    'id': 'string',
    'number': 'string',
    'sourceWarehouseId': 'string',
    'sourceWarehouseName': 'string?',
    'destinationWarehouseId': 'string',
    'destinationWarehouseName': 'string?',
    'status': 'string',
    'note': 'string?',
    'lineCount': 'integer',
    'quantity': 'number',
    'receivedQuantity': 'number',
    'inTransitQuantity': 'number',
    'isOverdue': 'boolean',
    'dispatchedAt': 'string?',
    'version': 'integer',
  });

  static const pageContract = ContractSchema('StockTransferPageResponse', {
    'items': 'array',
    'page': 'object',
  });

  final String id;

  /// `TRF-…`, what the label on the pallet shows.
  final String number;
  final String sourceWarehouseId;
  final String? sourceWarehouseName;
  final String destinationWarehouseId;
  final String? destinationWarehouseName;
  final String status;
  final String? note;
  final int lineCount;

  /// Sent in total.
  final num quantity;
  final num receivedQuantity;

  /// Still on the road (what can be received).
  final num inTransitQuantity;

  /// In transit for more than 7 days.
  final bool isOverdue;
  final DateTime? dispatchedAt;

  /// For `If-Match: W/"<version>"`.
  final int version;

  bool get receivable => TransferStatus.receivable.contains(status);
}

@immutable
class TransferPage {
  const TransferPage(this.items, this.page);

  factory TransferPage.fromJson(Map<String, Object?> json) => TransferPage(
    json.objectList('items').map(TransferSummary.fromJson).toList(),
    DocumentPage.fromJson(json.optObject('page') ?? const {}),
  );

  final List<TransferSummary> items;
  final DocumentPage page;
}

/// StockTransferLineResponse.
@immutable
class TransferLine {
  const TransferLine({
    required this.id,
    required this.lineNo,
    required this.variantId,
    required this.sku,
    required this.quantity,
    required this.receivedQuantity,
    required this.inTransitQuantity,
    this.destinationLocationId,
    this.destinationLocationCode,
  });

  factory TransferLine.fromJson(Map<String, Object?> json) => TransferLine(
    id: json.requireString('id'),
    lineNo: _readInt(json['lineNo']),
    variantId: json.requireString('variantId'),
    sku: json.requireString('sku'),
    destinationLocationId: json.optString('destinationLocationId'),
    destinationLocationCode: json.optString('destinationLocationCode'),
    quantity: readQuantity(json['quantity']),
    receivedQuantity: readQuantity(json['receivedQuantity']),
    inTransitQuantity: readQuantity(json['inTransitQuantity']),
  );

  static const contract = ContractSchema('StockTransferLineResponse', {
    'id': 'string',
    'lineNo': 'integer',
    'variantId': 'string',
    'sku': 'string',
    'destinationLocationId': 'string?',
    'destinationLocationCode': 'string?',
    'quantity': 'number',
    'receivedQuantity': 'number',
    'inTransitQuantity': 'number',
  });

  final String id;
  final int lineNo;
  final String variantId;
  final String sku;

  /// Planned destination shelf (the receiver may choose another).
  final String? destinationLocationId;
  final String? destinationLocationCode;
  final num quantity;
  final num receivedQuantity;
  final num inTransitQuantity;

  bool get done => inTransitQuantity <= 0;
}

/// StockTransferResponse — summary plus lines (in line order).
@immutable
class TransferDetail {
  const TransferDetail({required this.transfer, required this.lines});

  factory TransferDetail.fromJson(Map<String, Object?> json) => TransferDetail(
    transfer: TransferSummary.fromJson(
      json.optObject('transfer') ?? (throw const FormatException('transfer')),
    ),
    lines: json.objectList('lines').map(TransferLine.fromJson).toList(),
  );

  /// StockTransferActionResponse (`POST …/receive`): the updated transfer.
  factory TransferDetail.fromAction(Map<String, Object?> json) =>
      TransferDetail.fromJson(
        json.optObject('transfer') ?? (throw const FormatException('action')),
      );

  static const contract = ContractSchema('StockTransferResponse', {
    'transfer': 'object',
    'lines': 'array',
  });

  static const actionContract = ContractSchema('StockTransferActionResponse', {
    'transfer': 'object',
    'movementIds': 'array',
  });

  final TransferSummary transfer;
  final List<TransferLine> lines;

  /// Open lines first, then line order.
  List<TransferLine> get sortedLines => [
    ...lines.where((l) => !l.done),
    ...lines.where((l) => l.done),
  ];
}

/// StockCountSummaryResponse.
@immutable
class CountSummary {
  const CountSummary({
    required this.id,
    required this.number,
    required this.warehouseId,
    required this.scope,
    required this.isBlind,
    required this.quantitiesHidden,
    required this.status,
    required this.lineCount,
    required this.countedLineCount,
    required this.version,
    this.warehouseName,
    this.note,
  });

  factory CountSummary.fromJson(Map<String, Object?> json) => CountSummary(
    id: json.requireString('id'),
    number: json.requireString('number'),
    warehouseId: json.requireString('warehouseId'),
    warehouseName: json.optString('warehouseName'),
    scope: json.requireString('scope'),
    isBlind: json['isBlind'] == true,
    quantitiesHidden: json['quantitiesHidden'] == true,
    status: json.requireString('status'),
    note: json.optString('note'),
    lineCount: _readInt(json['lineCount']),
    countedLineCount: _readInt(json['countedLineCount']),
    version: _readInt(json['version']),
  );

  static const contract = ContractSchema('StockCountSummaryResponse', {
    'id': 'string',
    'number': 'string',
    'warehouseId': 'string',
    'warehouseName': 'string?',
    'scope': 'string',
    'isBlind': 'boolean',
    'quantitiesHidden': 'boolean',
    'status': 'string',
    'note': 'string?',
    'lineCount': 'integer',
    'countedLineCount': 'integer',
    'version': 'integer',
  });

  static const pageContract = ContractSchema('StockCountPageResponse', {
    'items': 'array',
    'page': 'object',
  });

  final String id;
  final String number;
  final String warehouseId;
  final String? warehouseName;

  /// `full`, `locations` or `variants`.
  final String scope;
  final bool isBlind;

  /// Blind count seen without `inventory.count.manage`: system quantities
  /// are null and never shown.
  final bool quantitiesHidden;
  final String status;
  final String? note;
  final int lineCount;
  final int countedLineCount;
  final int version;

  bool get isOpen => status == CountStatus.open;
}

@immutable
class CountPage {
  const CountPage(this.items, this.page);

  factory CountPage.fromJson(Map<String, Object?> json) => CountPage(
    json.objectList('items').map(CountSummary.fromJson).toList(),
    DocumentPage.fromJson(json.optObject('page') ?? const {}),
  );

  final List<CountSummary> items;
  final DocumentPage page;
}

/// StockCountLineResponse.
@immutable
class CountLine {
  const CountLine({
    required this.id,
    required this.variantId,
    required this.sku,
    required this.status,
    this.locationId,
    this.locationCode,
    this.expectedQuantity,
    this.countedQuantity,
    this.difference,
    this.countedAt,
  });

  factory CountLine.fromJson(Map<String, Object?> json) => CountLine(
    id: json.requireString('id'),
    variantId: json.requireString('variantId'),
    sku: json.requireString('sku'),
    locationId: json.optString('locationId'),
    locationCode: json.optString('locationCode'),
    expectedQuantity: readOptionalQuantity(json['expectedQuantity']),
    countedQuantity: readOptionalQuantity(json['countedQuantity']),
    difference: readOptionalQuantity(json['difference']),
    countedAt: DateTime.tryParse(json.optString('countedAt') ?? ''),
    status: json.requireString('status'),
  );

  static const contract = ContractSchema('StockCountLineResponse', {
    'id': 'string',
    'variantId': 'string',
    'sku': 'string',
    'locationId': 'string?',
    'locationCode': 'string?',
    'expectedQuantity': 'number?',
    'countedQuantity': 'number?',
    'difference': 'number?',
    'countedAt': 'string?',
    'status': 'string',
  });

  final String id;
  final String variantId;
  final String sku;
  final String? locationId;
  final String? locationCode;

  /// System quantity at the snapshot; null when hidden (blind count).
  final num? expectedQuantity;
  final num? countedQuantity;

  /// counted − expected; null when hidden or not counted.
  final num? difference;
  final DateTime? countedAt;
  final String status;

  bool get counted => countedQuantity != null;
}

/// StockCountResponse — summary plus lines (by shelf code, then SKU).
@immutable
class CountDetail {
  const CountDetail({required this.count, required this.lines});

  factory CountDetail.fromJson(Map<String, Object?> json) => CountDetail(
    count: CountSummary.fromJson(
      json.optObject('count') ?? (throw const FormatException('count')),
    ),
    lines: json.objectList('lines').map(CountLine.fromJson).toList(),
  );

  static const contract = ContractSchema('StockCountResponse', {
    'count': 'object',
    'lines': 'array',
  });

  final CountSummary count;
  final List<CountLine> lines;

  /// Counted lines, most recent first (what the counter entered).
  List<CountLine> get countedLines {
    final counted = lines.where((l) => l.counted).toList()
      ..sort((a, b) {
        final at = a.countedAt;
        final bt = b.countedAt;
        if (at == null || bt == null) return 0;
        return bt.compareTo(at);
      });
    return counted;
  }
}

/// Count entry modes (`StockCountEntryRequest.mode`).
enum CountMode {
  /// Absolute: the counted quantity of the line becomes the entered one.
  set('set'),

  /// Per scan: the entered quantity is added to the line.
  add('add');

  CountMode(this.wire);

  final String wire;
}

/// TransferResult of `POST /inventory/relocations` (one `transfer_out` and
/// one `transfer_in` movement per line).
@immutable
class RelocationResult {
  const RelocationResult({
    required this.transferId,
    required this.movements,
    this.reservationMoves = 0,
  });

  factory RelocationResult.fromJson(Map<String, Object?> json) =>
      RelocationResult(
        transferId: json.requireString('transferId'),
        movements: json
            .objectList('movements')
            .map(StockMovement.fromJson)
            .toList(),
        reservationMoves: json.objectList('reservationMoves').length,
      );

  static const contract = ContractSchema('TransferResult', {
    'transferId': 'string',
    'movements': 'array',
    'reservationMoves': 'array?',
  });

  final String transferId;
  final List<StockMovement> movements;

  /// Order reservations carried to the target shelf (`includeReserved`).
  final int reservationMoves;
}

/// Request bodies the app sends, field by field (contract-checked: a renamed
/// or removed request field fails the contract test). All optional on the
/// wire; the API validates combinations.
abstract final class WarehouseRequests {
  static const receive = ContractSchema('ReceiveStockTransferRequest', {
    'lines': 'array?',
  });
  static const receiveLine = ContractSchema('ReceiveStockTransferLineRequest', {
    'code': 'string?',
    'quantity': 'number?',
    'locationCode': 'string?',
  });
  static const countEntries = ContractSchema('StockCountEntriesRequest', {
    'entries': 'array?',
  });
  static const countEntry = ContractSchema('StockCountEntryRequest', {
    'code': 'string?',
    'locationCode': 'string?',
    'quantity': 'number?',
    'mode': 'string?',
  });
  static const relocation = ContractSchema('CreateRelocationRequest', {
    'warehouseId': 'string?',
    'reference': 'string?',
    'lines': 'array?',
  });
  static const relocationLine = ContractSchema('RelocationLineRequest', {
    'variantId': 'string?',
    'fromLocationId': 'string?',
    'toLocationId': 'string?',
    'quantity': 'number?',
    'includeReserved': 'boolean?',
  });

  /// One `shortages` entry of a 409 `inventory.insufficient_available`
  /// (read by `shortageAvailable`).
  static const shortage = ContractSchema('StockShortage', {
    'available': 'number?',
  });

  static const List<ContractSchema> all = [
    shortage,
    receive,
    receiveLine,
    countEntries,
    countEntry,
    relocation,
    relocationLine,
  ];
}
