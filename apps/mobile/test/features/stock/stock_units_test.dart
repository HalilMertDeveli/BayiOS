import 'package:bayios_mobile/app/router.dart';
import 'package:bayios_mobile/app/shell.dart';
import 'package:bayios_mobile/core/api/api_exception.dart';
import 'package:bayios_mobile/core/api/problem_details.dart';
import 'package:bayios_mobile/core/session/access.dart';
import 'package:bayios_mobile/core/session/me.dart';
import 'package:bayios_mobile/core/session/session_controller.dart';
import 'package:bayios_mobile/features/profile/profile_feature.dart';
import 'package:bayios_mobile/features/stock/quantity.dart';
import 'package:bayios_mobile/features/stock/stock_errors.dart';
import 'package:bayios_mobile/features/stock/stock_feature.dart';
import 'package:bayios_mobile/features/stock/stock_models.dart';
import 'package:flutter_test/flutter_test.dart';

import '../../helpers/fakes.dart';
import '../../helpers/pump.dart';

ApiException apiError(
  int status,
  String? code, {
  Duration? retryAfter,
  Map<String, Object?> extensions = const {},
}) => ApiException(
  status: status,
  retryAfter: retryAfter,
  problem: ProblemDetails(code: code, extensions: extensions),
);

void main() {
  group('parseQuantity', () {
    test('accepts integers and one decimal separator', () {
      expect(parseQuantity('12').value, 12);
      expect(parseQuantity(' 12,5 ').value, 12.5);
      expect(parseQuantity('0.125').value, 0.125);
      expect(parseQuantity('12').value, isA<int>());
    });

    test('rejects the rest', () {
      expect(parseQuantity('').error, QuantityError.empty);
      expect(parseQuantity('-1').error, QuantityError.invalid);
      expect(parseQuantity('1.000,5').error, QuantityError.invalid);
      expect(parseQuantity('0,000').error, QuantityError.notPositive);
      expect(parseQuantity('1,0001').error, QuantityError.tooManyDecimals);
    });

    test('adds without float noise; formats Turkish', () {
      expect(addQuantities(0.1, 0.2), 0.3);
      expect(addQuantities(1, 2), isA<int>());
      expect(formatQuantity(1250.5), '1.250,5');
      expect(quantityText(12.5), '12,5');
      expect(readQuantity('3.500'), 3.5);
      expect(readQuantity(7), 7);
      expect(readOptionalQuantity(null), isNull);
    });
  });

  group('stockErrorMessage', () {
    test('inventory codes first', () {
      expect(
        stockErrorMessage(l10n, apiError(409, 'inventory.busy')),
        l10n.stockErrorBusy,
      );
      expect(
        stockErrorMessage(l10n, apiError(409, 'inventory.duplicate_source')),
        l10n.stockErrorDuplicateSource,
      );
      expect(
        stockErrorMessage(
          l10n,
          apiError(422, 'inventory.insufficient_available'),
        ),
        l10n.stockErrorInsufficientAvailable,
      );
    });

    test('409 and 412 conflicts are explained', () {
      expect(
        stockErrorMessage(l10n, apiError(409, 'something.conflict')),
        l10n.stockErrorConflict,
      );
      expect(
        stockErrorMessage(l10n, apiError(412, null)),
        l10n.errorVersionMismatch,
      );
    });

    test('403: action-specific text, tenant codes stay generic', () {
      expect(
        stockErrorMessage(
          l10n,
          apiError(403, 'auth.forbidden'),
          forbidden: l10n.receivingForbidden,
        ),
        l10n.receivingForbidden,
      );
      expect(
        stockErrorMessage(
          l10n,
          apiError(403, 'tenant.access_denied'),
          forbidden: l10n.receivingForbidden,
        ),
        l10n.errorTenantAccessDenied,
      );
    });

    test('negative stock confirmation is detected', () {
      expect(
        needsNegativeStockConfirmation(
          apiError(
            409,
            'inventory.insufficient_stock',
            extensions: {'confirmationRequired': true},
          ),
        ),
        isTrue,
      );
      expect(
        needsNegativeStockConfirmation(
          apiError(409, 'inventory.insufficient_stock'),
        ),
        isFalse,
      );
    });

    test('API field messages are translated', () {
      expect(
        stockFieldMessage(l10n, 'A location is required in this warehouse.'),
        l10n.stockFieldLocationRequired,
      );
      expect(
        stockFieldMessage(l10n, "The variant's unit does not allow fractions."),
        l10n.stockFieldNoFractions,
      );
      expect(stockFieldMessage(l10n, 'Unknown'), l10n.errorValidation);
    });
  });

  group('withBusyRetry', () {
    test('retries busy once after Retry-After (capped at 5 s)', () async {
      final waits = <Duration>[];
      var attempts = 0;
      final result = await withBusyRetry(() async {
        attempts++;
        if (attempts == 1) {
          throw apiError(
            409,
            'inventory.busy',
            retryAfter: const Duration(seconds: 30),
          );
        }
        return 'ok';
      }, wait: (d) async => waits.add(d));
      expect(result, 'ok');
      expect(attempts, 2);
      expect(waits, [const Duration(seconds: 5)]);
    });

    test('other errors are not retried', () async {
      var attempts = 0;
      await expectLater(
        withBusyRetry<void>(() async {
          attempts++;
          throw apiError(403, 'auth.forbidden');
        }, wait: (_) async {}),
        throwsA(isA<ApiException>()),
      );
      expect(attempts, 1);
    });
  });

  group('feature visibility (GET /me permissions)', () {
    SessionReady ready(List<String> permissions) {
      final me = meFixture(
        memberships: [membershipJson('t1')],
        activeTenant: activeTenantJson('t1', permissions: permissions),
      );
      return SessionReady(
        me: me,
        memberships: me.memberships,
        membership: me.memberships.single,
        activeTenant: me.activeTenant!,
      );
    }

    final features = [stockFeature, profileFeature];

    test('stock tab with inventory.read; sub-routes follow it', () {
      final session = ready(['tenant.read', 'inventory.read']);
      expect(visibleFeatures(features, session.access), contains(stockFeature));
      expect(redirectFor(session, '/stok/mal-kabul', features), isNull);
    });

    test('without inventory.read the tab and its links are hidden', () {
      final session = ready(['tenant.read']);
      expect(
        visibleFeatures(features, session.access),
        isNot(contains(stockFeature)),
      );
      expect(redirectFor(session, '/stok/mal-kabul', features), '/profil');
    });

    test('actions come from permission ids, never role names', () {
      final managerByName = Access([
        const PermissionGrant(
          permission: 'inventory.read',
          scope: PermissionScope.all,
        ),
      ]);
      expect(managerByName.can(StockPermissions.adjust), isFalse);
      expect(managerByName.can(StockPermissions.receive), isFalse);
    });
  });

  test('location label matching ignores case and spaces', () {
    const bin = StorageLocation(
      id: 'l',
      warehouseId: 'w',
      code: 'A-01',
      barcode: 'LOC-A01',
      isActive: true,
    );
    expect(bin.matches(' loc-a01 '), isTrue);
    expect(bin.matches('a-01'), isTrue);
    expect(bin.matches(''), isFalse);
  });
}
