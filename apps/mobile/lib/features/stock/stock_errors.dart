import 'package:bayios_mobile/core/api/api_exception.dart';
import 'package:bayios_mobile/core/api/problem_messages.dart';
import 'package:bayios_mobile/core/session/access.dart';
import 'package:bayios_mobile/core/session/session_controller.dart';
import 'package:bayios_mobile/features/stock/quantity.dart';
import 'package:bayios_mobile/l10n/app_localizations.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

/// Permission ids (docs/product/roles-permissions.md §3.4). Visibility only:
/// the API checks every call again and answers 403 otherwise.
abstract final class StockPermissions {
  static const read = 'inventory.read';
  static const receive = 'inventory.receive';
  static const adjust = 'inventory.adjust';
}

/// Effective permissions of the active tenant (`GET /me`).
final stockAccessProvider = Provider<Access>((ref) {
  final session = ref.watch(sessionControllerProvider);
  return session is SessionReady ? session.access : Access.none;
});

bool isInventoryBusy(Object error) =>
    error is ApiException && error.code == 'inventory.busy';

/// Tenant policy `warn`: 409 `inventory.insufficient_stock` with
/// `confirmationRequired: true` — the same request with
/// `confirmNegativeStock: true` goes through after the user confirmed.
bool needsNegativeStockConfirmation(Object error) =>
    error is ApiException &&
    error.code == 'inventory.insufficient_stock' &&
    error.problem?.extensions['confirmationRequired'] == true;

/// Wait before the single automatic retry of `inventory.busy`.
Duration busyRetryDelay(ApiException error) {
  final wait = error.retryAfter ?? const Duration(seconds: 1);
  const max = Duration(seconds: 5);
  return wait > max ? max : wait;
}

/// `inventory.busy` (409, stock rows locked by another operation): waits
/// `Retry-After` (≤ 5 s) and sends once more. [send] must reuse its
/// Idempotency-Key (`IdempotentAction.run`), so a first attempt that did
/// commit is replayed, not applied twice. A second `busy` is rethrown.
Future<T> withBusyRetry<T>(
  Future<T> Function() send, {
  required Future<void> Function(Duration) wait,
  void Function()? onRetry,
}) async {
  try {
    return await send();
  } on ApiException catch (error) {
    if (!isInventoryBusy(error)) rethrow;
    onRetry?.call();
    await wait(busyRetryDelay(error));
    return await send();
  }
}

/// Injected delay (tests replace it with an immediate one).
final stockDelayProvider = Provider<Future<void> Function(Duration)>(
  (ref) => Future<void>.delayed,
);

/// Turkish message for a failed stock call: inventory / warehouse problem
/// codes first, then the app-wide mapping. [forbidden] replaces the generic
/// 403 text with the action's own ("Mal kabul yetkiniz yok…").
String stockErrorMessage(
  AppLocalizations l10n,
  Object error, {
  String? forbidden,
}) {
  if (error is! ApiException) return errorMessage(l10n, error);
  switch (error.code) {
    case 'inventory.busy':
      return l10n.stockErrorBusy;
    case 'inventory.insufficient_available':
      return l10n.stockErrorInsufficientAvailable;
    case 'inventory.insufficient_stock':
      return l10n.stockErrorInsufficientStock;
    case 'inventory.search_too_broad':
      return l10n.stockErrorSearchTooBroad;
    case 'inventory.duplicate_source':
      return l10n.stockErrorDuplicateSource;
    case 'warehouse.locations_disabled':
      return l10n.stockErrorLocationsDisabled;
  }
  if (error.kind == ApiErrorKind.forbidden && forbidden != null) {
    return forbidden;
  }
  if (error.kind == ApiErrorKind.validation) return l10n.stockErrorValidation;
  if (error.kind == ApiErrorKind.conflict) return l10n.stockErrorConflict;
  return errorMessage(l10n, error);
}

/// API validation messages (English developer text) → Turkish, for the
/// field errors of a 422 (`lines[0].quantity`, `reason`, …).
String stockFieldMessage(AppLocalizations l10n, String? message) {
  final text = message ?? '';
  if (text.startsWith('A location is required')) {
    return l10n.stockFieldLocationRequired;
  }
  if (text.startsWith('Location')) return l10n.stockFieldLocationInvalid;
  if (text.contains('does not allow fractions')) {
    return l10n.stockFieldNoFractions;
  }
  if (text.startsWith('Variant is not active')) {
    return l10n.stockFieldVariantInactive;
  }
  if (text.startsWith('Variant not found')) {
    return l10n.stockFieldVariantMissing;
  }
  if (text.startsWith('Must be greater than 0')) {
    return l10n.stockQuantityInvalid;
  }
  if (text.startsWith('A reason')) return l10n.stockReasonRequired;
  if (text.startsWith('Warehouse')) return l10n.stockFieldWarehouseInvalid;
  return l10n.errorValidation;
}

String quantityErrorMessage(AppLocalizations l10n, QuantityError error) =>
    switch (error) {
      QuantityError.empty => l10n.stockQuantityRequired,
      QuantityError.invalid => l10n.stockQuantityInvalid,
      QuantityError.tooManyDecimals => l10n.stockQuantityDecimals,
      QuantityError.notPositive => l10n.stockQuantityPositive,
      QuantityError.tooLarge => l10n.stockQuantityTooLarge,
    };
