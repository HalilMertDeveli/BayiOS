import 'package:bayios_mobile/core/api/idempotency.dart';
import 'package:bayios_mobile/features/stock/quantity.dart';
import 'package:bayios_mobile/features/stock/stock_api.dart';
import 'package:bayios_mobile/features/stock/stock_models.dart';
import 'package:bayios_mobile/features/warehouse/document_scan_controller.dart';
import 'package:bayios_mobile/features/warehouse/warehouse_api.dart';
import 'package:bayios_mobile/features/warehouse/warehouse_models.dart';

/// Teslim al (AF-07): scan the shelf, set the quantity, scan the product —
/// each product scan is one `POST /stock-transfers/{id}/receive` with the
/// scanned `code` and `locationCode` (partial receipts allowed). The API
/// answers with the updated transfer; over-receipt, unknown code, an item
/// that is not on the transfer and an unknown shelf are refused there.
class TransferReceiveController extends DocumentScanController<TransferDetail> {
  /// Named arguments: `api:`, `stock:`, `transferId:`, `alerts:`, `wait:`,
  /// `onWrite:`.
  TransferReceiveController({
    required this._api,
    required this._stock,
    required this.transferId,
    required super.alerts,
    required super.wait,
    super.onWrite,
  });

  final WarehouseApi _api;
  final StockApi _stock;
  final String transferId;

  TransferSummary? get transfer => document?.transfer;

  @override
  bool get writable => document?.transfer.receivable ?? false;

  @override
  Future<TransferDetail> fetch() => _api.transfer(transferId);

  @override
  Future<Warehouse?> fetchWarehouse(TransferDetail document) async {
    final all = await _stock.warehouses();
    return all
        .where((w) => w.id == document.transfer.destinationWarehouseId)
        .firstOrNull;
  }

  @override
  Future<TransferDetail> post(Map<String, Object?> body, IdempotencyKey key) =>
      _api.receive(transferId, body, key);

  @override
  Map<String, Object?> body(String code, num quantity, String? shelf) => {
    'lines': [
      {'code': code, 'quantity': quantity, 'locationCode': shelf},
    ],
  };

  @override
  List<ChangedLine> changes(TransferDetail before, TransferDetail after) {
    final old = {for (final l in before.lines) l.id: l.receivedQuantity};
    return [
      for (final line in after.lines)
        if (line.receivedQuantity != (old[line.id] ?? 0))
          ChangedLine(
            sku: line.sku,
            quantity: addQuantities(
              line.receivedQuantity,
              -(old[line.id] ?? 0),
            ),
            total: line.receivedQuantity,
            shelf: shelf,
          ),
    ];
  }
}
