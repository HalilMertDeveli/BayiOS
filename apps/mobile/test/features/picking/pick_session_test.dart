import 'package:bayios_mobile/features/picking/pick_session.dart';
import 'package:bayios_mobile/features/picking/picking_models.dart';
import 'package:flutter_test/flutter_test.dart';

import 'fake_picking_server.dart';

PickingTask _task([FakePickingServer? server]) =>
    PickingTask.fromJson((server ?? FakePickingServer()).taskJson());

void main() {
  group('models', () {
    test('quantities and versions are read from numbers or strings', () {
      final task = _task();
      expect(task.task.version, 3);
      expect(task.lines.first.quantityToPick, 2); // sent as "2.0"
      expect(task.lines.first.locationCode, 'A-01-01');
      expect(task.etag, 'W/"3"');
    });

    test('applyScan merges only the changed line and the new version', () {
      final task = _task();
      final next = task.applyScan(
        PickScanResult.fromJson({
          'taskId': 'task-1',
          'status': 'in_progress',
          'version': '4',
          'lines': [
            {
              'id': 'line-1',
              'variantId': 'v',
              'sku': 'FLT-001',
              'locationId': 'loc-a',
              'quantityToPick': 2,
              'quantityPicked': 1,
              'quantityShort': 0,
              'quantityRemaining': 1,
            },
          ],
          'openLineCount': 2,
        }),
      );
      expect(next.task.version, 4);
      expect(next.task.status, 'in_progress');
      expect(next.task.quantityPicked, 1);
      expect(next.lines.first.quantityRemaining, 1);
      expect(next.lines.last.quantityRemaining, 1);
    });

    test('request bodies send whole numbers as integers', () {
      expect(
        const PickScanRequest(code: 'X', quantity: 2, location: 'L').toJson(),
        {'code': 'X', 'location': 'L', 'quantity': 2, 'lineId': null},
      );
      expect(
        const ReportShortageRequest(reason: 'damaged', quantity: 1.5).toJson(),
        {'quantity': 1.5, 'reason': 'damaged'},
      );
    });
  });

  group('readScan', () {
    final lines = _task().lines;

    test('a product of the task is an item', () {
      expect(readScan('8690001', lines, null), isA<ItemReading>());
      expect(readScan('flt-001', lines, null), isA<ItemReading>());
    });

    test('a shelf of the task is a known location', () {
      final reading = readScan(' LOC-B0203 ', lines, null)! as LocationReading;
      expect(reading.location.locationId, 'loc-b');
      expect(reading.location.label, 'B-02-03');
      final byCode = readScan('a-01-01', lines, null)! as LocationReading;
      expect(byCode.location.locationId, 'loc-a');
    });

    test('unknown code without a shelf is taken as an (unknown) shelf', () {
      final reading = readScan('LOC-Z9', lines, null)! as LocationReading;
      expect(reading.location.isKnown, isFalse);
    });

    test('unknown code with a shelf set is sent as an item', () {
      const shelf = ScannedLocation(
        code: 'LOC-A0101',
        locationId: 'loc-a',
        label: 'A-01-01',
      );
      expect(readScan('999', lines, shelf), isA<ItemReading>());
    });

    test('blank input is ignored', () {
      expect(readScan('   ', lines, null), isNull);
    });
  });

  group('current line and shelf', () {
    test('first open line in shelf order; preferred while open', () {
      final lines = _task().lines.reversed.toList();
      expect(currentLine(lines)!.id, 'line-1');
      expect(currentLine(lines, 'line-2')!.id, 'line-2');
    });

    test('pick step asks for the shelf first', () {
      final line = _task().lines.first;
      expect(pickStep(line, null), PickStep.location);
      expect(
        pickStep(
          line,
          const ScannedLocation(code: 'x', locationId: 'loc-a', label: 'x'),
        ),
        PickStep.item,
      );
      expect(pickStep(null, null), PickStep.done);
    });

    test('the shelf is kept only for a line on the same shelf', () {
      final lines = _task().lines;
      const shelf = ScannedLocation(
        code: 'LOC-A0101',
        locationId: 'loc-a',
        label: 'A-01-01',
      );
      expect(keepLocation(shelf, lines.first), shelf);
      expect(keepLocation(shelf, lines.last), isNull);
    });

    test('a wrong shelf is forgotten after the rejection', () {
      const shelf = ScannedLocation(code: 'x', label: 'x');
      expect(locationAfterRejection(PickingProblem.wrongLocation, shelf), null);
      expect(locationAfterRejection(PickingProblem.wrongItem, shelf), shelf);
    });

    test('totals', () {
      final totals = PickTotals.of(_task().lines);
      expect(totals.toPick, 3);
      expect(totals.openLines, 2);
      expect(totals.progress, 0);
    });
  });

  group('quantity', () {
    test('accepts integers and Turkish decimals', () {
      expect((parseQuantity('2') as QuantityOk).value, 2);
      expect((parseQuantity('1,5') as QuantityOk).value, 1.5);
      expect((parseQuantity(' 0.125 ') as QuantityOk).value, 0.125);
    });

    QuantityError? errorOf(String text, {double? max}) =>
        switch (parseQuantity(text, max: max)) {
          QuantityInvalid(:final error) => error,
          QuantityOk() => null,
        };

    test('rejects what the API would reject, and over-picks', () {
      expect(errorOf(''), QuantityError.empty);
      expect(errorOf('abc'), QuantityError.invalid);
      expect(errorOf('-1'), QuantityError.invalid);
      expect(errorOf('0'), QuantityError.notPositive);
      expect(errorOf('1,2345'), QuantityError.tooManyDecimals);
      expect(errorOf('3', max: 2), QuantityError.overOpen);
      expect(errorOf('2', max: 2), isNull);
    });

    test('format', () {
      expect(formatQuantity(2), '2');
      expect(formatQuantity(1.5), '1,5');
      expect(formatQuantity(0.125), '0,125');
    });
  });
}
