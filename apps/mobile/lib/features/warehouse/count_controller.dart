import 'package:bayios_mobile/core/api/api_exception.dart';
import 'package:bayios_mobile/core/api/idempotency.dart';
import 'package:bayios_mobile/features/stock/quantity.dart';
import 'package:bayios_mobile/features/stock/stock_api.dart';
import 'package:bayios_mobile/features/stock/stock_models.dart';
import 'package:bayios_mobile/features/warehouse/document_scan_controller.dart';
import 'package:bayios_mobile/features/warehouse/warehouse_api.dart';
import 'package:bayios_mobile/features/warehouse/warehouse_errors.dart';
import 'package:bayios_mobile/features/warehouse/warehouse_models.dart';
import 'package:flutter/foundation.dart';

/// "Sayımı bitir" state.
sealed class SubmitState {
  const SubmitState();
}

final class SubmitIdle extends SubmitState {
  const SubmitIdle();
}

final class SubmitSending extends SubmitState {
  const SubmitSending();
}

/// [pending]: unknown outcome — "Tekrar gönder" resends the same key.
final class SubmitFailed extends SubmitState {
  const SubmitFailed(this.error, {required this.pending});

  final Object error;
  final bool pending;
}

final class SubmitDone extends SubmitState {
  const SubmitDone();
}

/// Sayım (AF-06): shelf → product → quantity, per scan one
/// `POST /stock-counts/{id}/entries` with `code` + `locationCode` and the
/// chosen mode (`add`: += per scan, `set`: absolute). "Sayımı bitir" =
/// `POST …/submit` with If-Match; applying stays on the web (manager).
class CountController extends DocumentScanController<CountDetail> {
  /// Named arguments: `api:`, `stock:`, `countId:`, `alerts:`, `wait:`,
  /// `onWrite:`.
  CountController({
    required this._api,
    required this._stock,
    required this.countId,
    required super.alerts,
    required super.wait,
    super.onWrite,
  });

  final WarehouseApi _api;
  final StockApi _stock;
  final String countId;

  CountMode _mode = CountMode.add;
  SubmitState _submit = const SubmitIdle();
  IdempotentAction<CountDetail>? _submitAction;

  CountMode get mode => _mode;
  SubmitState get submitState => _submit;

  CountSummary? get count => document?.count;

  /// System quantities may be shown (not a blind count seen by a counter).
  bool get showsSystemQuantities => !(count?.quantitiesHidden ?? true);

  @override
  bool get writable =>
      (document?.count.isOpen ?? false) && _submit is! SubmitSending;

  void setMode(CountMode mode) {
    if (mode == _mode) return;
    _mode = mode;
    // "1" is a good default per scan; an absolute count is typed (the
    // current text stays).
    setQuantityText(mode == CountMode.add ? '1' : this.quantityText);
  }

  @override
  QuantityParse parse(String text) =>
      _mode == CountMode.set ? parseCountQuantity(text) : parseQuantity(text);

  @override
  Future<CountDetail> fetch() => _api.count(countId);

  @override
  Future<Warehouse?> fetchWarehouse(CountDetail document) async {
    final all = await _stock.warehouses();
    return all.where((w) => w.id == document.count.warehouseId).firstOrNull;
  }

  @override
  Future<CountDetail> post(Map<String, Object?> body, IdempotencyKey key) =>
      _api.enterCount(countId, body, key);

  @override
  Map<String, Object?> body(String code, num quantity, String? shelf) => {
    'entries': [
      {
        'code': code,
        'locationCode': shelf,
        'quantity': quantity,
        'mode': _mode.wire,
      },
    ],
  };

  @override
  List<ChangedLine> changes(CountDetail before, CountDetail after) {
    final old = {
      for (final l in before.lines) l.id: (l.countedQuantity, l.countedAt),
    };
    return [
      for (final line in after.lines)
        if (line.countedQuantity != null &&
            (old[line.id] == null ||
                old[line.id]!.$1 != line.countedQuantity ||
                old[line.id]!.$2 != line.countedAt))
          ChangedLine(
            sku: line.sku,
            shelf: line.locationCode,
            quantity: addQuantities(
              line.countedQuantity!,
              -(old[line.id]?.$1 ?? 0),
            ),
            total: line.countedQuantity!,
          ),
    ];
  }

  /// "Sayımı bitir" (and "Tekrar gönder" after an unknown outcome, same
  /// key and the same If-Match version).
  Future<void> submit() async {
    final current = document;
    if (current == null || _submit is SubmitSending) return;
    final retry = _submit is SubmitFailed && (_submit as SubmitFailed).pending;
    if (!retry || _submitAction == null) {
      final version = current.count.version;
      _submitAction = IdempotentAction(
        (key) => _api.submitCount(countId, version, key),
      );
    }
    final action = _submitAction!;
    _submit = const SubmitSending();
    notifyListeners();
    try {
      final result = await withDocumentBusyRetry(action.run, wait: wait);
      if (disposed) return;
      _submitAction = null;
      _submit = const SubmitDone();
      replaceDocument(result);
      onWrite?.call();
    } on Object catch (error) {
      if (disposed) return;
      final pending = isResendable(error);
      if (!pending) _submitAction = null;
      _submit = SubmitFailed(error, pending: pending);
      notifyListeners();
      // 412 / no longer open: show the current state (the message says why).
      if (isStaleDocument(error)) await reload();
    }
  }

  void dismissSubmitError() {
    if (_submit is! SubmitFailed) return;
    _submit = const SubmitIdle();
    _submitAction = null;
    notifyListeners();
  }

  /// Problem code of the last submit failure (tests).
  @visibleForTesting
  String? get submitErrorCode => switch (_submit) {
    SubmitFailed(:final ApiException error) => error.code,
    _ => null,
  };
}
