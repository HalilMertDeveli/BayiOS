import 'package:bayios_mobile/core/auth/auth_repository.dart';
import 'package:bayios_mobile/core/session/me.dart';

/// The name to greet the user with, the same way the web picks it
/// (apps/web/src/lib/session/session-context.tsx, user-menu.tsx):
/// `GET /me` `fullName` → Supabase `user_metadata.full_name` → the local part
/// of the e-mail address. Null only when none of them is usable.
String? displayNameFor(Me me, AuthUser? user) {
  for (final candidate in [me.fullName, user?.fullName]) {
    final trimmed = candidate?.trim();
    if (trimmed != null && trimmed.isNotEmpty) return trimmed;
  }
  final email = (me.email ?? user?.email)?.trim();
  if (email == null || email.isEmpty) return null;
  final at = email.indexOf('@');
  final local = at < 0 ? email : email.substring(0, at);
  return local.isEmpty ? null : local;
}
