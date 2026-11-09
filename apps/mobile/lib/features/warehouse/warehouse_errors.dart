import 'package:bayios_mobile/core/api/api_exception.dart';
import 'package:bayios_mobile/features/stock/quantity.dart';
import 'package:bayios_mobile/features/stock/stock_errors.dart';
import 'package:bayios_mobile/l10n/app_localizations.dart';

/// Permission ids of the handheld warehouse screens
/// (docs/product/roles-permissions.md §3.4). Visibility only: the API checks
/// every call again and answers 403 otherwise.
abstract final class WarehousePermissions {
  /// Receive a dispatched transfer (warehouse_staff included).
  static const receiveTransfer = 'inventory.transfer.receive';

  /// Enter counts and submit (warehouse_staff included).
  static const performCount = 'inventory.count.perform';

  /// Shelf-to-shelf move: `POST /inventory/relocations` declares
  /// `inventory.relocate` (owner / admin / manager / warehouse_staff, #98).
  static const relocate = 'inventory.relocate';

  static const List<String> all = [receiveTransfer, performCount, relocate];
}

/// Problem codes of the transfer / count endpoints (StockTransferService,
/// StockCountService).
abstract final class DocumentProblem {
  static const overReceipt = 'transfer.over_receipt';
  static const unknownCode = 'transfer.unknown_code';
  static const wrongItem = 'transfer.wrong_item';
  static const unknownLocation = 'transfer.unknown_location';
  static const transferInvalidTransition = 'transfer.invalid_transition';
  static const warehouseInactive = 'transfer.warehouse_inactive';
  static const transferBusy = 'transfer.busy';
  static const countUnknownCode = 'count.unknown_code';
  static const countUnknownLocation = 'count.unknown_location';
  static const countOutOfScope = 'count.out_of_scope';
  static const countInvalidTransition = 'count.invalid_transition';
  static const nothingCounted = 'count.nothing_counted';
  static const countBusy = 'count.busy';

  /// The scan was refused, nothing changed: big red notice + alert sound.
  static const Set<String> scanRejections = {
    overReceipt,
    unknownCode,
    wrongItem,
    unknownLocation,
    countUnknownCode,
    countUnknownLocation,
    countOutOfScope,
  };
}

/// `transfer.busy`, `count.busy`, `inventory.busy` (409 + Retry-After): the
/// same request (same Idempotency-Key) is sent once more automatically.
bool isDocumentBusy(Object error) =>
    error is ApiException &&
    error.status == 409 &&
    (error.code?.endsWith('.busy') ?? false);

/// 412 (If-Match) or an action the current status no longer allows: the
/// document changed elsewhere; the screen reloads it.
bool isStaleDocument(Object error) =>
    error is ApiException &&
    (error.kind == ApiErrorKind.versionMismatch ||
        error.code == DocumentProblem.transferInvalidTransition ||
        error.code == DocumentProblem.countInvalidTransition);

bool isScanRejection(Object error) =>
    error is ApiException &&
    DocumentProblem.scanRejections.contains(error.code);

/// No answer, 5xx, rate limit, busy twice: the outcome is unknown and the
/// same action may be resent (same Idempotency-Key, applied at most once).
bool isResendable(Object error) => error is! ApiException || error.isTransient;

/// Waits Retry-After (≤ 5 s) after a `*.busy` and sends once more with the
/// same key ([send] is `IdempotentAction.run`). A second busy is rethrown.
Future<T> withDocumentBusyRetry<T>(
  Future<T> Function() send, {
  required Future<void> Function(Duration) wait,
  void Function()? onRetry,
}) async {
  try {
    return await send();
  } on ApiException catch (error) {
    if (!isDocumentBusy(error)) rethrow;
    onRetry?.call();
    await wait(busyRetryDelay(error));
    return await send();
  }
}

/// Turkish "what happened + what to do" for a failed transfer / count /
/// shelf-move call (same texts as the web's lib/stock-documents/errors.ts);
/// everything else falls back to [stockErrorMessage].
String documentErrorMessage(
  AppLocalizations l10n,
  Object error, {
  String? forbidden,
}) {
  if (error is ApiException) {
    switch (error.code) {
      case DocumentProblem.overReceipt:
        return l10n.docErrorOverReceipt;
      case DocumentProblem.unknownCode || DocumentProblem.countUnknownCode:
        return l10n.docErrorUnknownCode;
      case DocumentProblem.wrongItem:
        return l10n.docErrorWrongItem;
      case DocumentProblem.unknownLocation:
        return l10n.docErrorUnknownLocationTransfer;
      case DocumentProblem.countUnknownLocation:
        return l10n.docErrorUnknownLocationCount;
      case DocumentProblem.countOutOfScope:
        return l10n.docErrorOutOfScope;
      case DocumentProblem.transferInvalidTransition:
        return l10n.docErrorTransferChanged;
      case DocumentProblem.countInvalidTransition:
        return l10n.docErrorCountChanged;
      case DocumentProblem.warehouseInactive:
        return l10n.docErrorWarehouseInactive;
      case DocumentProblem.nothingCounted:
        return l10n.docErrorNothingCounted;
      case DocumentProblem.transferBusy ||
          DocumentProblem.countBusy ||
          'inventory.busy':
        return l10n.docErrorBusy;
    }
    if (error.kind == ApiErrorKind.versionMismatch) {
      return l10n.docErrorVersionMismatch;
    }
    if (error.kind == ApiErrorKind.validation) {
      final first = error.fieldErrors.values.expand((m) => m).firstOrNull;
      if (first != null) return documentFieldMessage(l10n, first);
    }
    if (error.kind == ApiErrorKind.notFound) return l10n.docErrorNotFound;
  }
  return stockErrorMessage(l10n, error, forbidden: forbidden);
}

/// Largest quantity the first `shortages` entry of a 409
/// `inventory.insufficient_available` says can move (null when absent).
num? shortageAvailable(Object error) {
  if (error is! ApiException) return null;
  final shortages = error.problem?.extensions['shortages'];
  if (shortages is! List<Object?> || shortages.isEmpty) return null;
  final first = shortages.first;
  if (first is! Map<String, Object?>) return null;
  return readOptionalQuantity(first['available']);
}

/// API validation messages (English developer text) → Turkish.
String documentFieldMessage(AppLocalizations l10n, String message) {
  if (message.startsWith('A destination location is required')) {
    return l10n.docFieldDestinationShelfRequired;
  }
  if (message.startsWith('Source and target must differ')) {
    return l10n.docFieldSameShelf;
  }
  if (message.startsWith('Line not found')) return l10n.docFieldLineMissing;
  if (message.startsWith('Must be ≥ 0')) return l10n.docFieldQuantityZeroOk;
  if (message.startsWith('The counted quantity is too large')) {
    return l10n.stockQuantityTooLarge;
  }
  return stockFieldMessage(l10n, message);
}
