import 'dart:async';

import 'package:bayios_mobile/core/auth/auth_repository.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

sealed class AuthState {
  const AuthState();
}

final class SignedOut extends AuthState {
  const SignedOut({this.sessionExpired = false});

  /// The session ended by itself (expired / revoked refresh token, or the
  /// API rejected the token after a refresh); the sign-in screen says so.
  final bool sessionExpired;
}

final class SignedIn extends AuthState {
  const SignedIn(this.user);

  final AuthUser user;
}

/// Overridden in bootstrap (SupabaseAuthRepository) and in tests (fakes).
final authRepositoryProvider = Provider<AuthRepository>(
  (ref) => throw UnimplementedError('authRepositoryProvider not overridden'),
);

/// Steps that must run while the session is still valid, right before
/// sign-out. Registered by other controllers (push) in their build; a plain
/// list, so [AuthController] does not depend on them (no provider cycle).
final beforeSignOutProvider = Provider<List<Future<void> Function()>>(
  (ref) => [],
);

final authControllerProvider = NotifierProvider<AuthController, AuthState>(
  AuthController.new,
);

/// Auth state of the app: signed in (with the Supabase user) or signed out.
/// An expired or revoked session counts as signed out.
class AuthController extends Notifier<AuthState> {
  late AuthRepository _repository;

  @override
  AuthState build() {
    _repository = ref.watch(authRepositoryProvider);
    final subscription = _repository.changes().listen(_onChange);
    ref.onDispose(subscription.cancel);
    final user = _repository.currentUser;
    return user == null ? const SignedOut() : SignedIn(user);
  }

  void _onChange(AuthChange change) {
    final user = change.user;
    if (user != null) {
      if (state case SignedIn(user: final current) when current == user) {
        return;
      }
      state = SignedIn(user);
    } else if (state is SignedIn) {
      state = SignedOut(sessionExpired: change.sessionExpired);
    }
  }

  /// Throws [AuthFailure] (the form shows the Turkish message).
  Future<void> signIn({required String email, required String password}) async {
    await _repository.signInWithPassword(email: email, password: password);
    final user = _repository.currentUser;
    if (user == null) {
      throw const AuthFailure(AuthFailureKind.unknown);
    }
    state = SignedIn(user);
  }

  /// Runs the [beforeSignOutProvider] steps first (e.g. removing this
  /// install's push registration — it needs the session), then signs out,
  /// also when a step fails. Steps bound their own time.
  Future<void> signOut() async {
    for (final step in [...ref.read(beforeSignOutProvider)]) {
      try {
        await step();
      } on Object {
        // Best effort: the API drops stale registrations.
      }
    }
    state = const SignedOut();
    await _repository.signOut();
  }

  /// The API rejected the session (401 after a refresh, or no token could be
  /// obtained): drop it locally and show the sign-in screen with a notice.
  void sessionExpired() {
    if (state is SignedOut) return;
    state = const SignedOut(sessionExpired: true);
    unawaited(_repository.signOut());
  }
}
