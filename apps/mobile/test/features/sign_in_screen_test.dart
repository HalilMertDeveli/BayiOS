import 'package:bayios_mobile/core/auth/auth_controller.dart';
import 'package:bayios_mobile/core/auth/auth_repository.dart';
import 'package:bayios_mobile/features/auth/sign_in_screen.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import '../helpers/fakes.dart';
import '../helpers/pump.dart';

class _ExpiredAuth extends AuthController {
  @override
  AuthState build() => const SignedOut(sessionExpired: true);
}

void main() {
  late FakeAuthRepository repo;

  setUp(() => repo = FakeAuthRepository());

  Future<void> pump(WidgetTester tester) => pumpScreen(
    tester,
    const SignInScreen(),
    overrides: [authRepositoryProvider.overrideWithValue(repo)],
  );

  Future<void> submit(WidgetTester tester) async {
    await tester.tap(find.byKey(const Key('sign-in-submit')));
    await tester.pumpAndSettle();
  }

  testWidgets('empty form shows both validation messages', (tester) async {
    await pump(tester);
    await submit(tester);

    expect(find.text(l10n.validationEmailRequired), findsOneWidget);
    expect(find.text(l10n.validationPasswordRequired), findsOneWidget);
    expect(repo.signInCalls, isEmpty);
  });

  testWidgets('malformed e-mail is rejected before calling Auth', (
    tester,
  ) async {
    await pump(tester);
    await tester.enterText(find.byKey(const Key('sign-in-email')), 'depo@');
    await tester.enterText(find.byKey(const Key('sign-in-password')), 'x');
    await submit(tester);

    expect(find.text(l10n.validationEmailInvalid), findsOneWidget);
    expect(repo.signInCalls, isEmpty);
  });

  testWidgets('wrong credentials show the Turkish message', (tester) async {
    repo.signInError = const AuthFailure(AuthFailureKind.invalidCredentials);
    await pump(tester);
    await tester.enterText(
      find.byKey(const Key('sign-in-email')),
      'depo@ornek.test',
    );
    await tester.enterText(find.byKey(const Key('sign-in-password')), 'yanlis');
    await submit(tester);

    expect(find.text('E-posta adresi veya şifre hatalı.'), findsOneWidget);
    expect(repo.signInCalls.single.email, 'depo@ornek.test');
  });

  testWidgets('offline sign-in says the server is unreachable', (tester) async {
    repo.signInError = const AuthFailure(AuthFailureKind.network);
    await pump(tester);
    await tester.enterText(
      find.byKey(const Key('sign-in-email')),
      'depo@ornek.test',
    );
    await tester.enterText(find.byKey(const Key('sign-in-password')), 'x');
    await tester.testTextInput.receiveAction(TextInputAction.done);
    await tester.pumpAndSettle();

    expect(find.text(l10n.authNetwork), findsOneWidget);
  });

  testWidgets('password visibility toggle', (tester) async {
    await pump(tester);
    EditableText field() => tester.widget<EditableText>(
      find.descendant(
        of: find.byKey(const Key('sign-in-password')),
        matching: find.byType(EditableText),
      ),
    );
    expect(field().obscureText, isTrue);
    await tester.tap(find.byTooltip(l10n.showPassword));
    await tester.pump();
    expect(field().obscureText, isFalse);
    expect(find.byTooltip(l10n.hidePassword), findsOneWidget);
  });

  testWidgets('expired session notice', (tester) async {
    await pumpScreen(
      tester,
      const SignInScreen(),
      overrides: [
        authRepositoryProvider.overrideWithValue(repo),
        authControllerProvider.overrideWith(_ExpiredAuth.new),
      ],
    );
    expect(find.text(l10n.authSessionExpired), findsOneWidget);
  });

  testWidgets('touch targets ≥ 48 dp, labelled, readable', (tester) async {
    await pump(tester);
    await expectAccessibleTargets(tester);
    final submitSize = tester.getSize(find.byKey(const Key('sign-in-submit')));
    expect(submitSize.height, greaterThanOrEqualTo(56));
  });
}
