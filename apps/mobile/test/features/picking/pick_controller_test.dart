import 'package:bayios_mobile/core/api/api_client.dart';
import 'package:bayios_mobile/features/picking/pick_controller.dart';
import 'package:bayios_mobile/features/picking/pick_session.dart';
import 'package:bayios_mobile/features/picking/picking_models.dart';
import 'package:dio/dio.dart';
import 'package:flutter_test/flutter_test.dart';

import '../../helpers/fakes.dart';
import '../../helpers/pump.dart';
import 'fake_picking_server.dart';

const _all = PickPermissions(
  pick: true,
  claim: true,
  supervisor: false,
  reportShortage: true,
);

void main() {
  late FakePickingServer server;
  late RecordingAlerts alerts;
  late int writes;

  setUp(() {
    server = FakePickingServer();
    alerts = RecordingAlerts();
    writes = 0;
  });

  PickController controllerFor({
    PickPermissions permissions = _all,
    String user = userId,
  }) {
    final c = PickController(
      api: pickingApiFor(server),
      task: PickingTask.fromJson(server.taskJson()),
      userId: user,
      permissions: permissions,
      l10n: l10n,
      alerts: alerts,
      onWrite: () => writes++,
      busyDelay: (_) => Duration.zero,
    );
    addTearDown(c.dispose);
    return c;
  }

  String? keyOf(RequestOptions r) =>
      r.headers[ApiClient.idempotencyHeader] as String?;

  group('scan', () {
    test('shelf then product counts the line', () async {
      final c = controllerFor();
      expect(c.step, PickStep.location);

      await c.handleCode('LOC-A0101');
      expect(c.location?.locationId, 'loc-a');
      expect(c.step, PickStep.item);
      expect(server.requests, isEmpty, reason: 'a shelf is local');

      await c.handleCode('8690001');
      expect(server.appliedScans, 1);
      expect(c.lines.first.quantityPicked, 1);
      expect(c.task.task.version, 4);
      expect(c.feedback?.tone, PickTone.success);
      expect(c.feedback?.title, l10n.pickingScanAccepted('FLT-001', '1'));
      expect(alerts.events, ['accepted', 'accepted']);
      expect(writes, 1);

      final scan = server.writes('/scans').single;
      expect(scan.data, contains('"location":"LOC-A0101"'));
      expect(scan.headers.containsKey('If-Match'), isFalse);
      expect(keyOf(scan), isNotEmpty);
    });

    test('wrong product: warning + alert, nothing counted', () async {
      final c = controllerFor();
      await c.handleCode('LOC-A0101');
      await c.handleCode('8690999'); // a product, but not on this task

      expect(server.appliedScans, 0);
      expect(c.lines.first.quantityPicked, 0);
      expect(c.feedback?.tone, PickTone.error);
      expect(c.feedback?.title, l10n.pickingRejectWrongItem);
      expect(c.feedback?.text, l10n.pickingErrorWrongItem);
      expect(alerts.events.last, 'rejected');
      expect(c.hasFailedScan, isFalse, reason: 'a rejection is not resent');
    });

    test('wrong shelf: warning, shelf forgotten, nothing counted', () async {
      final c = controllerFor();
      await c.handleCode('LOC-B0203'); // shelf of line 2 → focus moves
      expect(c.current?.id, 'line-2');
      c.focusLine('line-1');
      // Line 1's product while standing at line 2's shelf.
      await c.handleCode('LOC-B0203');
      await c.handleCode('8690001');

      expect(server.appliedScans, 0);
      expect(c.feedback?.title, l10n.pickingRejectWrongLocation);
      expect(c.location, isNull);
      expect(alerts.events.last, 'rejected');
    });

    test('a shelf that is not on the task warns at once', () async {
      final c = controllerFor();
      await c.handleCode('LOC-Z-99');
      expect(c.location?.isKnown, isFalse);
      expect(c.feedback?.tone, PickTone.warning);
      expect(alerts.events, ['rejected']);
    });

    test('quantity validation blocks the request', () async {
      final c = controllerFor();
      await c.handleCode('LOC-A0101');
      c.setQuantityText('3'); // only 2 open
      await c.handleCode('8690001');

      expect(server.requests, isEmpty);
      expect(c.quantityError, QuantityError.overOpen);
      expect(c.feedback?.text, l10n.pickingQuantityErrorOverOpen('2'));

      c.setQuantityText('2');
      expect(c.quantityError, isNull);
      await c.handleCode('8690001');
      expect(c.lines.first.quantityPicked, 2);
      expect(c.lines.first.isOpen, isFalse);
      expect(c.current?.id, 'line-2', reason: 'next line in shelf order');
      expect(c.quantityText, '1', reason: 'reset after a scan');
      expect(c.location, isNull, reason: 'line 2 is on another shelf');
    });

    test('stepper stays within 1 … open', () {
      final c = controllerFor()..incrementQuantity();
      expect(c.quantityText, '2');
      expect(c.canIncrement, isFalse);
      c.incrementQuantity();
      expect(c.quantityText, '2');
      c
        ..decrementQuantity()
        ..decrementQuantity();
      expect(c.quantityText, '1');
      expect(c.canDecrement, isFalse);
    });

    test('lost answer: resend reuses the key and counts once', () async {
      final c = controllerFor();
      await c.handleCode('LOC-A0101');
      server.dropNextAnswer = true;
      await c.handleCode('8690001');

      expect(server.appliedScans, 1, reason: 'the API applied it');
      expect(c.lines.first.quantityPicked, 0, reason: 'no answer yet');
      expect(c.hasFailedScan, isTrue);
      expect(c.feedback?.title, l10n.pickingScanNotSaved);
      final firstKey = keyOf(server.writes('/scans').single);
      expect(c.failedScanKey?.value, firstKey);

      await c.resend();
      final scans = server.writes('/scans');
      expect(scans, hasLength(2));
      expect(keyOf(scans[1]), firstKey, reason: 'same Idempotency-Key');
      expect(scans[1].data, scans[0].data, reason: 'same body');
      expect(server.appliedScans, 1, reason: 'replayed, not applied again');
      expect(c.lines.first.quantityPicked, 1);
      expect(c.hasFailedScan, isFalse);
    });

    test('a new scan after a success gets a new key', () async {
      final c = controllerFor();
      await c.handleCode('LOC-A0101');
      await c.handleCode('8690001');
      await c.handleCode('8690001');
      final keys = server.writes('/scans').map(keyOf).toList();
      expect(keys, hasLength(2));
      expect(keys[0], isNot(keys[1]));
      expect(server.appliedScans, 2);
    });

    test(
      'busy (409 + Retry-After) is retried once with the same key',
      () async {
        final c = controllerFor();
        await c.handleCode('LOC-A0101');
        server.overrides['POST ${FakePickingServer.taskPath}/scans'] = [
          () => FakePickingServer.problem(
            409,
            'picking.busy',
            headers: {'Retry-After': '1'},
          ),
        ];
        await c.handleCode('8690001');

        final scans = server.writes('/scans');
        expect(scans, hasLength(2));
        expect(keyOf(scans[0]), keyOf(scans[1]));
        expect(c.lines.first.quantityPicked, 1);
      },
    );

    test('offline (no response): resend offered', () async {
      final c = controllerFor();
      await c.handleCode('LOC-A0101');
      server.overrides['POST ${FakePickingServer.taskPath}/scans'] = [
        () => FakeReply.fail(DioExceptionType.connectionError),
      ];
      await c.handleCode('8690001');
      expect(c.hasFailedScan, isTrue);
      expect(c.feedback?.text, contains(l10n.pickingScanResendHint));
      await c.resend();
      expect(c.lines.first.quantityPicked, 1);
    });

    test('read-only users cannot scan', () async {
      final c = controllerFor(user: otherUserId);
      expect(c.canWork, isFalse);
      await c.handleCode('LOC-A0101');
      expect(c.location, isNull);
      expect(server.requests, isEmpty);
    });
  });

  group('complete', () {
    Future<PickController> picked() async {
      final c = controllerFor();
      await c.handleCode('LOC-A0101');
      c.setQuantityText('2');
      await c.handleCode('8690001');
      await c.handleCode('LOC-B0203');
      await c.handleCode('8690002');
      expect(c.totals.openLines, 0);
      return c;
    }

    test('sends If-Match of the version on screen', () async {
      final c = await picked();
      await c.complete();
      final request = server.writes('/complete').single;
      expect(request.headers['If-Match'], 'W/"5"');
      expect(c.task.task.status, 'completed');
      expect(c.task.shipmentId, 'ship-1');
      expect(c.notice?.title, l10n.pickingCompleted('SO-1001'));
    });

    test('412: conflict shown and the task reloaded', () async {
      final c = await picked();
      server.version = 9; // someone else changed the task
      await c.complete();

      expect(c.notice?.title, l10n.pickingConflictTitle);
      await pumpEventQueue();
      expect(
        server.requests.last.path,
        FakePickingServer.taskPath,
        reason: 'GET after the conflict',
      );
      expect(c.task.task.version, 9);
      // A retry on the fresh version is a new action (new key) and passes.
      await c.complete();
      final completes = server.writes('/complete');
      expect(completes, hasLength(2));
      expect(keyOf(completes[0]), isNot(keyOf(completes[1])));
      expect(completes[1].headers['If-Match'], 'W/"9"');
      expect(c.task.task.status, 'completed');
    });

    test('lost answer: the retry replays with the same key', () async {
      final c = await picked();
      server.dropNextAnswer = true;
      await c.complete();
      expect(c.notice?.tone, PickTone.error);
      await c.complete();
      final completes = server.writes('/complete');
      expect(keyOf(completes[0]), keyOf(completes[1]));
      expect(c.task.task.status, 'completed');
    });
  });

  group('shortage', () {
    test('reported for the open quantity; retry reuses the key', () async {
      final c = controllerFor();
      server.dropNextAnswer = true;
      final line = c.lines.last;
      final failed = await c.reportShortage(line, 'not_found', 1);
      expect(failed, isNotNull);

      final ok = await c.reportShortage(line, 'not_found', 1);
      expect(ok, isNull);
      final requests = server.writes('/shortage');
      expect(keyOf(requests[0]), keyOf(requests[1]));
      expect(c.lines.last.quantityShort, 1);
      expect(c.lines.last.isOpen, isFalse);
      expect(c.feedback?.title, l10n.pickingShortageReported('BRK-002', '1'));
    });

    test('a different quantity is a new action', () async {
      final c = controllerFor();
      server
          .overrides['POST ${FakePickingServer.taskPath}/lines/line-1/shortage'] = [
        () => FakePickingServer.problem(422, 'picking.shortage_exceeds_open'),
      ];
      final line = c.lines.first;
      expect(
        await c.reportShortage(line, 'damaged', 2),
        l10n.pickingErrorShortageExceedsOpen,
      );
      expect(await c.reportShortage(line, 'damaged', 1), isNull);
      final requests = server.writes('/shortage');
      expect(keyOf(requests[0]), isNot(keyOf(requests[1])));
    });
  });

  group('claim', () {
    test('pool task: claim makes it mine', () async {
      server.assignedTo = null;
      final c = controllerFor();
      expect(c.canWork, isFalse);
      expect(c.canClaim, isTrue);
      await c.claim();
      expect(c.mine, isTrue);
      expect(c.canWork, isTrue);
      expect(c.notice?.title, l10n.pickingClaimed);
    });

    test('lost race: clear message and reload', () async {
      server.assignedTo = null;
      final c = controllerFor();
      server.assignedTo = otherUserId;
      await c.claim();
      expect(c.notice?.text, l10n.pickingErrorAlreadyAssigned);
      await pumpEventQueue();
      expect(c.task.task.assignedTo, otherUserId);
      expect(c.canClaim, isFalse);
    });

    test('without tasks.claim no claim is offered', () {
      server.assignedTo = null;
      final c = controllerFor(
        permissions: const PickPermissions(
          pick: true,
          claim: false,
          supervisor: false,
          reportShortage: true,
        ),
      );
      expect(c.canClaim, isFalse);
    });

    test('a supervisor may work on any task', () {
      final c = controllerFor(
        user: otherUserId,
        permissions: const PickPermissions(
          pick: true,
          claim: false,
          supervisor: true,
          reportShortage: true,
        ),
      );
      expect(c.canWork, isTrue);
    });
  });
}
