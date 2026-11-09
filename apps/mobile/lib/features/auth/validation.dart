import 'package:bayios_mobile/l10n/app_localizations.dart';

/// Client-side checks (UX only — Supabase Auth validates again); same rules
/// as the web (apps/web/src/lib/auth/validation.ts).
final _emailPattern = RegExp(r'^[^\s@]+@[^\s@]+\.[^\s@]+$');

String? validateEmail(AppLocalizations l10n, String? value) {
  final email = (value ?? '').trim();
  if (email.isEmpty) return l10n.validationEmailRequired;
  if (!_emailPattern.hasMatch(email)) return l10n.validationEmailInvalid;
  return null;
}

String? validatePasswordPresent(AppLocalizations l10n, String? value) =>
    (value ?? '').isEmpty ? l10n.validationPasswordRequired : null;
