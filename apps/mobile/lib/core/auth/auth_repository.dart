import 'dart:async';

import 'package:bayios_mobile/core/api/api_client.dart';
import 'package:flutter/foundation.dart';

/// The signed-in identity (Supabase Auth user). Display only; who may do
/// what comes from `GET /api/v1/me`.
@immutable
class AuthUser {
  const AuthUser({required this.id, this.email});

  final String id;
  final String? email;

  @override
  bool operator ==(Object other) =>
      other is AuthUser && other.id == id && other.email == email;

  @override
  int get hashCode => Object.hash(id, email);
}

/// A change of the auth session.
class AuthChange {
  const AuthChange(this.user, {this.sessionExpired = false});

  /// Null when signed out.
  final AuthUser? user;

  /// Signed out because the session could not be refreshed (revoked or
  /// expired refresh token) rather than by the user.
  final bool sessionExpired;
}

enum AuthFailureKind {
  invalidCredentials,
  emailNotConfirmed,
  rateLimited,
  network,
  sessionExpired,
  unknown,
}

class AuthFailure implements Exception {
  const AuthFailure(this.kind, {this.cause});

  final AuthFailureKind kind;
  final Object? cause;

  @override
  String toString() => 'AuthFailure(${kind.name})';
}

/// Sign-in only (warehouse staff): sign-up, invitations, password reset and
/// onboarding stay on the web. Implemented with supabase_flutter
/// (SupabaseAuthRepository); tests use a fake.
abstract interface class AuthRepository implements AccessTokenSource {
  AuthUser? get currentUser;

  /// Emits on sign-in, sign-out and session expiry (not on token refresh).
  Stream<AuthChange> changes();

  /// Throws [AuthFailure].
  Future<void> signInWithPassword({
    required String email,
    required String password,
  });

  /// Ends the session on this device and revokes its refresh token on the
  /// server when reachable. Never throws: the local session is always gone.
  Future<void> signOut();
}
