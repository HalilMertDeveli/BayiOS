import 'package:bayios_mobile/l10n/app_localizations.dart';
import 'package:bayios_mobile/main.dart' as app;
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:integration_test/integration_test.dart';

/// Stock smoke on a device/emulator against a real stack (no mocks, #43):
/// sign in → stock query of a demo product → goods receipt of N units into a
/// bin → stock query shows the higher balance. It WRITES a receipt (ledger
/// movement): run it only against a local/demo tenant.
///
/// Codes are typed into the manual-entry field (the emulator camera cannot
/// read a real label); the camera screen is opened once and left with
/// "Kodu elle gir". Grant the camera permission first so no system dialog
/// blocks the run (`adb shell pm grant com.bayios.bayios_mobile
/// android.permission.CAMERA`).
///
///     flutter test integration_test/stock_smoke_test.dart -d emulator-5560 \
///       --dart-define-from-file=env/dev.json \
///       --dart-define-from-file=env/smoke.json \
///       --dart-define=BAYIOS_SMOKE_SKU=<sku> \
///       --dart-define=BAYIOS_SMOKE_BARCODE=<barcode> \
///       --dart-define=BAYIOS_SMOKE_BIN=<bin code> \
///       --dart-define=BAYIOS_SMOKE_QTY=4
///
/// `SHOT:<name>` lines mark moments for an external `adb screencap`.
const _email = String.fromEnvironment('BAYIOS_SMOKE_EMAIL');
const _password = String.fromEnvironment('BAYIOS_SMOKE_PASSWORD');
const _tenantName = String.fromEnvironment('BAYIOS_SMOKE_TENANT');
const _sku = String.fromEnvironment('BAYIOS_SMOKE_SKU');
const _barcode = String.fromEnvironment('BAYIOS_SMOKE_BARCODE');
const _bin = String.fromEnvironment('BAYIOS_SMOKE_BIN');
const _quantity = String.fromEnvironment('BAYIOS_SMOKE_QTY', defaultValue: '4');

void main() {
  IntegrationTestWidgetsFlutterBinding.ensureInitialized();
  final l10n = lookupAppLocalizations(const Locale('tr'));

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
    debugPrint('SHOT:timeout');
    await tester.pump(const Duration(seconds: 3));
    final texts = find
        .byType(Text)
        .evaluate()
        .map((e) => (e.widget as Text).data)
        .whereType<String>()
        .join(' | ');
    throw TestFailure('Timed out waiting for any of $finders; screen: $texts');
  }

  /// Marks a screenshot moment and holds the frame for the capture.
  Future<void> shot(WidgetTester tester, String name) async {
    await tester.pump(const Duration(milliseconds: 500));
    debugPrint('SHOT:$name');
    await tester.pump(const Duration(seconds: 3));
  }

  Future<void> tapVisible(WidgetTester tester, Finder finder) async {
    // Close the soft keyboard and center the target (not under the app bar).
    FocusManager.instance.primaryFocus?.unfocus();
    await tester.pump(const Duration(milliseconds: 800));
    await Scrollable.ensureVisible(tester.element(finder), alignment: 0.5);
    await tester.pump(const Duration(milliseconds: 300));
    await tester.tap(finder);
    await tester.pump(const Duration(milliseconds: 300));
  }

  Future<void> submitField(WidgetTester tester, Key key, String text) async {
    await tester.enterText(find.byKey(key), text);
    await tester.testTextInput.receiveAction(TextInputAction.search);
    await tester.pump(const Duration(milliseconds: 300));
  }

  testWidgets('stock query → goods receipt → balance increased', (
    tester,
  ) async {
    for (final (name, value) in [
      ('BAYIOS_SMOKE_EMAIL', _email),
      ('BAYIOS_SMOKE_PASSWORD', _password),
      ('BAYIOS_SMOKE_SKU', _sku),
      ('BAYIOS_SMOKE_BARCODE', _barcode),
      ('BAYIOS_SMOKE_BIN', _bin),
    ]) {
      expect(value, isNotEmpty, reason: '$name missing');
    }

    await app.main();
    final signIn = find.byKey(const Key('sign-in-submit'));
    final stockTab = find.byKey(const Key('nav-stock'));
    final picker = find.text(l10n.tenantPickerTitle);

    if (await waitForAny(tester, [signIn, stockTab]) == 0) {
      await tester.enterText(find.byKey(const Key('sign-in-email')), _email);
      await tester.enterText(
        find.byKey(const Key('sign-in-password')),
        _password,
      );
      await tester.tap(signIn);
      if (await waitForAny(tester, [stockTab, picker]) == 1) {
        await tester.tap(
          _tenantName.isEmpty
              ? find.byType(ListTile).first
              : find.widgetWithText(ListTile, _tenantName),
        );
        await waitForAny(tester, [stockTab]);
      }
    }

    // 1. Stock query before the receipt.
    await tester.tap(stockTab);
    await waitForAny(tester, [find.byKey(const Key('stock-idle'))]);
    await shot(tester, '01-stock-idle');
    await submitField(tester, const Key('stock-search-field'), _sku);
    await waitForAny(tester, [find.text(_sku)]);
    await shot(tester, '02-stock-before');

    // 2. Goods receipt.
    await tapVisible(tester, find.byKey(const Key('stock-receive')));
    final codeField = find.byKey(const Key('receiving-code-field'));
    await waitForAny(tester, [codeField]);

    // Camera path once, then the manual fallback.
    await tapVisible(tester, find.byKey(const Key('receiving-scan')));
    final manual = find.byKey(const Key('scanner-manual'));
    await waitForAny(tester, [manual]);
    await tester.pump(const Duration(seconds: 2));
    await shot(tester, '03-camera-scanner');
    await tester.tap(manual);
    await waitForAny(tester, [codeField]);

    // Scan mismatch: unknown code.
    await submitField(tester, const Key('receiving-code-field'), 'YOK-000');
    await waitForAny(tester, [
      find.byKey(const Key('receiving-scan-not-found')),
    ]);
    await shot(tester, '04-scan-mismatch');

    // Match by barcode, quantity, bin.
    await submitField(tester, const Key('receiving-code-field'), _barcode);
    await waitForAny(tester, [find.byKey(const Key('receiving-line-editor'))]);
    await tester.enterText(
      find.byKey(const Key('receiving-quantity')),
      _quantity,
    );
    final binField = find.byWidgetPredicate(
      (w) =>
          w is DropdownButtonFormField<String> &&
          (w.key?.toString().contains('receiving-bin-') ?? false),
    );
    await tapVisible(tester, binField);
    await waitForAny(tester, [find.text(_bin)]);
    await tester.tap(find.text(_bin).last);
    await tester.pump(const Duration(seconds: 1));
    await shot(tester, '05-receiving-line');

    await tapVisible(tester, find.byKey(const Key('receiving-add-line')));
    await waitForAny(tester, [find.textContaining('raf $_bin')]);
    await shot(tester, '06-receiving-lines');

    await tester.tap(find.byKey(const Key('receiving-submit')));
    await waitForAny(tester, [
      find.byKey(const Key('receiving-success')),
      find.byKey(const Key('receiving-failure')),
    ]);
    await tester.pump(const Duration(milliseconds: 500));
    await shot(tester, '07-receiving-result');
    expect(find.byKey(const Key('receiving-success')), findsOneWidget);

    // 3. Stock query after the receipt.
    await tapVisible(tester, find.byKey(const Key('receiving-show-stock')));
    await waitForAny(tester, [find.byKey(const Key('stock-search-field'))]);
    await waitForAny(tester, [find.textContaining(_bin)]);
    await shot(tester, '08-stock-after');
  });
}
