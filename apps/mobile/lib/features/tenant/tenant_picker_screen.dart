import 'package:bayios_mobile/core/auth/auth_controller.dart';
import 'package:bayios_mobile/core/session/me.dart';
import 'package:bayios_mobile/core/session/session_controller.dart';
import 'package:bayios_mobile/core/theme/app_colors.dart';
import 'package:bayios_mobile/core/theme/app_icon.dart';
import 'package:bayios_mobile/core/theme/app_spacing.dart';
import 'package:bayios_mobile/core/ui/components.dart';
import 'package:bayios_mobile/core/ui/widgets.dart';
import 'package:bayios_mobile/l10n/app_localizations.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

/// M01 — pick the business to work in (memberships from `GET /me`).
class TenantPickerScreen extends ConsumerWidget {
  const TenantPickerScreen({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final l10n = AppLocalizations.of(context);
    final session = ref.watch(sessionControllerProvider);
    final memberships = session is SessionNeedsTenant
        ? session.memberships
        : const <Membership>[];
    final inactive = session is SessionNeedsTenant && session.inactiveNotice;
    final currentId = session is SessionNeedsTenant
        ? session.currentTenantId
        : null;

    return Scaffold(
      appBar: AppTopBar(
        title: l10n.tenantPickerTitle,
        showBack: false,
        actions: [
          TextButton.icon(
            key: const Key('tenant-picker-sign-out'),
            onPressed: () =>
                ref.read(authControllerProvider.notifier).signOut(),
            icon: const AppIcon(AppIcons.logout),
            label: Text(l10n.signOut),
          ),
        ],
      ),
      body: ListView(
        padding: const EdgeInsets.all(AppSpacing.md),
        children: [
          Text(
            l10n.tenantPickerSubtitle,
            style: context.text.bodyMedium?.copyWith(
              color: context.colors.textMuted,
            ),
          ),
          const SizedBox(height: AppSpacing.md),
          if (inactive) ...[
            ErrorMessageBox(l10n.tenantInactiveNotice),
            const SizedBox(height: AppSpacing.md),
          ],
          for (final m in memberships) ...[
            _TenantTile(
              membership: m,
              current: m.tenantId == currentId,
              onTap: () => ref
                  .read(sessionControllerProvider.notifier)
                  .selectTenant(m.tenantId),
            ),
            const SizedBox(height: AppSpacing.xs),
          ],
        ],
      ),
    );
  }
}

String? tenantStatusLabel(AppLocalizations l10n, Membership m) {
  if (m.isTenantUsable) return null;
  return switch (m.tenantStatus) {
    'suspended' => l10n.tenantStatusSuspended,
    'closed' => l10n.tenantStatusClosed,
    _ => l10n.tenantStatusUnavailable,
  };
}

class _TenantTile extends StatelessWidget {
  const _TenantTile({
    required this.membership,
    required this.current,
    required this.onTap,
  });

  final Membership membership;
  final bool current;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final c = context.colors;
    final status = tenantStatusLabel(l10n, membership);
    final usable = status == null;
    final subtitle = [
      roleLabel(l10n, membership.role),
      ?status,
      if (current) l10n.tenantCurrent,
    ].join(' · ');

    return AppCard(
      borderColor: current ? c.brandPrimary : null,
      padding: EdgeInsets.zero,
      child: ListTile(
        key: Key('tenant-${membership.tenantId}'),
        enabled: usable,
        onTap: usable ? onTap : null,
        contentPadding: const EdgeInsets.fromLTRB(
          AppSpacing.md,
          AppSpacing.xxs,
          AppSpacing.sm,
          AppSpacing.xxs,
        ),
        leading: IconBadge(
          icon: usable ? AppIcons.store03 : AppIcons.block,
          tone: usable ? Tone.brand : Tone.neutral,
        ),
        title: Text(
          membership.tenantName,
          style: context.text.titleMedium?.copyWith(
            color: usable ? c.textPrimary : c.textMuted,
          ),
        ),
        subtitle: Text(subtitle),
        trailing: usable
            ? AppIcon(
                AppIcons.arrowRight2,
                size: AppSize.icon,
                color: c.textMuted,
              )
            : null,
      ),
    );
  }
}
