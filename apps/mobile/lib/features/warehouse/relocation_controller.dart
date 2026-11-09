import 'dart:async';

import 'package:bayios_mobile/core/api/api_exception.dart';
import 'package:bayios_mobile/core/api/idempotency.dart';
import 'package:bayios_mobile/features/picking/scan_alerts.dart';
import 'package:bayios_mobile/features/stock/quantity.dart';
import 'package:bayios_mobile/features/stock/stock_api.dart';
import 'package:bayios_mobile/features/stock/stock_models.dart';
import 'package:bayios_mobile/features/warehouse/warehouse_api.dart';
import 'package:bayios_mobile/features/warehouse/warehouse_errors.dart';
import 'package:bayios_mobile/features/warehouse/warehouse_models.dart';
import 'package:flutter/foundation.dart';

/// The step the move is waiting for.
enum MoveStep { source, product, target, ready }

/// Why a scan was not taken.
sealed class MoveScanProblem {
  const MoveScanProblem();
}

final class ShelfNotFound extends MoveScanProblem {
  const ShelfNotFound(this.code);

  final String code;
}

final class SameShelf extends MoveScanProblem {
  const SameShelf();
}

final class ProductNotFound extends MoveScanProblem {
  const ProductNotFound(this.code);

  final String code;
}

final class ProductInactive extends MoveScanProblem {
  const ProductInactive(this.sku);

  final String sku;
}

final class LookupFailed extends MoveScanProblem {
  const LookupFailed(this.error);

  final Object error;
}

sealed class MoveSubmission {
  const MoveSubmission();
}

final class MoveIdle extends MoveSubmission {
  const MoveIdle();
}

final class MoveSending extends MoveSubmission {
  const MoveSending({this.retryingBusy = false});

  final bool retryingBusy;
}

/// [pending]: unknown outcome; "Tekrar gönder" sends the same key and body
/// (the scan controls stay locked meanwhile).
final class MoveFailed extends MoveSubmission {
  const MoveFailed(this.error, {required this.pending});

  final Object error;
  final bool pending;
}

final class MoveDone extends MoveSubmission {
  const MoveDone({
    required this.sku,
    required this.quantity,
    required this.from,
    required this.to,
    required this.result,
  });

  final String sku;
  final num quantity;
  final String from;
  final String to;
  final RelocationResult result;
}

/// Raf taşıma (#168): source shelf → product → quantity → target shelf in
/// one located warehouse, posted as one `POST /inventory/relocations`
/// (ledger transfer_out + transfer_in in one transaction; only available
/// stock moves, never negative). Shelves are matched on the device against
/// the warehouse's shelves (code or label barcode), the product by barcode
/// then SKU.
class RelocationController extends ChangeNotifier {
  /// Named arguments: `api:`, `stock:`, `alerts:`, `wait:`.
  RelocationController({
    required this._api,
    required this._stock,
    required this._alerts,
    required this._wait,
  });

  final WarehouseApi _api;
  final StockApi _stock;
  final ScanAlerts _alerts;
  final Future<void> Function(Duration) _wait;

  bool _disposed = false;
  bool _loading = true;
  Object? _loadError;
  List<Warehouse> _warehouses = const [];
  Warehouse? _warehouse;
  List<StorageLocation> _shelves = const [];
  bool _shelvesLoading = false;
  Object? _shelvesError;

  StorageLocation? _source;
  VariantRef? _variant;
  StorageLocation? _target;
  StockBalance? _balance;
  bool _balanceLoading = false;
  bool _lookingUp = false;
  int _lookup = 0;
  MoveScanProblem? _problem;
  String _quantityText = '1';
  QuantityError? _quantityError;
  bool _overAvailable = false;
  bool _includeReserved = false;
  MoveSubmission _submission = const MoveIdle();
  IdempotentAction<RelocationResult>? _action;

  /// SKU, quantity and shelf codes of the move being sent.
  late (String, num, String, String) _done;

  bool get loading => _loading;
  Object? get loadError => _loadError;

  /// Active warehouses that track shelves (moves need two shelves).
  List<Warehouse> get warehouses => _warehouses;
  Warehouse? get warehouse => _warehouse;
  List<StorageLocation> get shelves => _shelves;
  bool get shelvesLoading => _shelvesLoading;
  Object? get shelvesError => _shelvesError;
  StorageLocation? get source => _source;
  VariantRef? get variant => _variant;
  StorageLocation? get target => _target;

  /// Balance of the product on the source shelf (null = none / unknown).
  StockBalance? get balance => _balance;
  bool get balanceLoading => _balanceLoading;
  bool get lookingUp => _lookingUp;
  MoveScanProblem? get problem => _problem;
  String get quantityText => _quantityText;
  QuantityError? get quantityError => _quantityError;

  /// The quantity is more than the shelf's movable stock ([movable]).
  bool get overAvailable => _overAvailable;
  MoveSubmission get submission => _submission;

  /// `includeReserved` (#98): reserved stock still on the shelf moves too,
  /// together with its order reservations and open pick lines.
  bool get includeReserved => _includeReserved;

  /// The shelf has reserved stock: moving it is a choice.
  bool get hasReserved => (_balance?.reserved ?? 0) > 0;

  /// Upper bound on the device: available, or on hand with reserved stock
  /// included (the API excludes picked / packed goods and says so in
  /// `shortages`). Null when the balance is unknown.
  num? get movable => _includeReserved ? _balance?.onHand : _balance?.available;

  void setIncludeReserved({required bool value}) {
    if (locked || value == _includeReserved) return;
    _includeReserved = value;
    _overAvailable = false;
    _notify();
  }

  /// Scans and edits are locked while sending or after an unknown outcome
  /// (a resend must carry exactly the same request).
  bool get locked => switch (_submission) {
    MoveSending() => true,
    MoveFailed(:final pending) => pending,
    _ => false,
  };

  MoveStep get step {
    if (_source == null) return MoveStep.source;
    if (_variant == null) return MoveStep.product;
    if (_target == null) return MoveStep.target;
    return MoveStep.ready;
  }

  Future<void> load() async {
    _loading = true;
    _loadError = null;
    _notify();
    try {
      final all = await _stock.warehouses();
      if (_disposed) return;
      _warehouses = all.where((w) => w.isActive && w.usesLocations).toList();
      _warehouse = _warehouses.isEmpty
          ? null
          : _warehouses.firstWhere(
              (w) => w.isDefault,
              orElse: () => _warehouses.first,
            );
      _loading = false;
      _notify();
      await _loadShelves();
    } on Object catch (error) {
      if (_disposed) return;
      _loading = false;
      _loadError = error;
      _notify();
    }
  }

  Future<void> selectWarehouse(String id) async {
    if (locked || id == _warehouse?.id) return;
    final chosen = _warehouses.where((w) => w.id == id).firstOrNull;
    if (chosen == null) return;
    _warehouse = chosen;
    _shelves = const [];
    _resetMove(keepSource: false);
    _notify();
    await _loadShelves();
  }

  Future<void> retryShelves() => _loadShelves();

  Future<void> _loadShelves() async {
    final warehouse = _warehouse;
    if (warehouse == null) return;
    _shelvesLoading = true;
    _shelvesError = null;
    _notify();
    try {
      final bins = await _stock.locations(warehouse.id);
      if (_disposed || _warehouse?.id != warehouse.id) return;
      _shelves = bins.where((l) => l.isActive).toList();
    } on Object catch (error) {
      if (_disposed) return;
      _shelvesError = error;
    }
    _shelvesLoading = false;
    _notify();
  }

  StorageLocation? _findShelf(String code) =>
      _shelves.where((l) => l.matches(code)).firstOrNull;

  void scanSource(String raw) {
    final code = raw.trim();
    if (code.isEmpty || locked) return;
    final shelf = _findShelf(code);
    _clearDone();
    if (shelf == null) {
      _reject(ShelfNotFound(code));
      return;
    }
    _alerts.accepted();
    _problem = null;
    final changed = shelf.id != _source?.id;
    _source = shelf;
    if (changed) {
      _target = null;
      _balance = null;
      if (_variant != null) unawaited(_loadBalance());
    }
    _notify();
  }

  Future<void> scanProduct(String raw) async {
    final code = raw.trim();
    if (code.isEmpty || locked || _source == null) return;
    _clearDone();
    final generation = ++_lookup;
    _lookingUp = true;
    _problem = null;
    _notify();
    MoveScanProblem? problem;
    VariantRef? found;
    try {
      found = await _stock.findVariantByCode(code);
      if (found == null) {
        problem = ProductNotFound(code);
      } else if (!found.isActive) {
        problem = ProductInactive(found.sku);
      }
    } on Object catch (error) {
      problem = LookupFailed(error);
    }
    if (_disposed || generation != _lookup) return;
    _lookingUp = false;
    if (problem != null) {
      _reject(problem);
      return;
    }
    _alerts.accepted();
    _variant = found;
    _target = null;
    _quantityText = '1';
    _quantityError = null;
    _overAvailable = false;
    _includeReserved = false;
    _notify();
    await _loadBalance();
  }

  Future<void> _loadBalance() async {
    final variant = _variant;
    final source = _source;
    final warehouse = _warehouse;
    if (variant == null || source == null || warehouse == null) return;
    _balanceLoading = true;
    _balance = null;
    _notify();
    try {
      final balance = await _api.shelfBalance(
        variantId: variant.id,
        warehouseId: warehouse.id,
        locationId: source.id,
      );
      if (_disposed || _variant?.id != variant.id) return;
      _balance = balance;
    } on Object {
      // Best effort: the API refuses a move above the available stock.
    }
    _balanceLoading = false;
    _notify();
  }

  void setQuantityText(String text) {
    _quantityText = text;
    _quantityError = null;
    _overAvailable = false;
    _notify();
  }

  void stepQuantity(int delta) {
    final current = parseQuantity(_quantityText).value ?? 0;
    final next = addQuantities(current, delta);
    if (next <= 0) return;
    setQuantityText(_inputText(next));
  }

  /// "Tümünü taşı": the shelf's available quantity.
  void takeAll() {
    final available = movable;
    if (available == null || available <= 0) return;
    setQuantityText(_inputText(available));
  }

  void scanTarget(String raw) {
    final code = raw.trim();
    if (code.isEmpty || locked || _variant == null) return;
    _clearDone();
    final shelf = _findShelf(code);
    if (shelf == null) {
      _reject(ShelfNotFound(code));
      return;
    }
    if (shelf.id == _source?.id) {
      _reject(const SameShelf());
      return;
    }
    _alerts.accepted();
    _problem = null;
    _target = shelf;
    _notify();
  }

  /// Starts over at the given step (the "Değiştir" buttons).
  void changeSource() {
    if (locked) return;
    _resetMove(keepSource: false);
    _notify();
  }

  void changeProduct() {
    if (locked) return;
    _variant = null;
    _target = null;
    _balance = null;
    _problem = null;
    _notify();
  }

  void changeTarget() {
    if (locked) return;
    _target = null;
    _problem = null;
    _notify();
  }

  /// "Taşı" — and "Tekrar gönder" after an unknown outcome (same key).
  Future<void> submit() async {
    if (_submission is MoveSending) return;
    final retry =
        _submission is MoveFailed && (_submission as MoveFailed).pending;
    if (!retry || _action == null) {
      if (step != MoveStep.ready) return;
      final parsed = parseQuantity(_quantityText);
      if (!parsed.ok) {
        _quantityError = parsed.error;
        _alerts.rejected();
        _notify();
        return;
      }
      final available = movable;
      if (available != null && parsed.value! > available) {
        _overAvailable = true;
        _alerts.rejected();
        _notify();
        return;
      }
      final body = {
        'warehouseId': _warehouse!.id,
        'reference': null,
        'lines': [
          {
            'variantId': _variant!.id,
            'fromLocationId': _source!.id,
            'toLocationId': _target!.id,
            'quantity': parsed.value,
            if (_includeReserved) 'includeReserved': true,
          },
        ],
      };
      final sku = _variant!.sku;
      final quantity = parsed.value!;
      final from = _source!.code;
      final to = _target!.code;
      _done = (sku, quantity, from, to);
      _action = IdempotentAction((key) => _api.relocate(body, key));
    }
    final action = _action!;
    final (sku, quantity, from, to) = _done;
    _submission = const MoveSending();
    _notify();
    try {
      final result = await withDocumentBusyRetry(
        action.run,
        wait: _wait,
        onRetry: () {
          _submission = const MoveSending(retryingBusy: true);
          _notify();
        },
      );
      if (_disposed) return;
      _action = null;
      _alerts.accepted();
      _submission = MoveDone(
        sku: sku,
        quantity: quantity,
        from: from,
        to: to,
        result: result,
      );
      // Next move from the same shelf (a shelf is usually emptied item by
      // item); the product and target are scanned again.
      _resetMove(keepSource: true);
    } on Object catch (error) {
      if (_disposed) return;
      _alerts.rejected();
      final pending = isResendable(error);
      if (!pending) _action = null;
      _submission = MoveFailed(error, pending: pending);
      // Available stock changed under us: show the current figure.
      if (error is ApiException &&
          error.code == 'inventory.insufficient_available') {
        unawaited(_loadBalance());
      }
    }
    _notify();
  }

  /// Leaves an unknown outcome behind (the screen says to check the stock).
  void discardPending() {
    if (_submission is! MoveFailed) return;
    _action = null;
    _submission = const MoveIdle();
    _notify();
  }

  /// Idempotency-Key a resend would carry (tests).
  @visibleForTesting
  IdempotencyKey? get pendingKey => _action?.key;

  void _resetMove({required bool keepSource}) {
    if (!keepSource) _source = null;
    _variant = null;
    _target = null;
    _balance = null;
    _problem = null;
    _quantityText = '1';
    _quantityError = null;
    _overAvailable = false;
    _includeReserved = false;
  }

  void _clearDone() {
    if (_submission is MoveDone || _submission is MoveFailed && !locked) {
      _submission = const MoveIdle();
    }
  }

  void _reject(MoveScanProblem problem) {
    _alerts.rejected();
    _problem = problem;
    _notify();
  }

  void _notify() {
    if (!_disposed) notifyListeners();
  }

  @override
  void dispose() {
    _disposed = true;
    super.dispose();
  }
}

/// Input form of a quantity (`12,5`); the class shadows the name.
String _inputText(num value) => quantityText(value);
