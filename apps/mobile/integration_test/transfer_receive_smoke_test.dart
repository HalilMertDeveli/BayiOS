import 'package:bayios_mobile/l10n/app_localizations.dart';
import 'package:bayios_mobile/main.dart' as app;
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:integration_test/integration_test.dart';

/// Transfer receipt smoke on a device/emulator against a real stack (#168,
/// no mocks): sign in → Depo → Gelen transferler → open the dispatched
/// transfer → wrong product refused → scan shelf + product with a quantity
/// → "Kaydedildi" and the received quantity goes up.
///
/// It WRITES (ledger `transfer_in` movement): run it only against a
/// local/demo tenant, with a transfer created and dispatched on the web
/// beforehand (Depo → Transferler → Yeni → Gönder) whose destination is a
/// warehouse the smoke user can receive into (`inventory.transfer.receive`,
/// e.g. warehouse_staff). Codes are typed into the manual-entry fields.
///
///     flutter test integration_test/transfer_receive_smoke_test.dart \
///       -d emulator-5554 \
///       --dart-define-from-file=env/dev.json \
///       --dart-define-from-file=env/smoke.json \
///       --dart-define=BAYIOS_SMOKE_TRANSFER=<TRF-… number> \
///       --dart-define=BAYIOS_SMOKE_BARCODE=<barcode or SKU on the transfer> \
///       --dart-define=BAYIOS_SMOKE_BIN=<destination shelf code> \
///       --dart-define=BAYIOS_SMOKE_QTY=1 \
///       --dart-define=BAYIOS_SMOKE_WRONG_CODE=<code of a product NOT on it>
///
/// `SHOT:<name>` lines mark moments for an external `adb screencap`.
const _email = String.fromEnvironment('BAYIOS_SMOKE_EMAIL');
const _password = String.fromEnvironment('BAYIOS_SMOKE_PASSWORD');
const _tenantName = String.fromEnvironment('BAYIOS_SMOKE_TENANT');
const _transfer = String.fromEnvironment('BAYIOS_SMOKE_TRANSFER');
const _barcode = String.fromEnvironment('BAYIOS_SMOKE_BARCODE');
const _bin = String.fromEnvironment('BAYIOS_SMOKE_BIN');
const _wrongCode = String.fromEnvironment('BAYIOS_SMOKE_WRONG_CODE');
const _quantity = String.fromEnvironment('BAYIOS_SMOKE_QTY', defaultValue: '1');

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
    final texts = find
        .byType(Text)
        .evaluate()
        .map((e) => (e.widget as Text).data)
        .whereType<String>()
        .join(' | ');
    throw TestFailure('Timed out waiting for any of $finders; screen: $texts');
  }

  Future<void> shot(WidgetTester tester, String name) async {
    await tester.pump(const Duration(milliseconds: 500));
    debugPrint('SHOT:$name');
    await tester.pump(const Duration(seconds: 2));
  }

  Future<void> tapVisible(WidgetTester tester, Finder finder) async {
    FocusManager.instance.primaryFocus?.unfocus();
    await tester.pump(const Duration(milliseconds: 800));
    await Scrollable.ensureVisible(tester.element(finder), alignment: 0.5);
    await tester.pump(const Duration(milliseconds: 300));
    await tester.tap(finder);
    await tester.pump(const Duration(milliseconds: 300));
  }

  Future<void> submitField(WidgetTester tester, Key key, String text) async {
    await Scrollable.ensureVisible(
      tester.element(find.byKey(key)),
      alignment: 0.5,
    );
    await tester.enterText(find.byKey(key), text);
    await tester.testTextInput.receiveAction(TextInputAction.search);
    await tester.pump(const Duration(milliseconds: 300));
  }

  testWidgets('incoming transfer → wrong item refused → partial receipt', (
    tester,
  ) async {
    for (final (name, value) in [
      ('BAYIOS_SMOKE_EMAIL', _email),
      ('BAYIOS_SMOKE_PASSWORD', _password),
      ('BAYIOS_SMOKE_TRANSFER', _transfer),
      ('BAYIOS_SMOKE_BARCODE', _barcode),
      ('BAYIOS_SMOKE_BIN', _bin),
    ]) {
      expect(value, isNotEmpty, reason: '$name missing');
    }

    await app.main();
    final signIn = find.byKey(const Key('sign-in-submit'));
    final warehouseTab = find.byKey(const Key('nav-warehouse'));
    final picker = find.text(l10n.tenantPickerTitle);

    if (await waitForAny(tester, [signIn, warehouseTab]) == 0) {
      await tester.enterText(find.byKey(const Key('sign-in-email')), _email);
      await tester.enterText(
        find.byKey(const Key('sign-in-password')),
        _password,
      );
      await tester.tap(signIn);
      if (await waitForAny(tester, [warehouseTab, picker]) == 1) {
        await tester.tap(
          _tenantName.isEmpty
              ? find.byType(ListTile).first
              : find.widgetWithText(ListTile, _tenantName),
        );
        await waitForAny(tester, [warehouseTab]);
      }
    }

    // 1. Depo → Gelen transferler.
    await tester.tap(warehouseTab);
    final transfers = find.byKey(const Key('warehouse-transfers'));
    await waitForAny(tester, [transfers]);
    await shot(tester, '01-warehouse-hub');
    await tapVisible(tester, transfers);
    final card = find.text(_transfer);
    final dropdown = find.byKey(const Key('transfer-warehouse'));
    final found = await waitForAny(tester, [
      card,
      find.byKey(const Key('transfer-empty')),
    ]);
    if (found == 1 && dropdown.evaluate().isNotEmpty) {
      // Destination is not the default warehouse: list every warehouse.
      await tapVisible(tester, dropdown);
      await tester.tap(find.text(l10n.transferAllWarehouses).last);
      await waitForAny(tester, [card]);
    }
    await shot(tester, '02-incoming-transfers');

    // 2. Open the transfer.
    await tapVisible(tester, card);
    final codeField = find.byKey(const Key('transfer-code-field'));
    await waitForAny(tester, [codeField]);
    await shot(tester, '03-transfer-detail');

    // 3. Shelf, then a product that is not on the transfer → refused.
    await submitField(tester, const Key('transfer-shelf-field'), _bin);
    await waitForAny(tester, [find.text(l10n.docShelfCurrent(_bin))]);
    if (_wrongCode.isNotEmpty) {
      await submitField(tester, const Key('transfer-code-field'), _wrongCode);
      await waitForAny(tester, [find.byKey(const Key('transfer-rejected'))]);
      expect(find.text(l10n.docErrorWrongItem), findsOneWidget);
      await shot(tester, '04-wrong-item');
    }

    // 4. Quantity + the right product → received.
    await tester.enterText(
      find.byKey(const Key('transfer-quantity')),
      _quantity,
    );
    await submitField(tester, const Key('transfer-code-field'), _barcode);
    final outcome = await waitForAny(tester, [
      find.byKey(const Key('transfer-accepted')),
      find.byKey(const Key('transfer-rejected')),
      find.byKey(const Key('transfer-pending')),
    ]);
    await shot(tester, '05-received');
    expect(outcome, 0, reason: 'the receipt was not accepted');
  });
}
