import 'dart:async';

import 'package:bayios_mobile/core/auth/auth_repository.dart';
import 'package:supabase_flutter/supabase_flutter.dart' as sb;

/// [AuthRepository] over supabase_flutter's GoTrue client. Supabase is used
/// for identity only — no table is read or written through it.
class SupabaseAuthRepository implements AuthRepository {
  SupabaseAuthRepository(this._auth);

  final sb.GoTrueClient _auth;

  @override
  AuthUser? get currentUser => _toUser(_auth.currentSession?.user);

  @override
  Stream<AuthChange> changes() => _auth.onAuthStateChange
      // Network errors of background refreshes are stream errors; the next
      // API call reports them. Drop them here so the stream never closes.
      .handleError((Object _) {})
      .where(
        (s) =>
            s.event == sb.AuthChangeEvent.signedIn ||
            s.event == sb.AuthChangeEvent.signedOut ||
            s.event == sb.AuthChangeEvent.userUpdated,
      )
      .map(
        (s) => AuthChange(
          s.event == sb.AuthChangeEvent.signedOut
              ? null
              : _toUser(s.session?.user),
          sessionExpired:
              s.signOutReason == sb.SignOutReason.sessionExpired ||
              s.signOutReason == sb.SignOutReason.sessionMissing,
        ),
      );

  @override
  Future<void> signInWithPassword({
    required String email,
    required String password,
  }) async {
    try {
      await _auth.signInWithPassword(email: email.trim(), password: password);
    } on sb.AuthRetryableFetchException catch (error) {
      throw AuthFailure(AuthFailureKind.network, cause: error);
    } on sb.AuthException catch (error) {
      throw AuthFailure(
        mapAuthError(error.code, error.statusCode),
        cause: error,
      );
    } on Object catch (error) {
      // SocketException / ClientException from the HTTP layer.
      throw AuthFailure(AuthFailureKind.network, cause: error);
    }
  }

  @override
  Future<void> signOut() async {
    try {
      // Local scope: this device's session; the refresh token is revoked on
      // the server when reachable. The local session is removed first.
      await _auth.signOut();
    } on Object {
      // Offline: the local session is already gone, nothing else to do.
    }
  }

  @override
  Future<String?> accessToken() async {
    final session = _auth.currentSession;
    if (session == null) return null;
    // isExpired includes a 10 s safety margin.
    if (session.isExpired) return await refreshAccessToken();
    return session.accessToken;
  }

  @override
  Future<String?> refreshAccessToken() async {
    if (_auth.currentSession == null) return null;
    try {
      final response = await _auth.refreshSession();
      return response.session?.accessToken;
    } on sb.AuthRetryableFetchException {
      rethrow; // offline: the session stays, the caller reports network
    } on sb.AuthException {
      // Revoked / expired refresh token: GoTrue removed the session and
      // emitted signedOut (sessionExpired).
      return null;
    }
  }

  static AuthUser? _toUser(sb.User? user) =>
      user == null ? null : AuthUser(id: user.id, email: user.email);
}

/// Supabase Auth error code / status → [AuthFailureKind]
/// (https://supabase.com/docs/guides/auth/debugging/error-codes).
AuthFailureKind mapAuthError(String? code, String? statusCode) {
  switch (code) {
    case 'invalid_credentials':
      return AuthFailureKind.invalidCredentials;
    case 'email_not_confirmed':
      return AuthFailureKind.emailNotConfirmed;
    case 'over_request_rate_limit':
    case 'over_email_send_rate_limit':
      return AuthFailureKind.rateLimited;
    case 'session_not_found':
    case 'session_expired':
    case 'refresh_token_not_found':
      return AuthFailureKind.sessionExpired;
  }
  if (statusCode == '429') return AuthFailureKind.rateLimited;
  // Older GoTrue versions answer bad credentials with a bare 400.
  if (statusCode == '400' && code == null) {
    return AuthFailureKind.invalidCredentials;
  }
  return AuthFailureKind.unknown;
}
