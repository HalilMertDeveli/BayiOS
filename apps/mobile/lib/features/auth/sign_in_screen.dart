import 'package:bayios_mobile/core/api/problem_messages.dart';
import 'package:bayios_mobile/core/auth/auth_controller.dart';
import 'package:bayios_mobile/core/auth/auth_repository.dart';
import 'package:bayios_mobile/core/ui/components.dart';
import 'package:bayios_mobile/core/ui/tokens.dart';
import 'package:bayios_mobile/core/ui/widgets.dart';
import 'package:bayios_mobile/features/auth/validation.dart';
import 'package:bayios_mobile/l10n/app_localizations.dart';
import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
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

    return AnnotatedRegion<SystemUiOverlayStyle>(
      // White status-bar icons on the petrol header.
      value: SystemUiOverlayStyle.light,
      child: Scaffold(
        body: LayoutBuilder(
          builder: (context, constraints) => SingleChildScrollView(
            child: ConstrainedBox(
              constraints: BoxConstraints(minHeight: constraints.maxHeight),
              child: Column(
                children: [
                  _BrandHeader(l10n: l10n),
                  Center(
                    child: ConstrainedBox(
                      constraints: const BoxConstraints(maxWidth: 480),
                      child: Padding(
                        padding: const EdgeInsets.fromLTRB(
                          BosSpace.s24,
                          BosSpace.s32,
                          BosSpace.s24,
                          BosSpace.s24,
                        ),
                        child: _form(context, l10n, theme, expired),
                      ),
                    ),
                  ),
                ],
              ),
            ),
          ),
        ),
      ),
    );
  }

  Widget _form(
    BuildContext context,
    AppLocalizations l10n,
    ThemeData theme,
    bool expired,
  ) {
    return SafeArea(
      top: false,
      child: AutofillGroup(
        child: Form(
          key: _formKey,
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              Text(
                l10n.signInTitle,
                style: theme.textTheme.headlineSmall?.copyWith(
                  fontWeight: FontWeight.w700,
                ),
              ),
              const SizedBox(height: BosSpace.s4),
              Text(
                l10n.signInSubtitle,
                style: theme.textTheme.bodyMedium?.copyWith(
                  color: BosColors.textSecondary,
                ),
              ),
              const SizedBox(height: BosSpace.s24),
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
                    tooltip: _obscure ? l10n.showPassword : l10n.hidePassword,
                    icon: Icon(
                      _obscure
                          ? Icons.visibility_outlined
                          : Icons.visibility_off_outlined,
                    ),
                    onPressed: () => setState(() => _obscure = !_obscure),
                  ),
                ),
                validator: (v) => validatePasswordPresent(l10n, v),
              ),
              const SizedBox(height: BosSpace.s24),
              FilledButton(
                key: const Key('sign-in-submit'),
                onPressed: _submitting ? null : _submit,
                style: FilledButton.styleFrom(
                  minimumSize: const Size.fromHeight(BosSize.primaryAction),
                ),
                child: _submitting
                    ? Semantics(
                        label: l10n.signingIn,
                        child: const SizedBox.square(
                          dimension: 24,
                          child: CircularProgressIndicator(strokeWidth: 3),
                        ),
                      )
                    : Text(l10n.signInButton),
              ),
              const SizedBox(height: BosSpace.s24),
              Row(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  const Icon(
                    Icons.language,
                    size: 20,
                    color: BosColors.textMuted,
                  ),
                  const SizedBox(width: BosSpace.s8),
                  Expanded(
                    child: Text(
                      l10n.signInWebHint,
                      style: theme.textTheme.bodySmall?.copyWith(
                        color: BosColors.textMuted,
                      ),
                    ),
                  ),
                ],
              ),
            ],
          ),
        ),
      ),
    );
  }
}

/// Petrol brand header of the sign-in screen: mark, product name, tagline
/// and the copper accent line.
class _BrandHeader extends StatelessWidget {
  const _BrandHeader({required this.l10n});

  final AppLocalizations l10n;

  /// Below this height (small phones, landscape, an open keyboard) the
  /// header shrinks so the form and "Giriş yap" stay on screen.
  static const _compactBelow = 720.0;

  @override
  Widget build(BuildContext context) {
    final compact = MediaQuery.sizeOf(context).height < _compactBelow;
    return DecoratedBox(
      decoration: const BoxDecoration(
        gradient: LinearGradient(
          begin: Alignment.topLeft,
          end: Alignment.bottomRight,
          colors: [BosColors.brandPrimary, BosPetrol.p900],
        ),
        borderRadius: BorderRadius.vertical(bottom: Radius.circular(28)),
      ),
      child: SafeArea(
        bottom: false,
        child: SizedBox(
          width: double.infinity,
          child: Padding(
            padding: EdgeInsets.fromLTRB(
              BosSpace.s24,
              compact ? BosSpace.s16 : BosSpace.s48,
              BosSpace.s24,
              compact ? BosSpace.s16 : BosSpace.s40,
            ),
            child: Column(
              children: [
                BrandMark(size: compact ? 48 : 80, inverted: true),
                SizedBox(height: compact ? BosSpace.s8 : BosSpace.s20),
                Semantics(
                  header: true,
                  child: Text(
                    l10n.appTitle,
                    textAlign: TextAlign.center,
                    style: (compact ? BosType.h2 : BosType.display).copyWith(
                      color: BosColors.brandOnPrimary,
                      fontWeight: FontWeight.w700,
                    ),
                  ),
                ),
                if (!compact) ...[
                  const SizedBox(height: BosSpace.s8),
                  Text(
                    l10n.appTagline,
                    textAlign: TextAlign.center,
                    style: BosType.bodyLg.copyWith(color: BosPetrol.p100),
                  ),
                ],
                SizedBox(height: compact ? BosSpace.s8 : BosSpace.s20),
                Container(
                  width: 48,
                  height: 4,
                  decoration: BoxDecoration(
                    color: BosColors.brandAccent,
                    borderRadius: BorderRadius.circular(2),
                  ),
                ),
              ],
            ),
          ),
        ),
      ),
    );
  }
}
