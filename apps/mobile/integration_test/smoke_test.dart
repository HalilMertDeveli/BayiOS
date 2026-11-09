import 'package:bayios_mobile/l10n/app_localizations.dart';
import 'package:bayios_mobile/main.dart' as app;
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:integration_test/integration_test.dart';

/// End-to-end smoke on a device/emulator against a real stack (no mocks):
/// Supabase Auth sign-in → GET /api/v1/me → tenant → dashboard → sign-out.
///
/// Credentials come from a git-ignored define file, never from the repo:
///
///     flutter test integration_test/smoke_test.dart -d emulator-5554 \
///       --dart-define-from-file=env/dev.json \
///       --dart-define-from-file=env/smoke.json
///
/// See apps/mobile/README.md → "Emulator smoke".
const _email = String.fromEnvironment('BAYIOS_SMOKE_EMAIL');
const _password = String.fromEnvironment('BAYIOS_SMOKE_PASSWORD');

/// Optional: the business to pick when the user has several.
const _tenantName = String.fromEnvironment('BAYIOS_SMOKE_TENANT');

void main() {
  IntegrationTestWidgetsFlutterBinding.ensureInitialized();
  final l10n = lookupAppLocalizations(const Locale('tr'));

  /// Pumps until one of [finders] matches; returns its index.
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

  testWidgets('sign in, pick tenant, see dashboard, sign out', (tester) async {
    expect(_email, isNotEmpty, reason: 'BAYIOS_SMOKE_EMAIL missing');
    expect(_password, isNotEmpty, reason: 'BAYIOS_SMOKE_PASSWORD missing');

    await app.main();

    final signInButton = find.byKey(const Key('sign-in-submit'));
    final dashboard = find.byKey(const Key('dashboard-tenant'));
    final picker = find.text(l10n.tenantPickerTitle);
    final profileSignOut = find.byKey(const Key('profile-sign-out'));

    Future<void> signOutViaProfile() async {
      await tester.tap(find.byKey(const Key('nav-profile')));
      await waitForAny(tester, [profileSignOut]);
      await tester.tap(profileSignOut);
      await waitForAny(tester, [signInButton]);
    }

    // A session kept from an earlier run starts signed in: sign out first.
    if (await waitForAny(tester, [signInButton, dashboard]) == 1) {
      await signOutViaProfile();
    }

    await tester.enterText(find.byKey(const Key('sign-in-email')), _email);
    await tester.enterText(
      find.byKey(const Key('sign-in-password')),
      _password,
    );
    await tester.tap(signInButton);

    // One membership is selected automatically; several show the picker.
    if (await waitForAny(tester, [dashboard, picker]) == 1) {
      await tester.tap(
        _tenantName.isEmpty
            ? find.byType(ListTile).first
            : find.widgetWithText(ListTile, _tenantName),
      );
      await waitForAny(tester, [dashboard]);
    }

    expect(find.text(l10n.dashboardTitle), findsOneWidget);
    expect(find.byKey(const Key('dashboard-role')), findsOneWidget);
    // Leave the dashboard on screen for a moment (adb screenshot).
    await tester.pump(const Duration(seconds: 3));

    await signOutViaProfile();
  });
}
