import 'package:bayios_mobile/core/auth/auth_controller.dart';
import 'package:bayios_mobile/core/session/me.dart';
import 'package:bayios_mobile/core/session/session_controller.dart';
import 'package:bayios_mobile/core/ui/tokens.dart';
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
      appBar: AppBar(
        title: Text(l10n.tenantPickerTitle),
        actions: [
          TextButton.icon(
            key: const Key('tenant-picker-sign-out'),
            onPressed: () =>
                ref.read(authControllerProvider.notifier).signOut(),
            icon: const Icon(Icons.logout),
            label: Text(l10n.signOut),
          ),
        ],
      ),
      body: ListView(
        padding: const EdgeInsets.all(BosSpace.s16),
        children: [
          Text(
            l10n.tenantPickerSubtitle,
            style: Theme.of(context).textTheme.bodyLarge,
          ),
          const SizedBox(height: BosSpace.s16),
          if (inactive) ...[
            ErrorMessageBox(l10n.tenantInactiveNotice),
            const SizedBox(height: BosSpace.s16),
          ],
          for (final m in memberships) ...[
            _TenantTile(
              membership: m,
              current: m.tenantId == currentId,
              onTap: () => ref
                  .read(sessionControllerProvider.notifier)
                  .selectTenant(m.tenantId),
            ),
            const SizedBox(height: BosSpace.s8),
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
    final status = tenantStatusLabel(l10n, membership);
    final usable = status == null;
    final subtitle = [
      roleLabel(l10n, membership.role),
      ?status,
      if (current) l10n.tenantCurrent,
    ].join(' · ');

    return Card(
      child: ListTile(
        key: Key('tenant-${membership.tenantId}'),
        enabled: usable,
        onTap: usable ? onTap : null,
        leading: Icon(
          usable ? Icons.storefront_outlined : Icons.block,
          color: usable ? BosColors.brandPrimary : BosColors.textMuted,
        ),
        title: Text(
          membership.tenantName,
          style: Theme.of(context).textTheme.titleMedium,
        ),
        subtitle: Text(subtitle),
        trailing: usable ? const Icon(Icons.chevron_right) : null,
      ),
    );
  }
}
