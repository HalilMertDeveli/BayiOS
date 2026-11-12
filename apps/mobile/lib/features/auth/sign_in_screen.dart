import 'package:bayios_mobile/core/api/problem_messages.dart';
import 'package:bayios_mobile/core/auth/auth_controller.dart';
import 'package:bayios_mobile/core/auth/auth_repository.dart';
import 'package:bayios_mobile/core/theme/app_colors.dart';
import 'package:bayios_mobile/core/theme/app_icon.dart';
import 'package:bayios_mobile/core/theme/app_spacing.dart';
import 'package:bayios_mobile/core/ui/components.dart';
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
    final c = context.colors;
    final auth = ref.watch(authControllerProvider);
    final expired = auth is SignedOut && auth.sessionExpired && _error == null;
    final dark = Theme.of(context).brightness == Brightness.dark;

    return AnnotatedRegion<SystemUiOverlayStyle>(
      value: dark ? SystemUiOverlayStyle.light : SystemUiOverlayStyle.dark,
      child: Scaffold(
        body: SafeArea(
          child: LayoutBuilder(
            builder: (context, constraints) => SingleChildScrollView(
              padding: const EdgeInsets.all(AppSpacing.md),
              child: ConstrainedBox(
                constraints: BoxConstraints(
                  minHeight: constraints.maxHeight - AppSpacing.md * 2,
                ),
                child: Center(
                  child: ConstrainedBox(
                    constraints: const BoxConstraints(maxWidth: 480),
                    child: _card(context, l10n, c, expired),
                  ),
                ),
              ),
            ),
          ),
        ),
      ),
    );
  }

  /// Kit sign-in card: soft green top gradient, round icon badge, bold
  /// title, labelled inputs with leading icons, full-width primary button.
  Widget _card(
    BuildContext context,
    AppLocalizations l10n,
    AppColors c,
    bool expired,
  ) {
    // Small phones / landscape / open keyboard: a smaller badge so the form
    // and "Giriş yap" stay on screen.
    final compact = MediaQuery.sizeOf(context).height < 720;
    return DecoratedBox(
      decoration: BoxDecoration(
        color: c.surfaceCard,
        borderRadius: AppRadius.lgAll,
        border: Border.all(color: c.borderDefault),
        boxShadow: AppShadows.card,
      ),
      child: ClipRRect(
        borderRadius: AppRadius.lgAll,
        child: DecoratedBox(
          decoration: BoxDecoration(
            gradient: LinearGradient(
              begin: Alignment.topCenter,
              end: const Alignment(0, -0.4),
              colors: [c.brand.bg, c.surfaceCard],
            ),
          ),
          child: Padding(
            padding: const EdgeInsets.all(AppSpacing.lg),
            child: _form(context, l10n, c, expired, compact),
          ),
        ),
      ),
    );
  }

  Widget _form(
    BuildContext context,
    AppLocalizations l10n,
    AppColors c,
    bool expired,
    bool compact,
  ) {
    return AutofillGroup(
      child: Form(
        key: _formKey,
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            Center(
              child: HaloIcon(icon: AppIcons.store03, size: compact ? 64 : 96),
            ),
            SizedBox(height: compact ? AppSpacing.sm : AppSpacing.lg),
            Semantics(
              header: true,
              child: Text(
                l10n.appTitle,
                textAlign: TextAlign.center,
                style: context.text.headlineMedium?.copyWith(
                  color: c.textPrimary,
                ),
              ),
            ),
            const SizedBox(height: AppSpacing.xxs),
            Text(
              l10n.signInSubtitle,
              textAlign: TextAlign.center,
              style: context.text.bodyMedium?.copyWith(color: c.textMuted),
            ),
            SizedBox(height: compact ? AppSpacing.md : AppSpacing.lg),
            if (expired) ...[
              ErrorMessageBox(l10n.authSessionExpired),
              const SizedBox(height: AppSpacing.md),
            ],
            if (_error != null) ...[
              ErrorMessageBox(_error!),
              const SizedBox(height: AppSpacing.md),
            ],
            LabeledField(
              label: l10n.emailLabel,
              child: TextFormField(
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
                  hintText: l10n.emailHint,
                  prefixIcon: const AppIcon(AppIcons.email1),
                ),
                validator: (v) => validateEmail(l10n, v),
              ),
            ),
            const SizedBox(height: AppSpacing.md),
            LabeledField(
              label: l10n.passwordLabel,
              child: TextFormField(
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
                  hintText: l10n.passwordHint,
                  prefixIcon: const AppIcon(AppIcons.lock),
                  suffixIcon: IconButton(
                    key: const Key('sign-in-toggle-password'),
                    tooltip: _obscure ? l10n.showPassword : l10n.hidePassword,
                    icon: AppIcon(
                      _obscure ? AppIcons.visibility : AppIcons.hide,
                    ),
                    onPressed: () => setState(() => _obscure = !_obscure),
                  ),
                ),
                validator: (v) => validatePasswordPresent(l10n, v),
              ),
            ),
            const SizedBox(height: AppSpacing.lg),
            AppButton(
              key: const Key('sign-in-submit'),
              label: l10n.signInButton,
              loading: _submitting,
              loadingLabel: l10n.signingIn,
              expand: true,
              height: AppSize.primaryAction,
              onPressed: _submit,
            ),
            const SizedBox(height: AppSpacing.lg),
            Row(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                AppIcon(
                  AppIcons.language,
                  size: AppSize.icon,
                  color: c.textMuted,
                ),
                const SizedBox(width: AppSpacing.xs),
                Expanded(
                  child: Text(
                    l10n.signInWebHint,
                    style: context.text.bodySmall?.copyWith(color: c.textMuted),
                  ),
                ),
              ],
            ),
          ],
        ),
      ),
    );
  }
}
