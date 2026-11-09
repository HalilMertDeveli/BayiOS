import 'package:bayios_mobile/core/api/api_exception.dart';
import 'package:bayios_mobile/core/auth/auth_repository.dart';
import 'package:bayios_mobile/l10n/app_localizations.dart';

/// Turkish, user-facing message for an [ApiException]: by problem `code`
/// first, then by HTTP status (api-strategy §6; same order as the web's
/// apps/web/src/lib/api/problems.ts). `title`/`detail` are never shown.
String apiErrorMessage(AppLocalizations l10n, ApiException error) {
  switch (error.kind) {
    case ApiErrorKind.network:
      return l10n.errorNetwork;
    case ApiErrorKind.timeout:
      return l10n.errorTimeout;
    case ApiErrorKind.unauthorized:
      return l10n.errorUnauthorized;
    case ApiErrorKind.forbidden:
      return l10n.errorForbidden;
    case ApiErrorKind.tenantAccessDenied:
      return l10n.errorTenantAccessDenied;
    case ApiErrorKind.tenantInactive:
      return l10n.errorTenantInactive;
    case ApiErrorKind.disabledByTenantRule:
      return l10n.errorDisabledByTenantRule;
    case ApiErrorKind.entitlementRequired:
      return l10n.errorEntitlementRequired;
    case ApiErrorKind.entitlementLimit:
      return l10n.errorEntitlementLimit;
    case ApiErrorKind.notFound:
      return l10n.errorNotFound;
    case ApiErrorKind.malformed:
      return l10n.errorMalformed;
    case ApiErrorKind.validation:
      return l10n.errorValidation;
    case ApiErrorKind.conflict:
      return l10n.errorConflict;
    case ApiErrorKind.idempotencyInProgress:
      return l10n.errorIdempotencyInProgress;
    case ApiErrorKind.idempotencyKeyReused:
      return l10n.errorIdempotencyKeyReused;
    case ApiErrorKind.versionMismatch:
      return l10n.errorVersionMismatch;
    case ApiErrorKind.preconditionRequired:
      return l10n.errorPreconditionRequired;
    case ApiErrorKind.rateLimited:
      final wait = error.retryAfter;
      if (wait == null || wait == Duration.zero) return l10n.errorRateLimited;
      // Round up: "1 saniye" rather than "0 saniye" for 500 ms.
      final seconds = (wait.inMilliseconds / 1000).ceil();
      return l10n.errorRateLimitedRetryAfter(seconds);
    case ApiErrorKind.clientUpgradeRequired:
      return l10n.errorClientUpgradeRequired;
    case ApiErrorKind.server:
      return l10n.errorServer;
  }
}

/// Supabase Auth failure → Turkish message.
String authFailureMessage(AppLocalizations l10n, AuthFailure failure) =>
    switch (failure.kind) {
      AuthFailureKind.invalidCredentials => l10n.authInvalidCredentials,
      AuthFailureKind.emailNotConfirmed => l10n.authEmailNotConfirmed,
      AuthFailureKind.rateLimited => l10n.authRateLimited,
      AuthFailureKind.network => l10n.authNetwork,
      AuthFailureKind.sessionExpired => l10n.authSessionExpired,
      AuthFailureKind.unknown => l10n.authUnknown,
    };

/// Message for anything a screen may catch.
String errorMessage(AppLocalizations l10n, Object error) => switch (error) {
  final ApiException e => apiErrorMessage(l10n, e),
  final AuthFailure f => authFailureMessage(l10n, f),
  _ => l10n.errorServer,
};
