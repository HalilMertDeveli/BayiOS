import 'package:bayios_mobile/core/auth/auth_controller.dart';
import 'package:bayios_mobile/core/session/session_controller.dart';
import 'package:bayios_mobile/core/theme/app_colors.dart';
import 'package:bayios_mobile/core/theme/app_icon.dart';
import 'package:bayios_mobile/core/theme/app_spacing.dart';
import 'package:bayios_mobile/core/ui/components.dart';
import 'package:bayios_mobile/l10n/app_localizations.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

/// No active membership: creating a business or accepting an invitation is
/// done on the web.
class NoMembershipScreen extends ConsumerWidget {
  const NoMembershipScreen({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final l10n = AppLocalizations.of(context);
    final c = context.colors;
    return Scaffold(
      body: SafeArea(
        child: Padding(
          padding: const EdgeInsets.all(AppSpacing.md),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              const Align(
                alignment: Alignment.centerLeft,
                child: BrandWordmark(),
              ),
              const Spacer(),
              const Center(child: HaloIcon(icon: AppIcons.store03)),
              const SizedBox(height: AppSpacing.lg),
              Text(
                l10n.noMembershipTitle,
                textAlign: TextAlign.center,
                style: context.text.headlineMedium?.copyWith(
                  color: c.textPrimary,
                ),
              ),
              const SizedBox(height: AppSpacing.xs),
              Text(
                l10n.noMembershipBody,
                textAlign: TextAlign.center,
                style: context.text.bodyMedium?.copyWith(color: c.textMuted),
              ),
              const Spacer(),
              FilledButton.icon(
                key: const Key('no-membership-retry'),
                onPressed: () =>
                    ref.read(sessionControllerProvider.notifier).reload(),
                icon: const AppIcon(AppIcons.refresh),
                label: Text(l10n.retry),
                style: FilledButton.styleFrom(
                  minimumSize: const Size.fromHeight(AppSize.primaryAction),
                ),
              ),
              const SizedBox(height: AppSpacing.sm),
              OutlinedButton.icon(
                key: const Key('no-membership-sign-out'),
                onPressed: () =>
                    ref.read(authControllerProvider.notifier).signOut(),
                icon: const AppIcon(AppIcons.logout),
                label: Text(l10n.signOut),
                style: OutlinedButton.styleFrom(
                  minimumSize: const Size.fromHeight(AppSize.primaryAction),
                ),
              ),
            ],
          ),
        ),
      ),
    );
  }
}
