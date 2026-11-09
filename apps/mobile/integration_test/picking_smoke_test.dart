import 'dart:io';

import 'package:bayios_mobile/l10n/app_localizations.dart';
import 'package:bayios_mobile/main.dart' as app;
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:integration_test/integration_test.dart';

/// Picking smoke on a device/emulator against a real local stack (no mocks):
/// sign in → dashboard → Görevler → the task of [_order] → shelf + a wrong
/// product (warning, not counted) → a wrong shelf (warning) → the right
/// codes → complete. It WRITES (scans, completes) — run it only against a
/// demo tenant, for an order created for the smoke.
///
/// Codes are typed through the manual entry field: an emulator camera has no
/// real barcode in front of it. The right codes are read from the screen
/// (shelf code, "Barkod …" of the current line).
///
///     flutter drive -d emulator-5554 \
///       --driver=test_driver/integration_test.dart \
///       --target=integration_test/picking_smoke_test.dart \
///       --dart-define-from-file=env/dev.json \
///       --dart-define-from-file=env/smoke.json \
///       --dart-define=BAYIOS_SMOKE_ORDER=SIP-2026-000018 \
///       --dart-define=BAYIOS_SMOKE_WRONG_CODE=<a barcode of another product>
///
/// Screenshots go to `$BAYIOS_SCREENSHOT_DIR` (driver side).
const _email = String.fromEnvironment('BAYIOS_SMOKE_EMAIL');
const _password = String.fromEnvironment('BAYIOS_SMOKE_PASSWORD');
const _order = String.fromEnvironment('BAYIOS_SMOKE_ORDER');
const _wrongCode = String.fromEnvironment(
  'BAYIOS_SMOKE_WRONG_CODE',
  defaultValue: 'YANLIS-0000',
);

void main() {
  final binding = IntegrationTestWidgetsFlutterBinding.ensureInitialized();
  final l10n = lookupAppLocalizations(const Locale('tr'));
  var surfaceConverted = false;

  Future<int> waitForAny(
    WidgetTester tester,
    List<Finder> finders, {
    Duration timeout = const Duration(seconds: 30),
  }) async {
    final end = DateTime.now().add(timeout);
    while (DateTime.now().isBefore(end)) {
      await tester.pump(const Duration(milliseconds: 250));
      for (var i = 0; i < finders.length; i++) {
        if (finders[i].evaluate().isNotEmpty) return i;
      }
    }
    throw TestFailure('Timed out waiting for any of $finders');
  }

  Future<void> shot(WidgetTester tester, String name) async {
    await tester.pump(const Duration(milliseconds: 500));
    if (Platform.isAndroid && !surfaceConverted) {
      await binding.convertFlutterSurfaceToImage();
      surfaceConverted = true;
      await tester.pump();
    }
    await binding.takeScreenshot(name);
  }

  String feedback(WidgetTester tester) => tester
      .widgetList<Text>(
        find.descendant(
          of: find.byKey(const Key('pick-feedback')),
          matching: find.byType(Text),
        ),
      )
      .map((t) => t.data ?? '')
      .join(' ');

  Future<void> enterCode(WidgetTester tester, String code) async {
    await tester.enterText(find.byKey(const Key('pick-manual-code')), code);
    await tester.tap(find.byKey(const Key('pick-manual-submit')));
    // Wait for the answer (the feedback leaves "Gönderiliyor…").
    final end = DateTime.now().add(const Duration(seconds: 20));
    do {
      await tester.pump(const Duration(milliseconds: 250));
    } while (feedback(tester).contains(l10n.pickingSending) &&
        DateTime.now().isBefore(end));
    await tester.pump(const Duration(milliseconds: 300));
  }

  String currentText(WidgetTester tester, String key) =>
      tester.widget<Text>(find.byKey(Key(key))).data!;

  String currentBarcode(WidgetTester tester) {
    final prefix = l10n.pickingBarcode('');
    final text = tester
        .widgetList<Text>(
          find.descendant(
            of: find.byKey(const Key('pick-current')),
            matching: find.byType(Text),
          ),
        )
        .map((t) => t.data ?? '')
        .firstWhere((t) => t.startsWith(prefix));
    return text.substring(prefix.length);
  }

  testWidgets('pick a task with manual codes, wrong product and shelf', (
    tester,
  ) async {
    expect(_email, isNotEmpty, reason: 'BAYIOS_SMOKE_EMAIL missing');
    expect(_order, isNotEmpty, reason: 'BAYIOS_SMOKE_ORDER missing');

    await app.main();
    final signIn = find.byKey(const Key('sign-in-submit'));
    final dashboard = find.byKey(const Key('dashboard-tenant'));
    if (await waitForAny(tester, [signIn, dashboard]) == 1) {
      await tester.tap(find.byKey(const Key('nav-profile')));
      await waitForAny(tester, [find.byKey(const Key('profile-sign-out'))]);
      await tester.tap(find.byKey(const Key('profile-sign-out')));
      await waitForAny(tester, [signIn]);
    }
    await tester.enterText(find.byKey(const Key('sign-in-email')), _email);
    await tester.enterText(
      find.byKey(const Key('sign-in-password')),
      _password,
    );
    await tester.tap(signIn);
    await waitForAny(tester, [dashboard]);
    await waitForAny(tester, [
      find.byKey(const Key('dashboard-count-assigned')),
      find.byKey(const Key('dashboard-count-active')),
    ]);
    await shot(tester, '01-dashboard');

    // Görevler → the tab that lists the task (pool for a fresh order).
    await tester.tap(find.byKey(const Key('nav-tasks')));
    final card = find.ancestor(
      of: find.text(l10n.tasksOrder(_order)),
      matching: find.byType(Card),
    );
    var found = false;
    for (final tab in ['assigned', 'pool', 'all']) {
      final key = find.byKey(Key('tasks-tab-$tab'));
      if (key.evaluate().isEmpty) {
        await tester.pump(const Duration(seconds: 1));
        if (key.evaluate().isEmpty) continue;
      }
      await tester.tap(key);
      try {
        await waitForAny(tester, [card], timeout: const Duration(seconds: 8));
        found = true;
        break;
      } on TestFailure {
        continue;
      }
    }
    expect(found, isTrue, reason: 'task of $_order not listed');
    await shot(tester, '02-task-list');
    await tester.tap(card.first);
    await waitForAny(tester, [find.byKey(const Key('pick-current'))]);
    await waitForAny(tester, [find.byKey(const Key('pick-order'))]);
    await shot(tester, '03-pick-detail');

    String? firstShelf;
    var lineNo = 0;
    while (find.byKey(const Key('pick-current')).evaluate().isNotEmpty) {
      lineNo++;
      final shelf = currentText(tester, 'pick-current-location');
      final barcode = currentBarcode(tester);
      if (lineNo == 1) {
        firstShelf = shelf;
        await enterCode(tester, shelf);
        // Wrong product: warning, the line is not counted.
        final before = currentText(tester, 'pick-current-remaining');
        await enterCode(tester, _wrongCode);
        expect(
          feedback(tester),
          anyOf(
            contains(l10n.pickingRejectWrongItem),
            contains(l10n.pickingRejectUnknownCode),
          ),
        );
        expect(currentText(tester, 'pick-current-remaining'), before);
        await shot(tester, '04-wrong-product');
      } else if (firstShelf != null && firstShelf != shelf) {
        // Wrong bin: this line's product at the first line's shelf.
        await enterCode(tester, firstShelf);
        await enterCode(tester, barcode);
        expect(feedback(tester), contains(l10n.pickingRejectWrongLocation));
        await shot(tester, '06-wrong-bin');
        await enterCode(tester, shelf);
      } else {
        await enterCode(tester, shelf);
      }
      // Whole remaining quantity in one scan.
      final remaining = RegExp(r'(\d+(?:,\d+)?)')
          .firstMatch(currentText(tester, 'pick-current-remaining'))!
          .group(1)!;
      await tester.enterText(find.byKey(const Key('pick-qty')), remaining);
      await enterCode(tester, barcode);
      expect(feedback(tester), contains('+$remaining'));
      if (lineNo == 1) await shot(tester, '05-accepted');
    }

    await waitForAny(tester, [find.byKey(const Key('pick-complete'))]);
    await shot(tester, '07-all-picked');
    await tester.tap(find.byKey(const Key('pick-complete')));
    await waitForAny(tester, [find.byKey(const Key('pick-done'))]);
    expect(find.text(l10n.pickingDoneShipment), findsOneWidget);
    await shot(tester, '08-completed');
  });
}
