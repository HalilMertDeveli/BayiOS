import 'package:bayios_mobile/core/api/endpoint.dart';
import 'package:bayios_mobile/core/providers.dart';
import 'package:bayios_mobile/core/push/push_registration.dart';
import 'package:bayios_mobile/core/push/push_service.dart';
import 'package:bayios_mobile/l10n/app_localizations.dart';
import 'package:bayios_mobile/main.dart' as app;
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:integration_test/integration_test.dart';

/// Device-registration smoke (#44) on an emulator against a real local stack
/// with push **disabled** (no Firebase project exists): the app gets a
/// synthetic FCM-like token instead of a real one, everything else is real —
/// Supabase sign-in, `POST /api/v1/devices` after the tenant is chosen, and
/// `DELETE /api/v1/devices/{id}` before sign-out. The test holds after each
/// step ([_hold]) so the registration can also be checked from the host via
/// the API. Writes one registration row in the demo tenant and removes it.
///
///     flutter test integration_test/push_smoke_test.dart -d emulator-5560 \
///       --dart-define-from-file=env/dev.json \
///       --dart-define-from-file=env/smoke.json \
///       --dart-define=BAYIOS_SMOKE_PUSH_TOKEN=smoke-emulator-<32 hex>
const _email = String.fromEnvironment('BAYIOS_SMOKE_EMAIL');
const _password = String.fromEnvironment('BAYIOS_SMOKE_PASSWORD');
const _tenantName = String.fromEnvironment('BAYIOS_SMOKE_TENANT');
const _token = String.fromEnvironment('BAYIOS_SMOKE_PUSH_TOKEN');
const _hold = Duration(
  seconds: int.fromEnvironment('BAYIOS_SMOKE_HOLD_SECONDS', defaultValue: 20),
);

/// Stands in for Firebase: a fixed token, permission granted, nothing else.
class _SyntheticPush implements PushService {
  const _SyntheticPush(this._value);

  final String _value;

  @override
  bool get available => true;

  @override
  Future<PushPermission> requestPermission() async => PushPermission.granted;

  @override
  Future<String?> token() async => _value;

  @override
  Stream<String> get tokenRefreshes => const Stream.empty();

  @override
  Stream<PushTap> get taps => const Stream.empty();

  @override
  Future<PushTap?> initialTap() async => null;

  @override
  Stream<ForegroundPush> get foregroundMessages => const Stream.empty();

  @override
  Future<void> deleteToken() async {}
}

const _listDevices = ApiEndpoint(HttpMethod.get, '/api/v1/devices');

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
    throw TestFailure('Timed out waiting for any of $finders');
  }

  testWidgets('register after sign-in, unregister before sign-out', (
    tester,
  ) async {
    expect(_email, isNotEmpty, reason: 'BAYIOS_SMOKE_EMAIL missing');
    expect(_password, isNotEmpty, reason: 'BAYIOS_SMOKE_PASSWORD missing');
    expect(_token.length, greaterThanOrEqualTo(32), reason: 'token define');

    await app.run(pushOverride: const _SyntheticPush(_token));

    final signInButton = find.byKey(const Key('sign-in-submit'));
    final dashboard = find.byKey(const Key('dashboard-tenant'));
    final picker = find.text(l10n.tenantPickerTitle);
    final profileSignOut = find.byKey(const Key('profile-sign-out'));
    ProviderContainer container() =>
        ProviderScope.containerOf(tester.element(find.byType(MaterialApp)));

    Future<void> signOutViaProfile() async {
      await tester.tap(find.byKey(const Key('nav-profile')));
      await waitForAny(tester, [profileSignOut]);
      await tester.tap(profileSignOut);
      await waitForAny(tester, [signInButton]);
    }

    if (await waitForAny(tester, [signInButton, dashboard]) == 1) {
      await signOutViaProfile();
    }

    await tester.enterText(find.byKey(const Key('sign-in-email')), _email);
    await tester.enterText(
      find.byKey(const Key('sign-in-password')),
      _password,
    );
    await tester.tap(signInButton);
    if (await waitForAny(tester, [dashboard, picker]) == 1) {
      await tester.tap(
        _tenantName.isEmpty
            ? find.byType(ListTile).first
            : find.widgetWithText(ListTile, _tenantName),
      );
      await waitForAny(tester, [dashboard]);
    }

    // The registration happens in the background after the tenant is chosen.
    final end = DateTime.now().add(const Duration(seconds: 30));
    while (container().read(pushRegistrationProvider) is! PushRegistered) {
      if (DateTime.now().isAfter(end)) {
        fail('not registered: ${container().read(pushRegistrationProvider)}');
      }
      await tester.pump(const Duration(milliseconds: 250));
    }
    final registered =
        container().read(pushRegistrationProvider) as PushRegistered;
    final listed = await container()
        .read(apiClientProvider)
        .send(
          _listDevices,
          decode: (json) =>
              ((json! as Map<String, Object?>)['items']! as List<Object?>)
                  .cast<Map<String, Object?>>(),
        );
    final ids = [for (final d in listed.data) d['id']];
    expect(ids, contains(registered.deviceId));
    expect(listed.data.every((d) => !d.containsKey('token')), isTrue);
    debugPrint(
      'SMOKE_REGISTERED tenant=${registered.tenantId} '
      'device=${registered.deviceId} listed=${ids.length}',
    );
    await tester.pump(_hold);

    await signOutViaProfile();
    expect(container().read(pushRegistrationProvider), isA<PushIdle>());
    debugPrint('SMOKE_SIGNED_OUT device=${registered.deviceId}');
    await tester.pump(_hold);
  });
}
