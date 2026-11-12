import 'package:bayios_mobile/core/auth/auth_controller.dart';
import 'package:bayios_mobile/core/session/display_name.dart';
import 'package:bayios_mobile/core/session/session_controller.dart';
import 'package:bayios_mobile/core/theme/app_colors.dart';
import 'package:bayios_mobile/core/theme/app_icon.dart';
import 'package:bayios_mobile/core/theme/app_spacing.dart';
import 'package:bayios_mobile/core/ui/components.dart';
import 'package:bayios_mobile/core/ui/widgets.dart';
import 'package:bayios_mobile/l10n/app_localizations.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

/// A read-only fact: icon, small muted label above the value (the value is
/// what is read).
class _InfoRow extends StatelessWidget {
  const _InfoRow({
    required this.icon,
    required this.label,
    required this.value,
  });

  final AppIconData icon;
  final String label;
  final String value;

  @override
  Widget build(BuildContext context) {
    final c = context.colors;
    return Padding(
      padding: const EdgeInsets.symmetric(
        horizontal: AppSpacing.md,
        vertical: AppSpacing.sm,
      ),
      child: Row(
        children: [
          AppIcon(icon, size: AppSize.icon, color: c.textMuted),
          const SizedBox(width: AppSpacing.sm),
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(
                  label,
                  style: context.text.labelMedium?.copyWith(color: c.textMuted),
                ),
                const SizedBox(height: AppSpacing.xxs / 2),
                Text(
                  value,
                  style: context.text.bodyMedium?.copyWith(
                    color: c.textPrimary,
                  ),
                ),
              ],
            ),
          ),
        ],
      ),
    );
  }
}

/// One action row of the grouped list card (kit settings menu).
class _ActionRow extends StatelessWidget {
  const _ActionRow({
    required this.icon,
    required this.label,
    required this.onTap,
    super.key,
    this.danger = false,
  });

  final AppIconData icon;
  final String label;
  final VoidCallback onTap;
  final bool danger;

  @override
  Widget build(BuildContext context) {
    final c = context.colors;
    final fg = danger ? c.danger.fg : c.textPrimary;
    return InkWell(
      onTap: onTap,
      child: ConstrainedBox(
        constraints: const BoxConstraints(minHeight: AppSize.primaryAction),
        child: Padding(
          padding: const EdgeInsets.symmetric(horizontal: AppSpacing.md),
          child: Row(
            children: [
              AppIcon(icon, size: AppSize.icon, color: fg),
              const SizedBox(width: AppSpacing.sm),
              Expanded(
                child: Text(
                  label,
                  style: context.text.bodyMedium?.copyWith(color: fg),
                ),
              ),
              AppIcon(
                AppIcons.arrowRight2,
                size: AppSize.icon,
                color: c.textMuted,
              ),
            ],
          ),
        ),
      ),
    );
  }
}

class ProfileScreen extends ConsumerWidget {
  const ProfileScreen({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final l10n = AppLocalizations.of(context);
    final c = context.colors;
    final session = ref.watch(sessionControllerProvider);
    if (session is! SessionReady) return const LoadingView();
    final auth = ref.watch(authControllerProvider);
    final name =
        displayNameFor(session.me, auth is SignedIn ? auth.user : null) ??
        session.tenantName;

    return Scaffold(
      appBar: AppTopBar(title: l10n.profileTitle),
      body: ListView(
        padding: const EdgeInsets.all(AppSpacing.md),
        children: [
          // Who: avatar (initials), name, e-mail.
          AppCard(
            child: Row(
              children: [
                AppAvatar(name: name, size: AppSize.primaryAction),
                const SizedBox(width: AppSpacing.md),
                Expanded(
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Text(
                        name,
                        style: context.text.titleLarge?.copyWith(
                          color: c.textPrimary,
                        ),
                      ),
                      Text(
                        roleLabel(l10n, session.role),
                        style: context.text.bodySmall?.copyWith(
                          color: c.textMuted,
                        ),
                      ),
                    ],
                  ),
                ),
              ],
            ),
          ),
          const SizedBox(height: AppSpacing.md),
          AppCard(
            title: l10n.profileActionsSection,
            padding: EdgeInsets.zero,
            child: Column(
              children: [
                if (session.memberships.length > 1) ...[
                  _ActionRow(
                    key: const Key('profile-switch-tenant'),
                    icon: AppIcons.swapHorizontal,
                    label: l10n.switchTenant,
                    onTap: () => ref
                        .read(sessionControllerProvider.notifier)
                        .switchTenant(),
                  ),
                  const Divider(),
                ],
                _ActionRow(
                  key: const Key('profile-sign-out'),
                  icon: AppIcons.logout,
                  label: l10n.signOut,
                  danger: true,
                  onTap: () =>
                      ref.read(authControllerProvider.notifier).signOut(),
                ),
              ],
            ),
          ),
          const SizedBox(height: AppSpacing.md),
          AppCard(
            title: l10n.profileAccountSection,
            padding: const EdgeInsets.symmetric(vertical: AppSpacing.xs),
            child: Column(
              children: [
                _InfoRow(
                  icon: AppIcons.email1,
                  label: l10n.profileEmailLabel,
                  value: session.me.email ?? '—',
                ),
                _InfoRow(
                  icon: AppIcons.store03,
                  label: l10n.profileTenantLabel,
                  value: session.tenantName,
                ),
                _InfoRow(
                  icon: AppIcons.userStory,
                  label: l10n.profileRoleLabel,
                  value: roleLabel(l10n, session.role),
                ),
              ],
            ),
          ),
          const SizedBox(height: AppSpacing.md),
          AppCard(
            title: l10n.profileAboutSection,
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                const BrandWordmark(markSize: 28),
                const SizedBox(height: AppSpacing.sm),
                Text(
                  l10n.profileAboutCredit,
                  key: const Key('profile-about-credit'),
                  style: context.text.bodySmall?.copyWith(
                    color: c.textSecondary,
                  ),
                ),
                const SizedBox(height: AppSpacing.xs),
                Text(
                  l10n.profileFontCredit,
                  style: context.text.bodySmall?.copyWith(color: c.textMuted),
                ),
              ],
            ),
          ),
          const SizedBox(height: AppSpacing.lg),
        ],
      ),
    );
  }
}
