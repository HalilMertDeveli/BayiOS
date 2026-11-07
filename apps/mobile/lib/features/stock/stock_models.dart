import 'package:bayios_mobile/core/api/contract.dart';
import 'package:bayios_mobile/features/stock/quantity.dart';
import 'package:flutter/foundation.dart';

/// WarehouseResponse (`GET /warehouses`).
@immutable
class Warehouse {
  const Warehouse({
    required this.id,
    required this.code,
    required this.name,
    required this.usesLocations,
    required this.isDefault,
    required this.status,
  });

  factory Warehouse.fromJson(Map<String, Object?> json) => Warehouse(
    id: json.requireString('id'),
    code: json.requireString('code'),
    name: json.requireString('name'),
    usesLocations: json['usesLocations'] == true,
    isDefault: json['isDefault'] == true,
    status: json.requireString('status'),
  );

  static const contract = ContractSchema('WarehouseResponse', {
    'id': 'string',
    'code': 'string',
    'name': 'string',
    'usesLocations': 'boolean',
    'isDefault': 'boolean',
    'status': 'string',
  });

  static const listContract = ContractSchema('WarehouseListResponse', {
    'items': 'array',
  });

  final String id;
  final String code;
  final String name;

  /// Bins (raf/göz) are tracked: a receipt line needs a location.
  final bool usesLocations;
  final bool isDefault;
  final String status;

  bool get isActive => status == 'active';
}

/// LocationResponse — a bin (raf/göz) of a warehouse.
@immutable
class StorageLocation {
  const StorageLocation({
    required this.id,
    required this.warehouseId,
    required this.code,
    required this.isActive,
    this.barcode,
  });

  factory StorageLocation.fromJson(Map<String, Object?> json) =>
      StorageLocation(
        id: json.requireString('id'),
        warehouseId: json.requireString('warehouseId'),
        code: json.requireString('code'),
        barcode: json.optString('barcode'),
        isActive: json['isActive'] == true,
      );

  static const contract = ContractSchema('LocationResponse', {
    'id': 'string',
    'warehouseId': 'string',
    'code': 'string',
    'barcode': 'string?',
    'isActive': 'boolean',
  });

  static const listContract = ContractSchema('LocationListResponse', {
    'items': 'array',
  });

  final String id;
  final String warehouseId;
  final String code;
  final String? barcode;
  final bool isActive;

  /// A scanned or typed bin label matches the bin barcode or its code
  /// (case-insensitive: labels are often typed in lower case).
  bool matches(String scanned) {
    final value = scanned.trim().toUpperCase();
    if (value.isEmpty) return false;
    return code.toUpperCase() == value || barcode?.toUpperCase() == value;
  }
}

/// What the screens show of a variant: from VariantResponse (lookup/search)
/// or VariantSummaryResponse (batch).
@immutable
class VariantRef {
  const VariantRef({
    required this.id,
    required this.sku,
    this.name,
    this.productName,
    this.barcode,
    this.status = 'active',
  });

  factory VariantRef.fromVariant(Map<String, Object?> json) => VariantRef(
    id: json.requireString('id'),
    sku: json.requireString('sku'),
    name: json.optString('name'),
    barcode: json.optString('barcode'),
    status: json.requireString('status'),
  );

  factory VariantRef.fromSummary(Map<String, Object?> json) => VariantRef(
    id: json.requireString('id'),
    sku: json.requireString('sku'),
    name: json.optString('name'),
    productName: json.optString('productName'),
    barcode: json.optString('barcode'),
    status: json.requireString('status'),
  );

  static const variantContract = ContractSchema('VariantResponse', {
    'id': 'string',
    'sku': 'string',
    'name': 'string?',
    'barcode': 'string?',
    'status': 'string',
  });

  static const variantPageContract = ContractSchema(
    'PagedResponseOfVariantResponse',
    {'items': 'array'},
  );

  static const summaryContract = ContractSchema('VariantSummaryResponse', {
    'id': 'string',
    'sku': 'string',
    'name': 'string?',
    'productName': 'string',
    'barcode': 'string?',
    'status': 'string',
  });

  static const summaryListContract = ContractSchema(
    'ListResponseOfVariantSummaryResponse',
    {'items': 'array'},
  );

  final String id;
  final String sku;
  final String? name;
  final String? productName;
  final String? barcode;
  final String status;

  /// "Ürün adı — varyant adı", whichever is known.
  String? get title {
    final parts = [
      if (productName != null && productName!.isNotEmpty) productName!,
      if (name != null && name!.isNotEmpty && name != productName) name!,
    ];
    return parts.isEmpty ? null : parts.join(' — ');
  }

  bool get isActive => status == 'active';
}

/// BalanceResponse — on hand / reserved / available of one variant in one
/// warehouse (and bin), plus the effective minimum stock (#137).
@immutable
class StockBalance {
  const StockBalance({
    required this.id,
    required this.variantId,
    required this.warehouseId,
    required this.onHand,
    required this.reserved,
    required this.available,
    required this.belowMinimum,
    this.locationId,
    this.minQuantity,
  });

  factory StockBalance.fromJson(Map<String, Object?> json) => StockBalance(
    id: json.requireString('id'),
    variantId: json.requireString('variantId'),
    warehouseId: json.requireString('warehouseId'),
    locationId: json.optString('locationId'),
    onHand: readQuantity(json['onHand']),
    reserved: readQuantity(json['reserved']),
    available: readQuantity(json['available']),
    minQuantity: readOptionalQuantity(json['minQuantity']),
    belowMinimum: json['belowMinimum'] == true,
  );

  static const contract = ContractSchema('BalanceResponse', {
    'id': 'string',
    'variantId': 'string',
    'warehouseId': 'string',
    'locationId': 'string?',
    'onHand': 'number',
    'reserved': 'number',
    'available': 'number',
    'minQuantity': 'number?',
    'belowMinimum': 'boolean',
  });

  static const pageContract = ContractSchema('BalancePageResponse', {
    'items': 'array',
    'page': 'object',
  });

  final String id;
  final String variantId;
  final String warehouseId;
  final String? locationId;
  final num onHand;
  final num reserved;
  final num available;

  /// Effective minimum of the variant in this warehouse; null = none set.
  final num? minQuantity;

  /// Available stock of the variant over the warehouse ≤ [minQuantity].
  final bool belowMinimum;
}

/// InventoryPageInfo (keyset paging).
@immutable
class InventoryPage {
  const InventoryPage({required this.hasMore, this.nextCursor});

  factory InventoryPage.fromJson(Map<String, Object?> json) => InventoryPage(
    nextCursor: json.optString('nextCursor'),
    hasMore: json['hasMore'] == true,
  );

  static const contract = ContractSchema('InventoryPageInfo', {
    'nextCursor': 'string?',
    'hasMore': 'boolean',
  });

  final String? nextCursor;
  final bool hasMore;
}

@immutable
class BalancePage {
  const BalancePage(this.items, this.page);

  factory BalancePage.fromJson(Map<String, Object?> json) => BalancePage(
    json.objectList('items').map(StockBalance.fromJson).toList(),
    InventoryPage.fromJson(json.optObject('page') ?? const {}),
  );

  final List<StockBalance> items;
  final InventoryPage page;
}

/// MovementResult — one ledger row (`inventory_movements`).
@immutable
class StockMovement {
  const StockMovement({
    required this.id,
    required this.variantId,
    required this.warehouseId,
    required this.type,
    required this.quantity,
    required this.balanceAfter,
    this.locationId,
  });

  factory StockMovement.fromJson(Map<String, Object?> json) => StockMovement(
    id: json.requireString('id'),
    variantId: json.requireString('variantId'),
    warehouseId: json.requireString('warehouseId'),
    locationId: json.optString('locationId'),
    type: json.requireString('type'),
    quantity: readQuantity(json['quantity']),
    balanceAfter: readQuantity(json['balanceAfter']),
  );

  static const contract = ContractSchema('MovementResult', {
    'id': 'string',
    'variantId': 'string',
    'warehouseId': 'string',
    'locationId': 'string?',
    'type': 'string',
    'quantity': 'number',
    'balanceAfter': 'number',
  });

  final String id;
  final String variantId;
  final String warehouseId;
  final String? locationId;
  final String type;
  final num quantity;

  /// On-hand of the (variant, warehouse, bin) right after this movement.
  final num balanceAfter;
}

/// ReceiptResult — the movements one goods receipt wrote.
@immutable
class ReceiptResult {
  const ReceiptResult({
    required this.receiptId,
    required this.warehouseId,
    required this.movements,
  });

  factory ReceiptResult.fromJson(Map<String, Object?> json) => ReceiptResult(
    receiptId: json.requireString('receiptId'),
    warehouseId: json.requireString('warehouseId'),
    movements: json
        .objectList('movements')
        .map(StockMovement.fromJson)
        .toList(),
  );

  static const contract = ContractSchema('ReceiptResult', {
    'receiptId': 'string',
    'warehouseId': 'string',
    'movements': 'array',
  });

  final String receiptId;
  final String warehouseId;
  final List<StockMovement> movements;
}

/// Adjustment types the API accepts (`CreateAdjustmentRequest.type`), in
/// the order the form offers them. The direction comes from the type.
enum AdjustmentType {
  adjustmentIn('adjustment_in', inbound: true),
  adjustmentOut('adjustment_out', inbound: false),
  damageOut('damage_out', inbound: false),
  returnIn('return_in', inbound: true);

  AdjustmentType(this.wire, {required this.inbound});

  final String wire;
  final bool inbound;
}
