import 'package:bayios_mobile/core/api/api_exception.dart';
import 'package:bayios_mobile/core/api/problem_messages.dart';
import 'package:bayios_mobile/features/picking/pick_session.dart';
import 'package:bayios_mobile/l10n/app_localizations.dart';

/// `*.busy` lock conflicts (409 + Retry-After): retry the same request.
bool isBusy(Object error) =>
    error is ApiException &&
    error.status == 409 &&
    (error.code?.endsWith('.busy') ?? false);

/// 412 / `concurrency.version_mismatch`: someone changed the task.
bool isVersionConflict(Object error) =>
    error is ApiException && error.kind == ApiErrorKind.versionMismatch;

/// The request may not have reached the API (or the API was overloaded):
/// resend the same action (same Idempotency-Key).
bool isResendable(Object error) =>
    error is ApiException && (error.isTransient || isBusy(error));

bool isScanRejection(Object error) =>
    error is ApiException && PickingProblem.scanRejections.contains(error.code);

bool isStale(Object error) =>
    error is ApiException && PickingProblem.stale.contains(error.code);

/// Short title for a rejected scan (shown big, with the warning alert).
String rejectionTitle(AppLocalizations l10n, String? code) => switch (code) {
  PickingProblem.wrongItem => l10n.pickingRejectWrongItem,
  PickingProblem.wrongLocation => l10n.pickingRejectWrongLocation,
  PickingProblem.unknownLocation => l10n.pickingRejectUnknownLocation,
  PickingProblem.locationRequired => l10n.pickingRejectLocationRequired,
  PickingProblem.overPick => l10n.pickingRejectOverPick,
  _ => l10n.pickingRejectUnknownCode,
};

/// Picking problem code → Turkish "what happened + what to do" (same texts
/// as the web's apps/web/src/lib/fulfilment/errors.ts); everything else
/// falls back to the shared [errorMessage].
String pickingErrorMessage(AppLocalizations l10n, Object error) {
  if (error is! ApiException) return errorMessage(l10n, error);
  switch (error.code) {
    case PickingProblem.alreadyAssigned:
      return l10n.pickingErrorAlreadyAssigned;
    case PickingProblem.notAssignee:
      return l10n.pickingErrorNotAssignee;
    case PickingProblem.invalidTransition:
      return l10n.pickingErrorInvalidTransition;
    case PickingProblem.orderNotFulfillable:
      return l10n.pickingErrorOrderNotFulfillable;
    case 'picking.busy' || 'order.busy' || 'inventory.busy':
      return l10n.pickingErrorBusy;
    case PickingProblem.unknownCode:
      return l10n.pickingErrorUnknownCode;
    case PickingProblem.wrongItem:
      return l10n.pickingErrorWrongItem;
    case PickingProblem.locationRequired:
      return l10n.pickingErrorLocationRequired;
    case PickingProblem.unknownLocation:
      return l10n.pickingErrorUnknownLocation;
    case PickingProblem.wrongLocation:
      return l10n.pickingErrorWrongLocation;
    case PickingProblem.overPick:
      return l10n.pickingErrorOverPick;
    case PickingProblem.lineClosed:
      return l10n.pickingErrorLineClosed;
    case PickingProblem.shortageExceedsOpen:
      return l10n.pickingErrorShortageExceedsOpen;
    case PickingProblem.incomplete:
      return l10n.pickingErrorIncomplete;
  }
  if (error.kind == ApiErrorKind.validation) {
    final messages = error.fieldErrors.values.expand((m) => m);
    if (messages.any((m) => m.contains('does not allow fractions'))) {
      return l10n.pickingErrorFraction;
    }
  }
  if (error.kind == ApiErrorKind.notFound) return l10n.pickingErrorNotFound;
  return errorMessage(l10n, error);
}
