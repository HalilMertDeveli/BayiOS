import 'dart:async';

import 'package:bayios_mobile/core/api/api_exception.dart';
import 'package:bayios_mobile/core/api/idempotency.dart';
import 'package:bayios_mobile/core/api/problem_messages.dart';
import 'package:bayios_mobile/core/session/access.dart';
import 'package:bayios_mobile/features/picking/pick_session.dart';
import 'package:bayios_mobile/features/picking/picking_api.dart';
import 'package:bayios_mobile/features/picking/picking_errors.dart';
import 'package:bayios_mobile/features/picking/picking_models.dart';
import 'package:bayios_mobile/features/picking/scan_alerts.dart';
import 'package:bayios_mobile/l10n/app_localizations.dart';
import 'package:flutter/foundation.dart';

/// What the permissions of `GET /me` allow on the pick screen. Visibility
/// only: the API checks every call (and who the task is assigned to).
@immutable
class PickPermissions {
  const PickPermissions({
    required this.pick,
    required this.claim,
    required this.supervisor,
    required this.reportShortage,
  });

  factory PickPermissions.of(Access access) => PickPermissions(
    pick: access.can('tasks.pick'),
    claim: access.can('tasks.claim'),
    supervisor: access.can('tasks.assign'),
    reportShortage: access.can('tasks.report_shortage'),
  );

  /// `tasks.pick` — scan and complete.
  final bool pick;

  /// `tasks.claim` — take a pool task.
  final bool claim;

  /// `tasks.assign` — may act on every task (not only own ones).
  final bool supervisor;

  /// `tasks.report_shortage`.
  final bool reportShortage;
}

enum PickTone { success, info, warning, error }

/// The line under the scan controls ("✓ SKU +1", "✗ Yanlış raf" …).
@immutable
class PickFeedback {
  const PickFeedback(this.tone, this.title, [this.text]);

  final PickTone tone;
  final String title;
  final String? text;
}

/// Task-level notice (claim / complete / conflicts).
typedef PickNotice = PickFeedback;

enum PickPending { claim, complete }

/// State and commands of one pick task. Every write is one
/// [IdempotentAction]: "Tekrar gönder" after a lost answer resends the same
/// action (same `Idempotency-Key`), so a scan that already reached the API is
/// counted once. Busy conflicts (409 `*.busy` + Retry-After) are retried once
/// automatically with the same key.
class PickController extends ChangeNotifier {
  /// Named arguments: `api:`, `task:`, `userId:`, `permissions:`, `l10n:`,
  /// `alerts:`, `onWrite:`, `busyDelay:`.
  PickController({
    required this._api,
    required this._task,
    required this._userId,
    required this._permissions,
    required this._l10n,
    required this._alerts,
    this.onWrite,
    Duration Function(ApiException error)? busyDelay,
  }) : _busyDelay = busyDelay ?? _defaultBusyDelay;

  final PickingApi _api;
  final String _userId;
  final PickPermissions _permissions;
  final AppLocalizations _l10n;
  final ScanAlerts _alerts;
  final Duration Function(ApiException error) _busyDelay;

  /// Called after every successful write (lists refresh).
  final VoidCallback? onWrite;

  PickingTask _task;
  String? _focusId;
  ScannedLocation? _location;
  String _quantityText = '1';
  QuantityError? _quantityError;
  PickFeedback? _feedback;
  PickNotice? _notice;
  bool _sending = false;
  bool _retryingBusy = false;
  PickPending? _pending;
  bool _disposed = false;

  IdempotentAction<PickScanResult>? _failedScan;
  PickScanRequest? _failedRequest;
  IdempotentAction<PickingTask>? _claimAction;
  IdempotentAction<PickingTask>? _completeAction;
  int? _completeVersion;
  IdempotentAction<PickingTask>? _shortageAction;
  bool _claimUnconfirmed = false;
  bool _completeUnconfirmed = false;
  String? _shortageSignature;
  Future<void> _queue = Future.value();

  static Duration _defaultBusyDelay(ApiException error) {
    final wait = error.retryAfter ?? const Duration(seconds: 1);
    if (wait < const Duration(milliseconds: 300)) {
      return const Duration(milliseconds: 300);
    }
    return wait > const Duration(seconds: 3)
        ? const Duration(seconds: 3)
        : wait;
  }

  // ---- read model ----

  PickingTask get task => _task;
  List<PickingTaskLine> get lines => sortLines(_task.lines);
  PickingTaskLine? get current => currentLine(_task.lines, _focusId);
  ScannedLocation? get location => _location;
  PickStep get step => pickStep(current, _location);
  PickTotals get totals => PickTotals.of(_task.lines);
  String get quantityText => _quantityText;
  QuantityError? get quantityError => _quantityError;
  PickFeedback? get feedback => _feedback;
  PickNotice? get notice => _notice;
  bool get sending => _sending;
  bool get retryingBusy => _retryingBusy;
  PickPending? get pending => _pending;
  PickPermissions get permissions => _permissions;

  /// "Üstlen" whose answer never arrived: the button reads "Tekrar gönder"
  /// and sends the same request (same Idempotency-Key).
  bool get claimNeedsResend => _claimUnconfirmed;

  /// Same for "Görevi tamamla" (same key and If-Match version).
  bool get completeNeedsResend => _completeUnconfirmed;

  /// A scan whose answer never arrived; [resend] sends it again.
  bool get hasFailedScan => _failedScan != null;

  /// The key the failed scan will be resent with (tests, diagnostics).
  IdempotencyKey? get failedScanKey => _failedScan?.key;

  bool get active => _task.task.isActive;
  bool get mine => _task.task.assignedTo == _userId;

  /// Scan / shortage / complete are offered (the API re-checks).
  bool get canWork =>
      active && _permissions.pick && (mine || _permissions.supervisor);

  bool get canClaim => active && _task.task.isPool && _permissions.claim;

  /// What is still open on the current line (upper bound of the quantity).
  double? get quantityMax => current?.quantityRemaining;

  // ---- local UI state ----

  void setQuantityText(String text) {
    _quantityText = text;
    _quantityError = null;
    _notify();
  }

  void incrementQuantity() {
    final value = _parsedOr(1);
    var next = value.floorToDouble() + 1;
    final max = quantityMax;
    if (max != null && next > max) next = max;
    setQuantityText(formatQuantity(next));
  }

  void decrementQuantity() {
    final value = _parsedOr(1);
    final next = value.ceilToDouble() - 1;
    setQuantityText(formatQuantity(next < 1 ? 1 : next));
  }

  bool get canDecrement => _parsedOr(1) > 1;

  bool get canIncrement {
    final max = quantityMax;
    return max == null || _parsedOr(1) < max;
  }

  double _parsedOr(double fallback) => switch (parseQuantity(_quantityText)) {
    QuantityOk(:final value) => value,
    QuantityInvalid() => fallback,
  };

  void focusLine(String lineId) {
    _focusId = lineId;
    final line = currentLine(_task.lines, lineId);
    _location = keepLocation(_location, line);
    _clampQuantity();
    _notify();
  }

  void clearLocation() {
    _location = null;
    _notify();
  }

  void dismissNotice() {
    _notice = null;
    _notify();
  }

  // ---- scans ----

  /// One scanned or typed code. Scans are handled one after another (the
  /// camera may fire faster than the API answers).
  Future<void> handleCode(String raw) =>
      _queue = _queue.then((_) => _handleCode(raw));

  /// Resends the scan whose answer was lost — same body, same key.
  Future<void> resend() {
    final action = _failedScan;
    final request = _failedRequest;
    if (action == null || request == null) return Future.value();
    return _queue = _queue.then((_) => _sendScan(request, action));
  }

  Future<void> _handleCode(String raw) async {
    if (_disposed || !canWork) return;
    final reading = readScan(raw, _task.lines, _location);
    switch (reading) {
      case null:
        return;
      case LocationReading(:final location):
        _onShelf(location);
      case ItemReading(:final code):
        final parsed = parseQuantity(_quantityText, max: quantityMax);
        switch (parsed) {
          case QuantityInvalid(:final error):
            _quantityError = error;
            _feedback = PickFeedback(
              PickTone.error,
              _l10n.pickingScanNotSaved,
              quantityErrorText(_l10n, error, quantityMax),
            );
            _alerts.rejected();
            _notify();
          case QuantityOk(:final value):
            final request = PickScanRequest(
              code: code,
              location: _location?.code,
              quantity: value,
            );
            final id = _task.task.id;
            final action = IdempotentAction<PickScanResult>(
              (key) => _api.scan(id, request, key),
            );
            await _sendScan(request, action);
        }
    }
  }

  void _onShelf(ScannedLocation location) {
    _location = location;
    PickingTaskLine? onShelf;
    if (location.isKnown) {
      for (final l in lines) {
        if (l.locationId == location.locationId && l.isOpen) {
          onShelf = l;
          break;
        }
      }
    }
    if (onShelf != null) _focusId = onShelf.id;
    _feedback = PickFeedback(
      // Same signal on screen as in the hand: green with the "accepted"
      // alert, amber with the "rejected" one, neutral otherwise.
      !location.isKnown
          ? PickTone.warning
          : onShelf != null
          ? PickTone.success
          : PickTone.info,
      _l10n.pickingShelfScanned(location.label),
      !location.isKnown
          ? _l10n.pickingShelfUnknown
          : onShelf == null
          ? _l10n.pickingShelfEmpty
          : _l10n.pickingShelfNext(onShelf.sku),
    );
    // A shelf that is not on this task is most likely the wrong bin: warn
    // now; the API gives the final answer on the item scan.
    if (!location.isKnown) {
      _alerts.rejected();
    } else if (onShelf != null) {
      _alerts.accepted();
    }
    _clampQuantity();
    _notify();
  }

  Future<void> _sendScan(
    PickScanRequest request,
    IdempotentAction<PickScanResult> action,
  ) async {
    if (_disposed) return;
    _sending = true;
    _retryingBusy = false;
    _notify();
    try {
      final result = await _withBusyRetry(action);
      _failedScan = null;
      _failedRequest = null;
      final reallocated = _task.reallocatedBy(result);
      _task = _task.applyScan(result);
      if (reallocated) await _reloadAfterReallocation();
      PickingTaskLine? changed;
      for (final l in _task.lines) {
        if (result.lines.any((p) => p.id == l.id)) {
          changed = l;
          break;
        }
      }
      final following = currentLine(
        _task.lines,
        changed != null && changed.isOpen ? changed.id : null,
      );
      _focusId = following?.id;
      _location = keepLocation(_location, following);
      _quantityText = '1';
      _quantityError = null;
      _feedback = changed == null
          ? PickFeedback(PickTone.success, _l10n.pickingScanSaved)
          : PickFeedback(
              PickTone.success,
              _l10n.pickingScanAccepted(
                changed.sku,
                formatQuantity(request.quantity),
              ),
              [
                _l10n.pickingScanAcceptedBody(
                  formatQuantity(changed.quantityPicked),
                  formatQuantity(changed.quantityToPick),
                  changed.unit,
                ),
                if (reallocated) _l10n.pickingScanReallocated,
                if (!changed.isOpen) _l10n.pickingScanLineDone,
                if (result.openLineCount == 0) _l10n.pickingAllLinesDone,
              ].join(' '),
            );
      _alerts.accepted();
      onWrite?.call();
    } on Object catch (error) {
      _alerts.rejected();
      if (isScanRejection(error)) {
        final code = (error as ApiException).code;
        _location = locationAfterRejection(code, _location);
        _failedScan = null;
        _failedRequest = null;
        _feedback = PickFeedback(
          PickTone.error,
          rejectionTitle(_l10n, code),
          pickingErrorMessage(_l10n, error),
        );
      } else if (isResendable(error)) {
        _failedScan = action;
        _failedRequest = request;
        _feedback = PickFeedback(
          PickTone.error,
          _l10n.pickingScanNotSaved,
          isBusy(error)
              ? _l10n.pickingErrorBusyFinal
              : '${errorMessage(_l10n, error)} ${_l10n.pickingScanResendHint}',
        );
      } else {
        _failedScan = null;
        _failedRequest = null;
        _feedback = PickFeedback(
          PickTone.error,
          _l10n.pickingScanNotSaved,
          pickingErrorMessage(_l10n, error),
        );
        if (_needsReload(error)) unawaited(reload());
      }
    } finally {
      _sending = false;
      _retryingBusy = false;
      _notify();
    }
  }

  // ---- task commands ----

  Future<void> claim() async {
    if (_pending != null || !canClaim) return;
    final id = _task.task.id;
    final action = _claimAction ??= IdempotentAction<PickingTask>(
      (key) => _api.claim(id, key),
    );
    _pending = PickPending.claim;
    _notice = null;
    _notify();
    try {
      _task = await _withBusyRetry(action);
      _claimAction = null;
      _claimUnconfirmed = false;
      _focusId = null;
      _notice = PickFeedback(PickTone.success, _l10n.pickingClaimed);
      onWrite?.call();
    } on Object catch (error) {
      if (!isResendable(error)) _claimAction = null;
      _claimUnconfirmed = isResendable(error) && _claimAction != null;
      _notice = PickFeedback(
        PickTone.warning,
        _l10n.pickingClaimFailed,
        isBusy(error)
            ? _l10n.pickingErrorBusyFinal
            : pickingErrorMessage(_l10n, error),
      );
      if (!isResendable(error)) unawaited(reload());
    } finally {
      _pending = null;
      _notify();
    }
  }

  /// Completes with `If-Match` of the version on screen. One key per
  /// version: a retry after a lost answer replays instead of failing on the
  /// new state; a reload (new version) is a new action.
  Future<void> complete() async {
    if (_pending != null || !canWork) return;
    final id = _task.task.id;
    final version = _task.task.version;
    if (_completeVersion != version || _completeAction == null) {
      final ifMatch = etagFor(version);
      _completeAction = IdempotentAction<PickingTask>(
        (key) => _api.complete(id, ifMatch: ifMatch, key: key),
      );
      _completeVersion = version;
    }
    final action = _completeAction!;
    _pending = PickPending.complete;
    _notice = null;
    _notify();
    try {
      _task = await _withBusyRetry(action);
      _completeAction = null;
      _completeVersion = null;
      _completeUnconfirmed = false;
      _notice = PickFeedback(
        PickTone.success,
        _l10n.pickingCompleted(_task.task.orderNumber),
      );
      _alerts.accepted();
      onWrite?.call();
    } on Object catch (error) {
      if (isVersionConflict(error) || isStale(error)) {
        _notice = PickFeedback(
          PickTone.warning,
          _l10n.pickingConflictTitle,
          _l10n.pickingConflictBody,
        );
        unawaited(reload());
        _completeUnconfirmed = false;
      } else {
        _notice = PickFeedback(
          PickTone.error,
          _l10n.pickingCompleteFailed,
          isBusy(error)
              ? _l10n.pickingErrorBusyFinal
              : pickingErrorMessage(_l10n, error),
        );
        _completeUnconfirmed = isResendable(error);
        if (_needsReload(error)) unawaited(reload());
      }
    } finally {
      _pending = null;
      _notify();
    }
  }

  /// Reports a shortage; resolves with an error message for the dialog, or
  /// null on success. Retrying the same line/reason/quantity after a lost
  /// answer reuses the key.
  Future<String?> reportShortage(
    PickingTaskLine line,
    String reason,
    double quantity,
  ) async {
    final id = _task.task.id;
    final signature = '${line.id}|$reason|$quantity';
    if (_shortageAction == null || _shortageSignature != signature) {
      final request = ReportShortageRequest(reason: reason, quantity: quantity);
      _shortageAction = IdempotentAction<PickingTask>(
        (key) => _api.reportShortage(id, line.id, request, key),
      );
      _shortageSignature = signature;
    }
    final action = _shortageAction!;
    try {
      _task = await _withBusyRetry(action);
      _shortageAction = null;
      _shortageSignature = null;
      final following = currentLine(_task.lines);
      _focusId = following?.id;
      _location = keepLocation(_location, following);
      _clampQuantity();
      _feedback = PickFeedback(
        PickTone.info,
        _l10n.pickingShortageReported(line.sku, formatQuantity(quantity)),
        _l10n.pickingShortageReportedBody,
      );
      onWrite?.call();
      _notify();
      return null;
    } on Object catch (error) {
      if (!isResendable(error)) {
        _shortageAction = null;
        _shortageSignature = null;
      }
      if (_needsReload(error)) unawaited(reload());
      return isBusy(error)
          ? _l10n.pickingErrorBusyFinal
          : pickingErrorMessage(_l10n, error);
    }
  }

  /// Fresh task from the API (pull-to-refresh, after conflicts).
  Future<void> reload() async {
    try {
      final fresh = await _api.get(_task.task.id);
      if (_disposed) return;
      _task = fresh;
      // Another version: "Tamamla" is a new action again, not a resend.
      if (fresh.task.version != _completeVersion) _completeUnconfirmed = false;
      _location = keepLocation(_location, current);
      _clampQuantity();
      _notify();
    } on Object catch (error) {
      if (_disposed) return;
      _notice = PickFeedback(
        PickTone.error,
        _l10n.pickingDefaultTitle,
        pickingErrorMessage(_l10n, error),
      );
      _notify();
    }
  }

  /// After a scan that re-allocated the line to the scanned shelf (#98) the
  /// scan response only has progress: shelves and any new line come from a
  /// fresh read. The scan itself already counted; a failed read keeps the
  /// merged progress (pull-to-refresh later).
  Future<void> _reloadAfterReallocation() async {
    try {
      final fresh = await _api.get(_task.task.id);
      if (_disposed) return;
      _task = fresh;
      final scanned = _location;
      if (scanned != null && !scanned.isKnown) {
        // The scanned shelf is on the task now.
        _location = matchLocation(scanned.code, fresh.lines) ?? scanned;
      }
    } on Object {
      // Best effort, see above.
    }
  }

  // ---- helpers ----

  Future<T> _withBusyRetry<T>(IdempotentAction<T> action) async {
    try {
      return await action.run();
    } on ApiException catch (error) {
      if (!isBusy(error)) rethrow;
      _retryingBusy = true;
      _notify();
      await Future<void>.delayed(_busyDelay(error));
      return await action.run(); // same key
    }
  }

  static bool _needsReload(Object error) =>
      isStale(error) ||
      (error is ApiException &&
          (error.kind == ApiErrorKind.forbidden ||
              error.kind == ApiErrorKind.notFound ||
              error.code == PickingProblem.notAssignee));

  /// Keeps the typed quantity within what is open on the (new) current line.
  void _clampQuantity() {
    final max = quantityMax;
    if (max == null || max <= 0) return;
    final parsed = parseQuantity(_quantityText);
    if (parsed is QuantityOk && parsed.value > max) {
      _quantityText = formatQuantity(max < 1 ? max : max.floorToDouble());
    }
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

/// Turkish text for a rejected quantity.
String quantityErrorText(
  AppLocalizations l10n,
  QuantityError error,
  double? max,
) => switch (error) {
  QuantityError.empty => l10n.pickingQuantityErrorEmpty,
  QuantityError.invalid => l10n.pickingQuantityErrorInvalid,
  QuantityError.notPositive => l10n.pickingQuantityErrorNotPositive,
  QuantityError.tooManyDecimals => l10n.pickingQuantityErrorDecimals,
  QuantityError.overOpen => l10n.pickingQuantityErrorOverOpen(
    formatQuantity(max ?? 0),
  ),
};
