import 'dart:async';

import 'package:bayios_mobile/core/api/api_exception.dart';
import 'package:bayios_mobile/core/api/idempotency.dart';
import 'package:bayios_mobile/features/picking/scan_alerts.dart';
import 'package:bayios_mobile/features/stock/quantity.dart';
import 'package:bayios_mobile/features/stock/stock_models.dart';
import 'package:bayios_mobile/features/warehouse/warehouse_errors.dart';
import 'package:flutter/foundation.dart';

/// One line the last accepted scan changed ("FLT-001 · A-01-01 → 5").
@immutable
class ChangedLine {
  const ChangedLine({
    required this.sku,
    required this.quantity,
    required this.total,
    this.shelf,
  });

  final String sku;
  final String? shelf;

  /// What this scan added (receipt) or the entered quantity (count).
  final num quantity;

  /// Received / counted so far on the line after the scan.
  final num total;
}

/// Outcome of the last scan, shown under the scan controls.
sealed class EntryFeedback {
  const EntryFeedback();
}

/// The API accepted the entry.
final class EntryAccepted extends EntryFeedback {
  const EntryAccepted(this.changes, {this.replayed = false});

  final List<ChangedLine> changes;

  /// "Tekrar gönder" got the stored answer of an attempt that had already
  /// arrived: counted once.
  final bool replayed;
}

/// Refused (wrong item, unknown code, over-receipt, validation…): nothing
/// changed. [code] is what was scanned.
final class EntryRejected extends EntryFeedback {
  const EntryRejected(this.error, {this.code});

  final Object error;
  final String? code;
}

/// No answer (or 5xx / busy twice): the outcome is unknown; "Tekrar gönder"
/// sends the same request with the same Idempotency-Key.
final class EntryPending extends EntryFeedback {
  const EntryPending(this.error, {this.code});

  final Object error;
  final String? code;
}

/// The document changed elsewhere (412, closed / submitted meanwhile): it
/// was reloaded and the entry was not applied.
final class EntryStale extends EntryFeedback {
  const EntryStale(this.error);

  final Object error;
}

/// Client-side check before sending (the API checks again).
enum EntryProblem { shelfRequired }

/// Shared state machine of the scan-to-write screens (transfer receipt and
/// stock count): a sticky shelf code, a quantity and a product scan that
/// writes immediately. Every scan is one [IdempotentAction]; a lost answer
/// keeps the action for "Tekrar gönder" (same key, applied at most once) and
/// locks the scan controls until it is resent or dropped. `*.busy` is
/// retried once automatically with the same key.
abstract class DocumentScanController<D> extends ChangeNotifier {
  DocumentScanController({
    required this.alerts,
    required this.wait,
    this.onWrite,
  });

  final ScanAlerts alerts;
  final Future<void> Function(Duration) wait;

  /// After every accepted write (lists refresh).
  final VoidCallback? onWrite;

  D? _document;
  Object? _loadError;
  bool _loading = true;
  bool _disposed = false;
  String? _shelf;
  String _quantityText = '1';
  QuantityError? _quantityError;
  EntryProblem? _entryProblem;
  EntryFeedback? _feedback;
  bool _sending = false;
  bool _retryingBusy = false;
  IdempotentAction<D>? _failed;
  String? _failedCode;
  Warehouse? _warehouse;

  D? get document => _document;
  Object? get loadError => _loadError;
  bool get loading => _loading;

  /// Scanned shelf code (sent as `locationCode`; the API resolves it).
  String? get shelf => _shelf;
  String get quantityText => _quantityText;
  QuantityError? get quantityError => _quantityError;
  EntryProblem? get entryProblem => _entryProblem;
  EntryFeedback? get feedback => _feedback;
  bool get sending => _sending;
  bool get retryingBusy => _retryingBusy;

  /// A scan whose answer never arrived is waiting for "Tekrar gönder".
  bool get hasPending => _failed != null;

  /// Idempotency-Key a resend would carry (tests, diagnostics).
  @visibleForTesting
  IdempotencyKey? get pendingKey => _failed?.key;

  /// The document's warehouse (for `usesLocations`); null until known.
  Warehouse? get warehouse => _warehouse;

  /// Shelves are tracked in the warehouse: a scan needs a shelf first.
  bool get requiresShelf => _warehouse?.usesLocations ?? false;

  /// Scanning is possible (document loaded, writable, nothing pending).
  bool get canScan => _document != null && writable && !_sending && !hasPending;

  /// The document's state still takes entries.
  bool get writable;

  /// Loads the document.
  @protected
  Future<D> fetch();

  /// The warehouse the entries go to (resolved from the document).
  @protected
  Future<Warehouse?> fetchWarehouse(D document);

  /// Sends one entry.
  @protected
  Future<D> post(Map<String, Object?> body, IdempotencyKey key);

  /// Request body of one entry.
  @protected
  Map<String, Object?> body(String code, num quantity, String? shelf);

  /// Lines the accepted entry changed (before → after).
  @protected
  List<ChangedLine> changes(D before, D after);

  /// Quantity parsing (counts in `set` mode also accept 0).
  @protected
  QuantityParse parse(String text) => parseQuantity(text);

  Future<void> load() async {
    _loading = true;
    _loadError = null;
    _notify();
    try {
      final doc = await fetch();
      if (_disposed) return;
      _document = doc;
      _loading = false;
      _notify();
      if (_warehouse == null) unawaited(_loadWarehouse(doc));
    } on Object catch (error) {
      if (_disposed) return;
      _loadError = error;
      _loading = false;
      _notify();
    }
  }

  Future<void> _loadWarehouse(D doc) async {
    try {
      final warehouse = await fetchWarehouse(doc);
      if (_disposed) return;
      _warehouse = warehouse;
      _notify();
    } on Object {
      // Best effort: without it the API still says when a shelf is missing.
    }
  }

  void setShelf(String code) {
    final value = code.trim();
    if (value.isEmpty) return;
    _shelf = value;
    if (_entryProblem == EntryProblem.shelfRequired) _entryProblem = null;
    _notify();
  }

  void clearShelf() {
    _shelf = null;
    _notify();
  }

  void setQuantityText(String text) {
    _quantityText = text;
    _quantityError = null;
    _notify();
  }

  void stepQuantity(int delta) {
    final current = parse(_quantityText).value ?? 0;
    final next = addQuantities(current, delta);
    if (next < 0 || (next == 0 && parse('0').error != null)) return;
    setQuantityText(_inputText(next));
  }

  /// A scanned or typed product code: sends the entry right away.
  Future<void> enter(String raw) async {
    final code = raw.trim();
    if (code.isEmpty || !canScan) return;
    if (requiresShelf && _shelf == null) {
      _entryProblem = EntryProblem.shelfRequired;
      alerts.rejected();
      _notify();
      return;
    }
    final parsed = parse(_quantityText);
    if (!parsed.ok) {
      _quantityError = parsed.error;
      alerts.rejected();
      _notify();
      return;
    }
    final request = body(code, parsed.value!, _shelf);
    await _run(IdempotentAction<D>((key) => post(request, key)), code);
  }

  /// "Tekrar gönder": the same request with the same Idempotency-Key.
  Future<void> resend() async {
    final action = _failed;
    if (action == null || _sending) return;
    await _run(action, _failedCode, resent: true);
  }

  /// Drops an unknown outcome (the screen says to check the lines).
  void discardPending() {
    _failed = null;
    _failedCode = null;
    _feedback = null;
    _notify();
  }

  Future<void> _run(
    IdempotentAction<D> action,
    String? code, {
    bool resent = false,
  }) async {
    final before = _document;
    _sending = true;
    _retryingBusy = false;
    _entryProblem = null;
    _notify();
    try {
      final after = await withDocumentBusyRetry(
        action.run,
        wait: wait,
        onRetry: () {
          _retryingBusy = true;
          _notify();
        },
      );
      if (_disposed) return;
      _failed = null;
      _failedCode = null;
      _document = after;
      _quantityText = '1';
      _feedback = EntryAccepted(
        before == null ? const [] : changes(before, after),
        replayed: resent,
      );
      alerts.accepted();
      onWrite?.call();
    } on Object catch (error) {
      if (_disposed) return;
      alerts.rejected();
      if (isStaleDocument(error)) {
        _failed = null;
        _feedback = EntryStale(error);
        _sending = false;
        _retryingBusy = false;
        await _reload();
        return;
      }
      if (isResendable(error)) {
        _failed = action;
        _failedCode = code;
        _feedback = EntryPending(error, code: code);
      } else {
        // A 4xx stored nothing: the corrected scan is a new action.
        _failed = null;
        _failedCode = null;
        _feedback = EntryRejected(error, code: code);
      }
    }
    _sending = false;
    _retryingBusy = false;
    _notify();
  }

  /// Reloads the document, keeping the feedback (stale / after submit).
  @protected
  Future<void> reload() => _reload();

  Future<void> _reload() async {
    try {
      final doc = await fetch();
      if (_disposed) return;
      _document = doc;
    } on Object catch (error) {
      if (_disposed) return;
      _loadError = _document == null ? error : null;
    }
    _notify();
  }

  @protected
  void replaceDocument(D document) {
    _document = document;
    _notify();
  }

  @protected
  void setFeedback(EntryFeedback? feedback) {
    _feedback = feedback;
    _notify();
  }

  @protected
  bool get disposed => _disposed;

  void _notify() {
    if (!_disposed) notifyListeners();
  }

  @override
  void dispose() {
    _disposed = true;
    super.dispose();
  }
}

/// Quantity accepting 0 (count `set` mode: "this shelf is empty").
QuantityParse parseCountQuantity(String text) {
  final parsed = parseQuantity(text);
  if (parsed.error == QuantityError.notPositive) {
    return const QuantityParse.ok(0);
  }
  return parsed;
}

/// Problem code of an entry error (tests, screens).
String? problemCode(Object error) => error is ApiException ? error.code : null;

/// Input form of a quantity (`12,5`); the classes shadow the name.
String _inputText(num value) => quantityText(value);
