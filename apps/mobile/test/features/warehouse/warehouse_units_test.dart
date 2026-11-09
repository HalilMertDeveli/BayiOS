import 'package:bayios_mobile/core/api/api_exception.dart';
import 'package:bayios_mobile/core/api/problem_details.dart';
import 'package:bayios_mobile/features/stock/quantity.dart';
import 'package:bayios_mobile/features/warehouse/document_scan_controller.dart';
import 'package:bayios_mobile/features/warehouse/warehouse_errors.dart';
import 'package:bayios_mobile/features/warehouse/warehouse_models.dart';
import 'package:flutter_test/flutter_test.dart';

import '../../helpers/pump.dart';
import 'warehouse_harness.dart';

ApiException apiError(int status, String code) => ApiException(
  status: status,
  problem: ProblemDetails(code: code),
);

void main() {
  test('count quantities: 0 only in set mode', () {
    expect(parseCountQuantity('0').value, 0);
    expect(parseCountQuantity('2,5').value, 2.5);
    expect(parseCountQuantity('').error, QuantityError.empty);
    expect(parseQuantity('0').error, QuantityError.notPositive);
  });

  test('stale / busy / resendable classification', () {
    expect(
      isStaleDocument(apiError(412, 'concurrency.version_mismatch')),
      isTrue,
    );
    expect(
      isStaleDocument(apiError(409, 'transfer.invalid_transition')),
      isTrue,
    );
    expect(isStaleDocument(apiError(409, 'count.invalid_transition')), isTrue);
    expect(isStaleDocument(apiError(422, 'transfer.wrong_item')), isFalse);
    expect(isDocumentBusy(apiError(409, 'count.busy')), isTrue);
    expect(isResendable(ApiException.network()), isTrue);
    expect(isResendable(apiError(422, 'transfer.over_receipt')), isFalse);
    expect(isScanRejection(apiError(422, 'transfer.over_receipt')), isTrue);
  });

  test('every handheld problem code has its own Turkish message', () {
    final expected = {
      'transfer.over_receipt': l10n.docErrorOverReceipt,
      'transfer.unknown_code': l10n.docErrorUnknownCode,
      'transfer.wrong_item': l10n.docErrorWrongItem,
      'transfer.unknown_location': l10n.docErrorUnknownLocationTransfer,
      'transfer.invalid_transition': l10n.docErrorTransferChanged,
      'transfer.warehouse_inactive': l10n.docErrorWarehouseInactive,
      'transfer.busy': l10n.docErrorBusy,
      'count.unknown_code': l10n.docErrorUnknownCode,
      'count.unknown_location': l10n.docErrorUnknownLocationCount,
      'count.out_of_scope': l10n.docErrorOutOfScope,
      'count.invalid_transition': l10n.docErrorCountChanged,
      'count.nothing_counted': l10n.docErrorNothingCounted,
      'count.busy': l10n.docErrorBusy,
      'concurrency.version_mismatch': l10n.docErrorVersionMismatch,
      'inventory.insufficient_available': l10n.stockErrorInsufficientAvailable,
    };
    for (final MapEntry(key: code, value: message) in expected.entries) {
      expect(documentErrorMessage(l10n, apiError(422, code)), message);
    }
  });

  test('models read the contract shapes (numbers as strings too)', () {
    final detail = TransferDetail.fromJson(transferJson(receivedA: 6));
    expect(detail.transfer.receivable, isTrue);
    expect(detail.transfer.version, 3);
    expect(detail.sortedLines.first.sku, 'BLT-002');
    expect(detail.sortedLines.last.done, isTrue);

    final json = countJson(countedA: 4)
      ..['count'] = {
        ...countSummaryJson(counted: 1),
        'lineCount': '2',
        'version': '9',
      };
    final count = CountDetail.fromJson(json);
    expect(count.count.lineCount, 2);
    expect(count.count.version, 9);
    expect(count.countedLines.single.countedQuantity, 4);
  });
}
