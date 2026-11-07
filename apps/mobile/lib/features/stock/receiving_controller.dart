import 'dart:async';

import 'package:bayios_mobile/core/api/api_exception.dart';
import 'package:bayios_mobile/core/api/idempotency.dart';
import 'package:bayios_mobile/features/stock/quantity.dart';
import 'package:bayios_mobile/features/stock/stock_api.dart';
import 'package:bayios_mobile/features/stock/stock_errors.dart';
import 'package:bayios_mobile/features/stock/stock_models.dart';
import 'package:flutter/foundation.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

/// Max lines per receipt (API rule) and reference length.
const maxReceiptLines = 500;
const referenceMaxLength = 200;

/// One line of the receipt being built on the device (nothing is stored on
/// the server until "Mal kabulü kaydet": the API has no receipt drafts).
@immutable
class ReceiptLineDraft {
  const ReceiptLineDraft({
    required this.key,
    required this.variant,
    required this.quantity,
    this.location,
  });

  final int key;
  final VariantRef variant;
  final num quantity;
  final StorageLocation? location;

  ReceiptLineDraft withQuantity(num value) => ReceiptLineDraft(
    key: key,
    variant: variant,
    quantity: value,
    location: location,
  );
}

/// Outcome of the last scan / typed code.
sealed class ScanFeedback {
  const ScanFeedback();
}

/// The code matched a product: it waits for quantity and bin.
final class ScanMatched extends ScanFeedback {
  const ScanMatched(this.variant);

  final VariantRef variant;
}

/// No product with this barcode or SKU (the mobile app never creates one).
final class ScanNotFound extends ScanFeedback {
  const ScanNotFound(this.code);

  final String code;
}

/// Known product that is archived / passive: no stock can be received.
final class ScanInactive extends ScanFeedback {
  const ScanInactive(this.variant);

  final VariantRef variant;
}

final class ScanFailed extends ScanFeedback {
  const ScanFailed(this.code, this.error);

  final String code;
  final Object error;
}

sealed class ReceiptSubmission {
  const ReceiptSubmission();
}

final class SubmissionIdle extends ReceiptSubmission {
  const SubmissionIdle();
}

final class SubmissionSending extends ReceiptSubmission {
  const SubmissionSending({this.retryingBusy = false});

  /// Waiting for the automatic retry after `inventory.busy`.
  final bool retryingBusy;
}

/// The post failed. [pending] = the outcome is unknown (no response, 5xx,
/// busy twice…): the lines are locked and "Tekrar dene" resends the same
/// request with the same Idempotency-Key, so it is applied at most once.
final class SubmissionFailed extends ReceiptSubmission {
  const SubmissionFailed(this.error, {required this.pending});

  final Object error;
  final bool pending;
}

final class SubmissionSucceeded extends ReceiptSubmission {
  const SubmissionSucceeded({
    required this.result,
    required this.lines,
    required this.total,
    required this.posted,
  });

  final ReceiptResult result;
  final int lines;
  final num total;

  /// The posted lines (SKU + quantity + new balance on the screen).
  final List<ReceiptLineDraft> posted;
}

/// Client-side line validation (the API validates again).
@immutable
class LineValidation {
  const LineValidation({this.quantity, this.locationMissing = false});

  final QuantityError? quantity;
  final bool locationMissing;

  bool get ok => quantity == null && !locationMissing;
}

@immutable
class ReceivingState {
  const ReceivingState({
    this.loading = true,
    this.loadError,
    this.warehouses = const [],
    this.warehouse,
    this.locations = const [],
    this.locationsLoading = false,
    this.locationsError,
    this.reference = '',
    this.lookingUp = false,
    this.scan,
    this.current,
    this.lines = const [],
    this.lineErrors = const {},
    this.formErrors = const [],
    this.submission = const SubmissionIdle(),
    this.receipts = 0,
  });

  final bool loading;
  final Object? loadError;

  /// Active warehouses, default first.
  final List<Warehouse> warehouses;

  /// Target warehouse; null when the tenant has none yet (the API then
  /// creates and uses the default warehouse).
  final Warehouse? warehouse;

  /// Active bins of [warehouse] (when it uses locations).
  final List<StorageLocation> locations;
  final bool locationsLoading;
  final Object? locationsError;
  final String reference;
  final bool lookingUp;
  final ScanFeedback? scan;

  /// Matched product waiting for quantity and bin.
  final VariantRef? current;
  final List<ReceiptLineDraft> lines;

  /// 422 field errors by line key (already translated by the screen).
  final Map<int, List<String>> lineErrors;

  /// 422 field errors that belong to no line (raw API messages).
  final List<String> formErrors;
  final ReceiptSubmission submission;

  /// Receipts posted on this screen (resets the reference field).
  final int receipts;

  bool get usesLocations => warehouse?.usesLocations ?? false;

  /// Lines may not change while a post is in flight or its outcome is
  /// unknown (a retry must send exactly the same request).
  bool get locked => switch (submission) {
    SubmissionSending() => true,
    SubmissionFailed(:final pending) => pending,
    _ => false,
  };

  num get total => lines.fold<num>(0, (s, l) => addQuantities(s, l.quantity));

  ReceivingState copyWith({
    bool? loading,
    Object? loadError = _keep,
    List<Warehouse>? warehouses,
    Object? warehouse = _keep,
    List<StorageLocation>? locations,
    bool? locationsLoading,
    Object? locationsError = _keep,
    String? reference,
    bool? lookingUp,
    Object? scan = _keep,
    Object? current = _keep,
    List<ReceiptLineDraft>? lines,
    Map<int, List<String>>? lineErrors,
    List<String>? formErrors,
    ReceiptSubmission? submission,
    int? receipts,
  }) => ReceivingState(
    loading: loading ?? this.loading,
    loadError: loadError == _keep ? this.loadError : loadError,
    warehouses: warehouses ?? this.warehouses,
    warehouse: warehouse == _keep ? this.warehouse : warehouse as Warehouse?,
    locations: locations ?? this.locations,
    locationsLoading: locationsLoading ?? this.locationsLoading,
    locationsError: locationsError == _keep
        ? this.locationsError
        : locationsError,
    reference: reference ?? this.reference,
    lookingUp: lookingUp ?? this.lookingUp,
    scan: scan == _keep ? this.scan : scan as ScanFeedback?,
    current: current == _keep ? this.current : current as VariantRef?,
    lines: lines ?? this.lines,
    lineErrors: lineErrors ?? this.lineErrors,
    formErrors: formErrors ?? this.formErrors,
    submission: submission ?? this.submission,
    receipts: receipts ?? this.receipts,
  );
}

const Object _keep = Object();

final _lineField = RegExp(r'^lines\[(\d+)\]\.(\w+)$');

/// Goods receipt (AF-05 / WHT-11): pick the warehouse, scan or type a code,
/// enter quantity and bin, repeat, then post once. Stock changes only
/// through `POST /inventory/receipts` (ledger `receipt` movements).
class ReceivingController extends Notifier<ReceivingState> {
  IdempotentAction<ReceiptResult>? _action;
  int _nextKey = 1;
  int _scanGeneration = 0;

  @override
  ReceivingState build() {
    scheduleMicrotask(load);
    return const ReceivingState();
  }

  StockApi get _api => ref.read(stockApiProvider);

  /// Loads the warehouses (again after a failure).
  Future<void> load() async {
    if (!ref.mounted) return;
    state = state.copyWith(loading: true, loadError: null);
    try {
      final all = await _api.warehouses();
      if (!ref.mounted) return;
      final active = all.where((w) => w.isActive).toList();
      final chosen = active.isEmpty
          ? null
          : active.firstWhere((w) => w.isDefault, orElse: () => active.first);
      state = state.copyWith(
        loading: false,
        warehouses: active,
        warehouse: chosen,
      );
      await _loadLocations();
    } on Object catch (error) {
      if (ref.mounted) state = state.copyWith(loading: false, loadError: error);
    }
  }

  /// Another target warehouse; only before any line exists (lines and bins
  /// belong to one warehouse).
  Future<void> selectWarehouse(String id) async {
    if (state.lines.isNotEmpty || state.locked) return;
    final chosen = state.warehouses.where((w) => w.id == id).firstOrNull;
    if (chosen == null || chosen.id == state.warehouse?.id) return;
    state = state.copyWith(warehouse: chosen, locations: const []);
    await _loadLocations();
  }

  Future<void> retryLocations() => _loadLocations();

  Future<void> _loadLocations() async {
    final warehouse = state.warehouse;
    if (warehouse == null || !warehouse.usesLocations) {
      state = state.copyWith(locations: const [], locationsError: null);
      return;
    }
    state = state.copyWith(locationsLoading: true, locationsError: null);
    try {
      final bins = await _api.locations(warehouse.id);
      if (!ref.mounted || state.warehouse?.id != warehouse.id) return;
      state = state.copyWith(
        locations: bins.where((l) => l.isActive).toList(),
        locationsLoading: false,
      );
    } on Object catch (error) {
      if (ref.mounted) {
        state = state.copyWith(locationsLoading: false, locationsError: error);
      }
    }
  }

  void setReference(String value) => state = state.copyWith(reference: value);

  /// A scanned or typed product code: barcode, then SKU.
  Future<void> lookup(String raw) async {
    final code = raw.trim();
    if (code.isEmpty || state.locked) return;
    final generation = ++_scanGeneration;
    state = state.copyWith(lookingUp: true, scan: null);
    ScanFeedback feedback;
    try {
      final variant = await _api.findVariantByCode(code);
      feedback = switch (variant) {
        null => ScanNotFound(code),
        final v when !v.isActive => ScanInactive(v),
        final v => ScanMatched(v),
      };
    } on Object catch (error) {
      feedback = ScanFailed(code, error);
    }
    if (!ref.mounted || generation != _scanGeneration) return;
    state = state.copyWith(
      lookingUp: false,
      scan: feedback,
      current: feedback is ScanMatched ? feedback.variant : null,
      // A new scan starts a new receipt after a success.
      submission: state.submission is SubmissionSucceeded
          ? const SubmissionIdle()
          : null,
    );
  }

  /// The bin a scanned / typed bin label refers to in the target warehouse,
  /// or null (wrong warehouse, typo, inactive bin).
  StorageLocation? findBin(String code) =>
      state.locations.where((l) => l.matches(code)).firstOrNull;

  LineValidation validateLine(String quantityText, String? locationId) {
    final q = parseQuantity(quantityText);
    final missingBin =
        state.usesLocations &&
        (locationId == null || findBinById(locationId) == null);
    return LineValidation(quantity: q.error, locationMissing: missingBin);
  }

  StorageLocation? findBinById(String id) =>
      state.locations.where((l) => l.id == id).firstOrNull;

  /// Adds the matched product with the entered quantity and bin. The same
  /// product into the same bin is merged into the existing line.
  LineValidation addCurrent(String quantityText, String? locationId) {
    final variant = state.current;
    if (variant == null || state.locked) return const LineValidation();
    final validation = validateLine(quantityText, locationId);
    if (!validation.ok) return validation;
    if (state.lines.length >= maxReceiptLines) {
      return const LineValidation(quantity: QuantityError.tooLarge);
    }
    final quantity = parseQuantity(quantityText).value!;
    final location = state.usesLocations ? findBinById(locationId!) : null;
    final index = state.lines.indexWhere(
      (l) => l.variant.id == variant.id && l.location?.id == location?.id,
    );
    final lines = [...state.lines];
    if (index >= 0) {
      lines[index] = lines[index].withQuantity(
        addQuantities(lines[index].quantity, quantity),
      );
    } else {
      lines.add(
        ReceiptLineDraft(
          key: _nextKey++,
          variant: variant,
          quantity: quantity,
          location: location,
        ),
      );
    }
    state = state.copyWith(
      lines: lines,
      current: null,
      scan: null,
      lineErrors: const {},
      formErrors: const [],
      submission: state.submission is SubmissionFailed
          ? const SubmissionIdle()
          : null,
    );
    return validation;
  }

  void cancelCurrent() => state = state.copyWith(current: null, scan: null);

  void removeLine(int key) {
    if (state.locked) return;
    state = state.copyWith(
      lines: state.lines.where((l) => l.key != key).toList(),
      lineErrors: const {},
      formErrors: const [],
      submission: state.submission is SubmissionFailed
          ? const SubmissionIdle()
          : null,
    );
  }

  /// The request body of the current lines (on-screen order: the API's
  /// field errors `lines[i]` refer to it).
  Map<String, Object?> buildRequest() {
    final reference = state.reference.trim();
    return {
      'warehouseId': state.warehouse?.id,
      'reference': reference.isEmpty ? null : reference,
      'lines': [
        for (final line in state.lines)
          {
            'variantId': line.variant.id,
            'locationId': line.location?.id,
            'quantity': line.quantity,
            'unitCost': null,
          },
      ],
    };
  }

  /// "Mal kabulü kaydet" — and "Tekrar dene" after an unknown outcome,
  /// which resends the very same request with the same Idempotency-Key.
  Future<void> submit() async {
    final current = state.submission;
    if (current is SubmissionSending) return;
    if (state.lines.isEmpty) return;
    if (state.reference.trim().length > referenceMaxLength) return;

    final retry = current is SubmissionFailed && current.pending;
    if (!retry || _action == null) {
      final body = buildRequest();
      _action = IdempotentAction((key) => _api.createReceipt(body, key));
    }
    final action = _action!;
    final posted = state.lines;
    final total = state.total;
    state = state.copyWith(
      submission: const SubmissionSending(),
      lineErrors: const {},
      formErrors: const [],
    );
    try {
      final result = await withBusyRetry(
        action.run,
        wait: ref.read(stockDelayProvider),
        onRetry: () {
          if (ref.mounted) {
            state = state.copyWith(
              submission: const SubmissionSending(retryingBusy: true),
            );
          }
        },
      );
      if (!ref.mounted) return;
      _action = null;
      state = state.copyWith(
        lines: const [],
        reference: '',
        receipts: state.receipts + 1,
        current: null,
        scan: null,
        submission: SubmissionSucceeded(
          result: result,
          lines: posted.length,
          total: total,
          posted: posted,
        ),
      );
    } on Object catch (error) {
      if (!ref.mounted) return;
      final pending = error is! ApiException || error.isTransient;
      // A 4xx stored nothing: the corrected request is a new action.
      if (!pending) _action = null;
      state = state.copyWith(
        submission: SubmissionFailed(error, pending: pending),
        lineErrors: _lineErrorsOf(error),
        formErrors: _formErrorsOf(error),
      );
    }
  }

  /// Leaves an unknown outcome behind to edit the lines (the screen warns
  /// that the receipt may already be recorded and points to the stock).
  void discardPending() {
    if (state.submission is! SubmissionFailed) return;
    _action = null;
    state = state.copyWith(submission: const SubmissionIdle());
  }

  /// After a success: start the next receipt in the same warehouse.
  void startNew() => state = state.copyWith(
    submission: const SubmissionIdle(),
    scan: null,
    current: null,
  );

  /// Idempotency-Key of the request a "Tekrar dene" would send (tests).
  @visibleForTesting
  IdempotencyKey? get pendingKey => _action?.key;

  Map<int, List<String>> _lineErrorsOf(Object error) {
    if (error is! ApiException) return const {};
    final result = <int, List<String>>{};
    for (final entry in error.fieldErrors.entries) {
      final match = _lineField.firstMatch(entry.key);
      if (match == null) continue;
      final index = int.parse(match.group(1)!);
      if (index >= state.lines.length) continue;
      (result[state.lines[index].key] ??= []).addAll(entry.value);
    }
    return result;
  }

  List<String> _formErrorsOf(Object error) {
    if (error is! ApiException) return const [];
    return [
      for (final entry in error.fieldErrors.entries)
        if (!_lineField.hasMatch(entry.key)) ...entry.value,
    ];
  }
}

final NotifierProvider<ReceivingController, ReceivingState>
receivingControllerProvider =
    NotifierProvider.autoDispose<ReceivingController, ReceivingState>(
      ReceivingController.new,
    );
