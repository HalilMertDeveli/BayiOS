import 'dart:io';

import 'package:bayios_mobile/l10n/app_localizations.dart';
import 'package:bayios_mobile/main.dart' as app;
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:integration_test/integration_test.dart';

/// Web ↔ mobile real flow (#45), phase 2 of 3 — on an emulator/device against
/// a real stack, no mocks. Phase 1 (apps/web/e2e-local/web-mobile-1-prepare)
/// created a fresh business, a warehouse_staff user and an order approved on
/// the web; phase 3 (web-mobile-2-verify) checks the result on the web.
///
/// As the STAFF user: sign in → dashboard (role) → stock of A shows the web
/// order's reservation and no "Düzelt" (no `inventory.adjust`) → goods receipt
/// of C (unknown code rejected) → the order's task in the pool → claim →
/// shelf A, unknown code, wrong product (C), A in two scans → wrong shelf for
/// B, then B → complete → sign out. Codes are typed into the manual-entry
/// field (an emulator camera has no barcode in front of it).
///
/// Every value comes from the define file phase 1 writes (git-ignored, with
/// per-run synthetic logins); run it through apps/mobile/tool/e2e.sh.
/// Screenshots go to `$BAYIOS_SCREENSHOT_DIR` (flutter drive, driver side).
const _email = String.fromEnvironment('BAYIOS_FLOW_EMAIL');
const _password = String.fromEnvironment('BAYIOS_FLOW_PASSWORD');
const _tenant = String.fromEnvironment('BAYIOS_FLOW_TENANT');
const _order = String.fromEnvironment('BAYIOS_FLOW_ORDER');
const _receiveQty = String.fromEnvironment('BAYIOS_FLOW_RECEIVE_QTY');
const _initialA = String.fromEnvironment('BAYIOS_FLOW_A_INITIAL');

class _Product {
  const _Product(this.sku, this.barcode, this.shelf, this.quantity);
  final String sku;
  final String barcode;
  final String shelf;
  final String quantity;
}

const _a = _Product(
  String.fromEnvironment('BAYIOS_FLOW_A_SKU'),
  String.fromEnvironment('BAYIOS_FLOW_A_BARCODE'),
  String.fromEnvironment('BAYIOS_FLOW_A_SHELF'),
  String.fromEnvironment('BAYIOS_FLOW_A_QTY'),
);
const _b = _Product(
  String.fromEnvironment('BAYIOS_FLOW_B_SKU'),
  String.fromEnvironment('BAYIOS_FLOW_B_BARCODE'),
  String.fromEnvironment('BAYIOS_FLOW_B_SHELF'),
  String.fromEnvironment('BAYIOS_FLOW_B_QTY'),
);
const _c = _Product(
  String.fromEnvironment('BAYIOS_FLOW_C_SKU'),
  String.fromEnvironment('BAYIOS_FLOW_C_BARCODE'),
  String.fromEnvironment('BAYIOS_FLOW_C_SHELF'),
  '0',
);

void main() {
  final binding = IntegrationTestWidgetsFlutterBinding.ensureInitialized();
  final l10n = lookupAppLocalizations(const Locale('tr'));
  var surfaceConverted = false;

  String screenTexts() => find
      .byType(Text)
      .evaluate()
      .map((e) => (e.widget as Text).data)
      .whereType<String>()
      .join(' | ');

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
    throw TestFailure(
      'Timed out waiting for any of $finders; screen: ${screenTexts()}',
    );
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

  /// All texts below [finder], in tree order, joined with `|`.
  String textsIn(Finder finder) => find
      .descendant(of: finder, matching: find.byType(Text))
      .evaluate()
      .map((e) => (e.widget as Text).data ?? '')
      .join('|');

  Finder keyPrefix(String prefix) => find.byWidgetPredicate(
    (w) =>
        w.key is ValueKey<String> &&
        (w.key! as ValueKey<String>).value.startsWith(prefix),
  );

  /// The balance row of [shelf] (warehouse · shelf) on the stock screen.
  Finder balanceAt(String shelf) => find.ancestor(
    of: find.textContaining('· $shelf'),
    matching: keyPrefix('stock-balance-'),
  );

  String figures(String onHand, String reserved, String available) => [
    l10n.stockOnHand,
    onHand,
    l10n.stockReserved,
    reserved,
    l10n.stockAvailable,
    available,
  ].join('|');

  String feedback(WidgetTester tester) =>
      textsIn(find.byKey(const Key('pick-feedback')));

  Finder pickScrollable() => find
      .descendant(
        of: find.byKey(const Key('pick-scroll')),
        matching: find.byType(Scrollable),
      )
      .first;

  Future<void> enterCode(WidgetTester tester, String code) async {
    // Keyboard closed, manual entry back in the upper part of the list (with
    // the feedback box above it) after reading the line card further down.
    FocusManager.instance.primaryFocus?.unfocus();
    await tester.pump(const Duration(milliseconds: 500));
    final field = find.byKey(const Key('pick-manual-code'));
    await tester.scrollUntilVisible(field, -200, scrollable: pickScrollable());
    await Scrollable.ensureVisible(tester.element(field), alignment: 0.4);
    await tester.pump(const Duration(milliseconds: 300));
    final before = feedback(tester);
    // Like a user: tap the field (focus + soft keyboard), type, "Gönder".
    await tester.tap(field);
    await tester.pump(const Duration(milliseconds: 800));
    await tester.enterText(field, code);
    await tester.pump(const Duration(milliseconds: 300));
    final submit = find.byKey(const Key('pick-manual-submit'));
    await Scrollable.ensureVisible(tester.element(submit), alignment: 0.4);
    await tester.pump(const Duration(milliseconds: 300));
    expect(
      tester.widget<TextField>(field).controller!.text,
      code,
      reason: 'typed into the manual entry field',
    );
    await tester.tap(submit);
    // Wait for the answer: the feedback box shows a new message that is not
    // "Gönderiliyor…" (every step of this flow answers differently).
    final end = DateTime.now().add(const Duration(seconds: 20));
    while (true) {
      await tester.pump(const Duration(milliseconds: 250));
      // The soft keyboard scrolls the focused field to the top of the list;
      // the feedback box right above it may then be outside the built area.
      final box = find.byKey(const Key('pick-feedback'));
      if (box.evaluate().isEmpty) {
        await tester.scrollUntilVisible(
          box,
          -100,
          scrollable: pickScrollable(),
        );
      }
      final text = feedback(tester);
      if (text.isNotEmpty &&
          text != before &&
          !text.contains(l10n.pickingSending)) {
        break;
      }
      if (DateTime.now().isAfter(end)) {
        throw TestFailure(
          'No scan answer for "$code" in 20 s; screen: ${screenTexts()}',
        );
      }
    }
    await tester.pump(const Duration(milliseconds: 300));
  }

  /// The pick screen is a ListView: with the keyboard open and a long
  /// rejection message the current-line card can sit below the built area.
  /// Close the keyboard and scroll it into view before reading it.
  Future<void> revealCurrent(WidgetTester tester) async {
    FocusManager.instance.primaryFocus?.unfocus();
    await tester.pump(const Duration(milliseconds: 800));
    await tester.scrollUntilVisible(
      find.byKey(const Key('pick-current-remaining')),
      200,
      scrollable: pickScrollable(),
    );
    await tester.pump(const Duration(milliseconds: 300));
  }

  String currentText(String key) =>
      (find.byKey(Key(key)).evaluate().single.widget as Text).data!;

  String currentBarcode() =>
      textsIn(find.byKey(const Key('pick-current')))
          .split('|')
          .firstWhere((t) => t.startsWith(l10n.pickingBarcode('')));

  Future<void> pickQuantity(WidgetTester tester, _Product p, String qty) async {
    await tester.enterText(find.byKey(const Key('pick-qty')), qty);
    await enterCode(tester, p.barcode);
    expect(feedback(tester), contains('+$qty'), reason: 'scan of ${p.sku}');
  }

  Future<void> scanShelf(WidgetTester tester, String shelf) async {
    await enterCode(tester, shelf);
    expect(feedback(tester), contains(l10n.pickingShelfScanned(shelf)));
  }

  testWidgets('web order → staff receives, claims and picks on mobile', (
    tester,
  ) async {
    for (final (name, value) in [
      ('BAYIOS_FLOW_EMAIL', _email),
      ('BAYIOS_FLOW_PASSWORD', _password),
      ('BAYIOS_FLOW_ORDER', _order),
      ('BAYIOS_FLOW_A_SKU', _a.sku),
      ('BAYIOS_FLOW_B_SHELF', _b.shelf),
      ('BAYIOS_FLOW_C_BARCODE', _c.barcode),
      ('BAYIOS_FLOW_RECEIVE_QTY', _receiveQty),
      ('BAYIOS_FLOW_A_INITIAL', _initialA),
    ]) {
      expect(value, isNotEmpty, reason: '$name missing (run tool/e2e.sh)');
    }
    // A is picked in two scans (1 + rest).
    expect(int.parse(_a.quantity), greaterThan(1));

    await app.main();
    final signIn = find.byKey(const Key('sign-in-submit'));
    final dashboard = find.byKey(const Key('dashboard-tenant'));
    final picker = find.text(l10n.tenantPickerTitle);
    final signOut = find.byKey(const Key('profile-sign-out'));

    Future<void> signOutViaProfile() async {
      await tester.tap(find.byKey(const Key('nav-profile')));
      await waitForAny(tester, [signOut]);
      await tester.tap(signOut);
      await waitForAny(tester, [signIn]);
    }

    // ---- 1. Sign in as the warehouse staff user (one membership).
    if (await waitForAny(tester, [signIn, dashboard]) == 1) {
      await signOutViaProfile();
    }
    await tester.enterText(find.byKey(const Key('sign-in-email')), _email);
    await tester.enterText(
      find.byKey(const Key('sign-in-password')),
      _password,
    );
    await tester.tap(signIn);
    if (await waitForAny(tester, [dashboard, picker]) == 1) {
      await tester.tap(find.widgetWithText(ListTile, _tenant));
      await waitForAny(tester, [dashboard]);
    }
    expect(textsIn(dashboard), contains(_tenant));
    expect(
      textsIn(find.byKey(const Key('dashboard-role'))),
      contains(l10n.roleWarehouseStaff),
    );
    // The web order's task waits in the pool.
    await waitForAny(tester, [
      find.byKey(const Key('dashboard-count-assigned')),
    ]);
    await shot(tester, '01-dashboard');

    // ---- 2. Stock of A: the web approval's reservation; no "Düzelt".
    await tester.tap(find.byKey(const Key('nav-stock')));
    await waitForAny(tester, [find.byKey(const Key('stock-idle'))]);
    await submitField(tester, const Key('stock-search-field'), _a.sku);
    await waitForAny(tester, [balanceAt(_a.shelf)]);
    final orderedA = int.parse(_a.quantity);
    final initialA = int.parse(_initialA);
    expect(
      textsIn(balanceAt(_a.shelf)),
      contains(figures(_initialA, '$orderedA', '${initialA - orderedA}')),
    );
    expect(
      keyPrefix('stock-adjust-'),
      findsNothing,
      reason: 'warehouse_staff has no inventory.adjust',
    );
    expect(find.byKey(const Key('stock-receive')), findsOneWidget);
    await shot(tester, '02-stock-reserved-no-adjust');

    // ---- 3. Goods receipt of C: unknown code rejected, then C on its shelf.
    await tapVisible(tester, find.byKey(const Key('stock-receive')));
    final codeField = find.byKey(const Key('receiving-code-field'));
    await waitForAny(tester, [codeField]);
    await submitField(
      tester,
      const Key('receiving-code-field'),
      'YOK-${_c.barcode}',
    );
    await waitForAny(tester, [
      find.byKey(const Key('receiving-scan-not-found')),
    ]);
    await submitField(tester, const Key('receiving-code-field'), _c.barcode);
    await waitForAny(tester, [find.byKey(const Key('receiving-line-editor'))]);
    await tester.enterText(
      find.byKey(const Key('receiving-quantity')),
      _receiveQty,
    );
    final binField = find.byWidgetPredicate(
      (w) =>
          w is DropdownButtonFormField<String> &&
          (w.key?.toString().contains('receiving-bin-') ?? false),
    );
    await tapVisible(tester, binField);
    await waitForAny(tester, [find.text(_c.shelf)]);
    await tester.tap(find.text(_c.shelf).last);
    await tester.pump(const Duration(seconds: 1));
    await tapVisible(tester, find.byKey(const Key('receiving-add-line')));
    await waitForAny(tester, [find.textContaining('raf ${_c.shelf}')]);
    await shot(tester, '03-receiving-line');
    await tester.tap(find.byKey(const Key('receiving-submit')));
    await waitForAny(tester, [
      find.byKey(const Key('receiving-success')),
      find.byKey(const Key('receiving-failure')),
    ]);
    expect(
      find.byKey(const Key('receiving-success')),
      findsOneWidget,
      reason: screenTexts(),
    );
    await tapVisible(tester, find.byKey(const Key('receiving-show-stock')));
    await waitForAny(tester, [balanceAt(_c.shelf)]);
    expect(
      textsIn(balanceAt(_c.shelf)),
      contains(figures(_receiveQty, '0', _receiveQty)),
    );
    await shot(tester, '04-received-balance');

    // ---- 4. The web order's task: pool tab → claim.
    await tester.tap(find.byKey(const Key('nav-tasks')));
    await waitForAny(tester, [find.byKey(const Key('tasks-tab-pool'))]);
    await tester.tap(find.byKey(const Key('tasks-tab-pool')));
    final card = find.ancestor(
      of: find.text(l10n.tasksOrder(_order)),
      matching: find.byType(Card),
    );
    await waitForAny(tester, [card]);
    await shot(tester, '05-pool');
    await tester.tap(card.first);
    await waitForAny(tester, [find.byKey(const Key('pick-claim'))]);
    expect(find.byKey(const Key('pick-readonly')), findsOneWidget);
    await waitForAny(tester, [find.byKey(const Key('pick-order'))]);
    await tapVisible(tester, find.byKey(const Key('pick-claim')));
    await waitForAny(tester, [find.byKey(const Key('pick-current'))]);
    expect(find.text(l10n.pickingClaimed), findsOneWidget);
    await shot(tester, '06-claimed');

    // ---- 5. Line 1 = A (first shelf in walking order).
    await revealCurrent(tester);
    expect(currentText('pick-current-location'), _a.shelf);
    expect(currentBarcode(), l10n.pickingBarcode(_a.barcode));
    final remainingA = currentText('pick-current-remaining');
    expect(remainingA, contains('$orderedA'));
    await scanShelf(tester, _a.shelf);
    await enterCode(tester, 'YOK-${_a.barcode}');
    expect(feedback(tester), contains(l10n.pickingRejectUnknownCode));
    await enterCode(tester, _c.barcode);
    expect(feedback(tester), contains(l10n.pickingRejectWrongItem));
    await shot(tester, '07-wrong-product');
    await revealCurrent(tester);
    expect(currentText('pick-current-remaining'), remainingA);
    await pickQuantity(tester, _a, '1');
    await revealCurrent(tester);
    expect(currentText('pick-current-location'), _a.shelf);
    expect(currentText('pick-current-remaining'), contains('${orderedA - 1}'));
    await pickQuantity(tester, _a, '${orderedA - 1}');

    // ---- 6. Line 2 = B: wrong shelf first (A's), then the right one.
    await revealCurrent(tester);
    expect(currentText('pick-current-location'), _b.shelf);
    expect(currentBarcode(), l10n.pickingBarcode(_b.barcode));
    final remainingB = currentText('pick-current-remaining');
    await scanShelf(tester, _a.shelf);
    await enterCode(tester, _b.barcode);
    expect(feedback(tester), contains(l10n.pickingRejectWrongLocation));
    await shot(tester, '08-wrong-shelf');
    await revealCurrent(tester);
    expect(currentText('pick-current-remaining'), remainingB);
    await scanShelf(tester, _b.shelf);
    await pickQuantity(tester, _b, _b.quantity);

    // ---- 7. Complete: the API turns the picks into a draft shipment.
    expect(find.byKey(const Key('pick-current')), findsNothing);
    await waitForAny(tester, [find.byKey(const Key('pick-complete'))]);
    await shot(tester, '09-all-picked');
    await tapVisible(tester, find.byKey(const Key('pick-complete')));
    await waitForAny(tester, [find.byKey(const Key('pick-done'))]);
    expect(find.text(l10n.pickingDoneShipment), findsOneWidget);
    await shot(tester, '10-completed');

    await signOutViaProfile();
  });
}
