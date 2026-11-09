import 'package:bayios_mobile/core/api/api_exception.dart';
import 'package:bayios_mobile/core/api/problem_details.dart';
import 'package:bayios_mobile/core/api/problem_messages.dart';
import 'package:bayios_mobile/core/auth/auth_repository.dart';
import 'package:bayios_mobile/l10n/app_localizations.dart';
import 'package:flutter/widgets.dart';
import 'package:flutter_test/flutter_test.dart';

void main() {
  final l10n = lookupAppLocalizations(const Locale('tr'));

  String message(int status, {String? code, Duration? retryAfter}) =>
      apiErrorMessage(
        l10n,
        ApiException(
          status: status,
          problem: code == null ? null : ProblemDetails(code: code),
          retryAfter: retryAfter,
        ),
      );

  group('problem → Turkish message', () {
    test('401', () {
      expect(message(401), l10n.errorUnauthorized);
      expect(message(401, code: 'auth.invalid_token'), l10n.errorUnauthorized);
      expect(l10n.errorUnauthorized, contains('yeniden giriş'));
    });

    test('403 tenant.access_denied vs other 403s', () {
      expect(
        message(403, code: 'tenant.access_denied'),
        l10n.errorTenantAccessDenied,
      );
      expect(l10n.errorTenantAccessDenied, contains('işletme'));
      expect(message(403, code: 'tenant.inactive'), l10n.errorTenantInactive);
      expect(message(403, code: 'auth.forbidden'), l10n.errorForbidden);
      expect(message(403), l10n.errorForbidden);
      expect(
        message(403, code: 'auth.disabled_by_tenant_rule'),
        l10n.errorDisabledByTenantRule,
      );
      expect(
        message(403, code: 'entitlement.required'),
        l10n.errorEntitlementRequired,
      );
    });

    test('429 rate_limit.exceeded with and without Retry-After', () {
      expect(
        message(
          429,
          code: 'rate_limit.exceeded',
          retryAfter: const Duration(seconds: 30),
        ),
        'Çok sayıda istek gönderildi. 30 saniye sonra tekrar deneyin.',
      );
      expect(
        message(429, retryAfter: const Duration(milliseconds: 1200)),
        contains('2 saniye'),
      );
      expect(message(429, code: 'rate_limit.exceeded'), l10n.errorRateLimited);
    });

    test('409: code first, then status', () {
      expect(message(409), l10n.errorConflict);
      expect(
        message(409, code: 'order.invalid_transition'),
        l10n.errorConflict,
      );
      expect(
        message(409, code: 'idempotency.in_progress'),
        l10n.errorIdempotencyInProgress,
      );
      expect(
        message(422, code: 'idempotency.key_reused'),
        l10n.errorIdempotencyKeyReused,
      );
      expect(message(412), l10n.errorVersionMismatch);
    });

    test('5xx never leaks details', () {
      for (final status in [500, 502, 503, 504]) {
        expect(message(status), l10n.errorServer);
      }
      expect(
        apiErrorMessage(
          l10n,
          ApiException(
            status: 500,
            problem: const ProblemDetails(
              code: 'server.error',
              detail: 'NpgsqlException: secret stack trace',
            ),
          ),
        ),
        l10n.errorServer,
      );
    });

    test('network and timeout', () {
      expect(apiErrorMessage(l10n, ApiException.network()), l10n.errorNetwork);
      expect(
        apiErrorMessage(l10n, ApiException.network(timeout: true)),
        l10n.errorTimeout,
      );
    });

    test('every kind has a non-empty Turkish message', () {
      for (final kind in ApiErrorKind.values) {
        final text = apiErrorMessage(
          l10n,
          ApiException(status: 400, kind: kind),
        );
        expect(text, isNotEmpty, reason: kind.name);
      }
    });

    test('auth failures', () {
      expect(
        errorMessage(
          l10n,
          const AuthFailure(AuthFailureKind.invalidCredentials),
        ),
        'E-posta adresi veya şifre hatalı.',
      );
      expect(
        errorMessage(l10n, const AuthFailure(AuthFailureKind.network)),
        l10n.authNetwork,
      );
      expect(errorMessage(l10n, StateError('x')), l10n.errorServer);
    });
  });

  group('classify', () {
    test('busy conflicts are transient', () {
      final busy = ApiException(
        status: 409,
        problem: const ProblemDetails(code: 'inventory.busy'),
      );
      expect(busy.kind, ApiErrorKind.conflict);
      expect(busy.isTransient, isTrue);
      expect(ApiException(status: 409).isTransient, isFalse);
      expect(ApiException(status: 403).isTransient, isFalse);
    });
  });

  group('parseRetryAfter', () {
    test('delay-seconds', () {
      expect(parseRetryAfter('12'), const Duration(seconds: 12));
      expect(parseRetryAfter(' 0 '), Duration.zero);
      expect(parseRetryAfter(null), isNull);
      expect(parseRetryAfter('soon'), isNull);
    });

    test('HTTP-date', () {
      final now = DateTime.utc(2026, 11, 8, 10);
      expect(
        parseRetryAfter('Sun, 08 Nov 2026 10:00:45 GMT', now: now),
        const Duration(seconds: 45),
      );
      expect(
        parseRetryAfter('Sun, 08 Nov 2026 09:00:00 GMT', now: now),
        Duration.zero,
      );
    });
  });

  group('ProblemDetails.fromJson', () {
    test('tolerates odd types', () {
      final p = ProblemDetails.fromJson({
        'status': '409',
        'code': 'inventory.busy',
        'errors': {'a': 'single'},
        'permission': 'orders.approve',
      });
      expect(p.status, 409);
      expect(p.errors['a'], ['single']);
      expect(p.extensionString('permission'), 'orders.approve');
    });
  });
}
