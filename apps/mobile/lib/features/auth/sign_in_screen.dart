import 'package:bayios_mobile/core/api/problem_messages.dart';
import 'package:bayios_mobile/core/auth/auth_controller.dart';
import 'package:bayios_mobile/core/auth/auth_repository.dart';
import 'package:bayios_mobile/core/ui/tokens.dart';
import 'package:bayios_mobile/core/ui/widgets.dart';
import 'package:bayios_mobile/features/auth/validation.dart';
import 'package:bayios_mobile/l10n/app_localizations.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

/// M01 — e-mail + password sign-in (Supabase Auth). Sign-up, password reset
/// and onboarding are web-only.
class SignInScreen extends ConsumerStatefulWidget {
  const SignInScreen({super.key});

  @override
  ConsumerState<SignInScreen> createState() => _SignInScreenState();
}

class _SignInScreenState extends ConsumerState<SignInScreen> {
  final _formKey = GlobalKey<FormState>();
  final _email = TextEditingController();
  final _password = TextEditingController();
  bool _obscure = true;
  bool _submitting = false;
  String? _error;

  @override
  void dispose() {
    _email.dispose();
    _password.dispose();
    super.dispose();
  }

  Future<void> _submit() async {
    if (_submitting) return;
    setState(() => _error = null);
    if (!(_formKey.currentState?.validate() ?? false)) return;
    setState(() => _submitting = true);
    final l10n = AppLocalizations.of(context);
    try {
      await ref
          .read(authControllerProvider.notifier)
          .signIn(email: _email.text, password: _password.text);
      // The router leaves this screen once the session is loaded.
    } on AuthFailure catch (failure) {
      if (mounted) setState(() => _error = authFailureMessage(l10n, failure));
    } on Object {
      if (mounted) setState(() => _error = l10n.authUnknown);
    } finally {
      if (mounted) setState(() => _submitting = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final theme = Theme.of(context);
    final auth = ref.watch(authControllerProvider);
    final expired = auth is SignedOut && auth.sessionExpired && _error == null;

    return Scaffold(
      body: SafeArea(
        child: Center(
          child: SingleChildScrollView(
            padding: const EdgeInsets.all(BosSpace.s24),
            child: ConstrainedBox(
              constraints: const BoxConstraints(maxWidth: 480),
              child: AutofillGroup(
                child: Form(
                  key: _formKey,
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.stretch,
                    children: [
                      Icon(
                        Icons.warehouse_outlined,
                        size: 56,
                        color: theme.colorScheme.primary,
                      ),
                      const SizedBox(height: BosSpace.s16),
                      Text(
                        l10n.appTitle,
                        textAlign: TextAlign.center,
                        style: theme.textTheme.headlineMedium,
                      ),
                      const SizedBox(height: BosSpace.s8),
                      Text(
                        l10n.signInSubtitle,
                        textAlign: TextAlign.center,
                        style: theme.textTheme.bodyMedium?.copyWith(
                          color: BosColors.textMuted,
                        ),
                      ),
                      const SizedBox(height: BosSpace.s32),
                      if (expired) ...[
                        ErrorMessageBox(l10n.authSessionExpired),
                        const SizedBox(height: BosSpace.s16),
                      ],
                      if (_error != null) ...[
                        ErrorMessageBox(_error!),
                        const SizedBox(height: BosSpace.s16),
                      ],
                      TextFormField(
                        key: const Key('sign-in-email'),
                        controller: _email,
                        enabled: !_submitting,
                        keyboardType: TextInputType.emailAddress,
                        textInputAction: TextInputAction.next,
                        autofillHints: const [
                          AutofillHints.username,
                          AutofillHints.email,
                        ],
                        autocorrect: false,
                        enableSuggestions: false,
                        decoration: InputDecoration(
                          labelText: l10n.emailLabel,
                          prefixIcon: const Icon(Icons.mail_outline),
                        ),
                        validator: (v) => validateEmail(l10n, v),
                      ),
                      const SizedBox(height: BosSpace.s16),
                      TextFormField(
                        key: const Key('sign-in-password'),
                        controller: _password,
                        enabled: !_submitting,
                        obscureText: _obscure,
                        textInputAction: TextInputAction.done,
                        autofillHints: const [AutofillHints.password],
                        autocorrect: false,
                        enableSuggestions: false,
                        onFieldSubmitted: (_) => _submit(),
                        decoration: InputDecoration(
                          labelText: l10n.passwordLabel,
                          prefixIcon: const Icon(Icons.lock_outline),
                          suffixIcon: IconButton(
                            key: const Key('sign-in-toggle-password'),
                            tooltip: _obscure
                                ? l10n.showPassword
                                : l10n.hidePassword,
                            icon: Icon(
                              _obscure
                                  ? Icons.visibility_outlined
                                  : Icons.visibility_off_outlined,
                            ),
                            onPressed: () =>
                                setState(() => _obscure = !_obscure),
                          ),
                        ),
                        validator: (v) => validatePasswordPresent(l10n, v),
                      ),
                      const SizedBox(height: BosSpace.s24),
                      FilledButton(
                        key: const Key('sign-in-submit'),
                        onPressed: _submitting ? null : _submit,
                        style: FilledButton.styleFrom(
                          minimumSize: const Size.fromHeight(
                            BosSize.primaryAction,
                          ),
                        ),
                        child: _submitting
                            ? Semantics(
                                label: l10n.signingIn,
                                child: const SizedBox.square(
                                  dimension: 24,
                                  child: CircularProgressIndicator(
                                    strokeWidth: 3,
                                  ),
                                ),
                              )
                            : Text(l10n.signInButton),
                      ),
                      const SizedBox(height: BosSpace.s24),
                      Text(
                        l10n.signInWebHint,
                        textAlign: TextAlign.center,
                        style: theme.textTheme.bodySmall?.copyWith(
                          color: BosColors.textMuted,
                        ),
                      ),
                    ],
                  ),
                ),
              ),
            ),
          ),
        ),
      ),
    );
  }
}
