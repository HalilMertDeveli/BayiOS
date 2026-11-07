import 'package:bayios_mobile/core/auth/auth_controller.dart';
import 'package:bayios_mobile/core/auth/auth_repository.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';

import '../../helpers/fakes.dart';

void main() {
  late FakeAuthRepository repo;
  late ProviderContainer container;

  ProviderContainer create() => ProviderContainer.test(
    overrides: [authRepositoryProvider.overrideWithValue(repo)],
  );

  setUp(() => repo = FakeAuthRepository());

  test('starts signed out without a stored session', () {
    container = create();
    expect(container.read(authControllerProvider), isA<SignedOut>());
  });

  test('a restored session starts signed in', () {
    repo.user = const AuthUser(id: 'user-1');
    container = create();
    final state = container.read(authControllerProvider);
    expect(state, isA<SignedIn>());
    expect((state as SignedIn).user.id, 'user-1');
  });

  test('sign in success', () async {
    container = create();
    await container
        .read(authControllerProvider.notifier)
        .signIn(email: 'depo@ornek.test', password: 'secret-1');
    expect(container.read(authControllerProvider), isA<SignedIn>());
    expect(repo.signInCalls.single.email, 'depo@ornek.test');
  });

  test('sign in failure keeps the user signed out', () async {
    repo.signInError = const AuthFailure(AuthFailureKind.invalidCredentials);
    container = create();
    await expectLater(
      container
          .read(authControllerProvider.notifier)
          .signIn(email: 'a@b.co', password: 'x'),
      throwsA(
        isA<AuthFailure>().having(
          (f) => f.kind,
          'kind',
          AuthFailureKind.invalidCredentials,
        ),
      ),
    );
    expect(container.read(authControllerProvider), isA<SignedOut>());
  });

  test('sign out', () async {
    repo.user = const AuthUser(id: 'user-1');
    container = create();
    await container.read(authControllerProvider.notifier).signOut();
    final state = container.read(authControllerProvider);
    expect(state, isA<SignedOut>());
    expect((state as SignedOut).sessionExpired, isFalse);
    expect(repo.signOutCalls, 1);
  });

  test('an expired / revoked session (refresh failed) is signed out', () async {
    repo.user = const AuthUser(id: 'user-1');
    container = create();
    expect(container.read(authControllerProvider), isA<SignedIn>());

    repo.emit(const AuthChange(null, sessionExpired: true));
    await Future<void>.delayed(Duration.zero);

    final state = container.read(authControllerProvider);
    expect(state, isA<SignedOut>());
    expect((state as SignedOut).sessionExpired, isTrue);
  });

  test('API rejection (401 after refresh) signs out locally', () async {
    repo.user = const AuthUser(id: 'user-1');
    container = create();
    container.read(authControllerProvider.notifier).sessionExpired();
    await Future<void>.delayed(Duration.zero);

    final state = container.read(authControllerProvider);
    expect((state as SignedOut).sessionExpired, isTrue);
    expect(repo.signOutCalls, 1);

    // Idempotent: a second 401 does not sign out twice.
    container.read(authControllerProvider.notifier).sessionExpired();
    expect(repo.signOutCalls, 1);
  });

  test('a sign-in on another path (stream) is picked up', () async {
    container = create();
    repo.emit(const AuthChange(AuthUser(id: 'user-9')));
    await Future<void>.delayed(Duration.zero);
    expect(
      (container.read(authControllerProvider) as SignedIn).user.id,
      'user-9',
    );
  });
}
