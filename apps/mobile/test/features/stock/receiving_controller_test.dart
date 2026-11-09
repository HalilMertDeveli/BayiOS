import 'package:bayios_mobile/features/stock/quantity.dart';
import 'package:bayios_mobile/features/stock/receiving_controller.dart';
import 'package:dio/dio.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';

import '../../helpers/fakes.dart';
import 'stock_harness.dart';

void main() {
  late RoutingAdapter api;
  late ProviderContainer container;
  late List<Duration> waits;

  setUp(() {
    api = RoutingAdapter();
    defaultRoutes(api);
    waits = [];
  });

  tearDown(() => container.dispose());

  Future<ReceivingController> start() async {
    // The listener keeps the auto-dispose provider alive for the test.
    container = ProviderContainer(
      overrides: stockOverrides(api, waits: waits),
      retry: (_, _) => null,
    )..listen(receivingControllerProvider, (_, _) {});
    final controller = container.read(receivingControllerProvider.notifier);
    await pumpEventQueue();
    return controller;
  }

  ReceivingState state() => container.read(receivingControllerProvider);

  Future<void> addLine(
    ReceivingController controller, {
    String code = '8690000000011',
    String quantity = '5',
    String? bin = binA,
  }) async {
    await controller.lookup(code);
    expect(controller.addCurrent(quantity, bin).ok, isTrue);
  }

  group('setup', () {
    test('loads warehouses and the bins of the default one', () async {
      await start();
      expect(state().loading, isFalse);
      expect(state().warehouse?.id, warehouseId);
      expect(state().usesLocations, isTrue);
      expect(state().locations.map((l) => l.code), ['A-01-01', 'B-02-03']);
    });

    test('load failure is shown and retried', () async {
      api.once(
        'GET',
        '/api/v1/warehouses',
        (_) => FakeReply.fail(DioExceptionType.connectionError),
      );
      final controller = await start();
      expect(state().loadError, isNotNull);
      await controller.load();
      expect(state().loadError, isNull);
      expect(state().warehouse, isNotNull);
    });

    test('tenant without warehouses posts without warehouseId', () async {
      api.on(
        'GET',
        '/api/v1/warehouses',
        (_) => FakeReply(200, json: {'items': <Object>[]}),
      );
      final controller = await start();
      expect(state().warehouse, isNull);
      await addLine(controller, bin: null);
      await controller.submit();
      final body =
          api.callsTo('POST', '/api/v1/inventory/receipts').single.body!
              as Map<String, Object?>;
      expect(body['warehouseId'], isNull);
    });
  });

  group('scan', () {
    test('barcode match → product waits for quantity and bin', () async {
      final controller = await start();
      await controller.lookup(' 8690000000011 ');
      expect(state().scan, isA<ScanMatched>());
      expect(state().current?.sku, 'FLT-001');
    });

    test('falls back to an exact SKU when the barcode is unknown', () async {
      final controller = await start();
      await controller.lookup('FLT-001');
      expect(state().current?.id, variantId);
      expect(
        api.callsTo('GET', '/api/v1/variants').single.query['sku'],
        'FLT-001',
      );
    });

    test('mismatch → not found, nothing to add', () async {
      final controller = await start();
      await controller.lookup('0000');
      expect(state().scan, isA<ScanNotFound>());
      expect((state().scan! as ScanNotFound).code, '0000');
      expect(state().current, isNull);
      expect(controller.addCurrent('1', binA).ok, isTrue);
      expect(state().lines, isEmpty);
    });

    test('inactive product cannot be received', () async {
      api.on(
        'GET',
        '/api/v1/variants/lookup',
        (_) => FakeReply(200, json: variantJson(status: 'archived')),
      );
      final controller = await start();
      await controller.lookup('8690000000011');
      expect(state().scan, isA<ScanInactive>());
      expect(state().current, isNull);
    });

    test('lookup failure is reported', () async {
      api.on(
        'GET',
        '/api/v1/variants/lookup',
        (_) => FakeReply.fail(DioExceptionType.connectionTimeout),
      );
      final controller = await start();
      await controller.lookup('8690000000011');
      expect(state().scan, isA<ScanFailed>());
    });

    test('bin label matches barcode or code, case-insensitive', () async {
      final controller = await start();
      expect(controller.findBin('loc-a0101')?.id, binA);
      expect(controller.findBin('b-02-03')?.id, binB);
      expect(controller.findBin('Z-99'), isNull);
    });
  });

  group('quantity and bin validation', () {
    test('rejects bad quantities', () async {
      final controller = await start();
      await controller.lookup('8690000000011');
      for (final (input, error) in [
        ('', QuantityError.empty),
        ('abc', QuantityError.invalid),
        ('1.250,5', QuantityError.invalid),
        ('0', QuantityError.notPositive),
        ('1,2345', QuantityError.tooManyDecimals),
        ('9999999999999999', QuantityError.tooLarge),
      ]) {
        expect(
          controller.addCurrent(input, binA).quantity,
          error,
          reason: input,
        );
      }
      expect(state().lines, isEmpty);
      expect(state().current, isNotNull);
    });

    test('requires a bin of this warehouse', () async {
      final controller = await start();
      await controller.lookup('8690000000011');
      expect(controller.addCurrent('2', null).locationMissing, isTrue);
      expect(controller.addCurrent('2', 'other-bin').locationMissing, isTrue);
      expect(state().lines, isEmpty);
    });

    test('Turkish decimal comma; same product + bin merges', () async {
      final controller = await start();
      await addLine(controller, quantity: '2,5');
      await addLine(controller, quantity: '1');
      await addLine(controller, quantity: '3', bin: binB);
      expect(state().lines, hasLength(2));
      expect(state().lines.first.quantity, 3.5);
      expect(state().total, 6.5);
    });

    test('warehouse cannot change while lines exist', () async {
      api.on(
        'GET',
        '/api/v1/warehouses',
        (_) => FakeReply(
          200,
          json: {
            'items': [
              warehouseJson(),
              warehouseJson(id: 'w-2', name: 'Şube', isDefault: false),
            ],
          },
        ),
      );
      final controller = await start();
      await addLine(controller);
      await controller.selectWarehouse('w-2');
      expect(state().warehouse?.id, warehouseId);
      controller.removeLine(state().lines.single.key);
      api.on(
        'GET',
        '/api/v1/warehouses/w-2/locations',
        (_) => FakeReply(200, json: {'items': <Object>[]}),
      );
      await controller.selectWarehouse('w-2');
      expect(state().warehouse?.id, 'w-2');
    });
  });

  group('submit', () {
    test('posts the ledger receipt and resets', () async {
      final controller = await start();
      controller.setReference('  IRS-2026-001 ');
      await addLine(controller);
      await controller.submit();

      final call = api.callsTo('POST', '/api/v1/inventory/receipts').single;
      expect(call.headers['X-Tenant-Id'], tenantId);
      expect(call.idempotencyKey, isNotEmpty);
      expect(call.body, {
        'warehouseId': warehouseId,
        'reference': 'IRS-2026-001',
        'lines': [
          {
            'variantId': variantId,
            'locationId': binA,
            'quantity': 5,
            'unitCost': null,
          },
        ],
      });
      final done = state().submission as SubmissionSucceeded;
      expect(done.lines, 1);
      expect(done.total, 5);
      expect(done.result.movements.single.balanceAfter, 17);
      expect(state().lines, isEmpty);
      expect(state().reference, '');
    });

    test(
      'network failure locks the lines; retry reuses the key once',
      () async {
        api.once(
          'POST',
          '/api/v1/inventory/receipts',
          (_) => FakeReply.fail(DioExceptionType.connectionError),
        );
        final controller = await start();
        await addLine(controller);
        await controller.submit();

        final failed = state().submission as SubmissionFailed;
        expect(failed.pending, isTrue);
        expect(state().locked, isTrue);
        // Locked: the request a retry sends must stay the same.
        controller.removeLine(state().lines.single.key);
        expect(state().lines, hasLength(1));
        await controller.lookup('8690000000011');
        expect(state().current, isNull);

        await controller.submit();
        final calls = api.callsTo('POST', '/api/v1/inventory/receipts');
        expect(calls, hasLength(2));
        expect(calls[1].idempotencyKey, calls[0].idempotencyKey);
        expect(calls[1].body, calls[0].body);
        expect(state().submission, isA<SubmissionSucceeded>());
      },
    );

    test('a new receipt after success gets a new key', () async {
      final controller = await start();
      await addLine(controller);
      await controller.submit();
      await addLine(controller);
      await controller.submit();
      final calls = api.callsTo('POST', '/api/v1/inventory/receipts');
      expect(calls[1].idempotencyKey, isNot(calls[0].idempotencyKey));
    });

    test('inventory.busy is retried once after Retry-After', () async {
      api.once(
        'POST',
        '/api/v1/inventory/receipts',
        (_) => FakeReply(
          409,
          json: problem(409, 'inventory.busy'),
          headers: {'Retry-After': '2'},
        ),
      );
      final controller = await start();
      await addLine(controller);
      await controller.submit();
      final calls = api.callsTo('POST', '/api/v1/inventory/receipts');
      expect(calls, hasLength(2));
      expect(calls[1].idempotencyKey, calls[0].idempotencyKey);
      expect(waits, [const Duration(seconds: 2)]);
      expect(state().submission, isA<SubmissionSucceeded>());
    });

    test('busy twice → pending conflict with retry', () async {
      FakeReply busy(Call _) =>
          FakeReply(409, json: problem(409, 'inventory.busy'));
      api
        ..once('POST', '/api/v1/inventory/receipts', busy)
        ..once('POST', '/api/v1/inventory/receipts', busy);
      final controller = await start();
      await addLine(controller);
      await controller.submit();
      final failed = state().submission as SubmissionFailed;
      expect(failed.pending, isTrue);
      final key = controller.pendingKey;
      await controller.submit();
      final calls = api.callsTo('POST', '/api/v1/inventory/receipts');
      expect(calls, hasLength(3));
      expect(calls.map((c) => c.idempotencyKey).toSet(), {key!.value});
    });

    test(
      '403 is final: lines stay editable, next try is a new action',
      () async {
        api.once(
          'POST',
          '/api/v1/inventory/receipts',
          (_) => FakeReply(403, json: problem(403, 'auth.forbidden')),
        );
        final controller = await start();
        await addLine(controller);
        await controller.submit();
        final failed = state().submission as SubmissionFailed;
        expect(failed.pending, isFalse);
        expect(state().locked, isFalse);
        expect(controller.pendingKey, isNull);
      },
    );

    test('422 field errors are attached to their lines', () async {
      api.once(
        'POST',
        '/api/v1/inventory/receipts',
        (_) => FakeReply(
          422,
          json: problem(
            422,
            'validation.failed',
            extra: {
              'errors': {
                'lines[1].quantity': [
                  "The variant's unit does not allow fractions.",
                ],
                'reference': ['Too long.'],
              },
            },
          ),
        ),
      );
      final controller = await start();
      await addLine(controller);
      await addLine(controller, quantity: '1,5', bin: binB);
      await controller.submit();
      final second = state().lines[1].key;
      expect(state().lineErrors.keys, [second]);
      expect(state().lineErrors[second]!.single, contains('fractions'));
      expect(state().formErrors, ['Too long.']);
      expect(state().locked, isFalse);
    });

    test('discarding an unknown outcome unlocks the lines', () async {
      api.once(
        'POST',
        '/api/v1/inventory/receipts',
        (_) => FakeReply(503, json: problem(503, 'server.unavailable')),
      );
      final controller = await start();
      await addLine(controller);
      await controller.submit();
      expect(state().locked, isTrue);
      controller.discardPending();
      expect(state().locked, isFalse);
      expect(state().submission, isA<SubmissionIdle>());
    });
  });
}
