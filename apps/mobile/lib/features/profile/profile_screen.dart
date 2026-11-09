import 'package:bayios_mobile/core/auth/auth_controller.dart';
import 'package:bayios_mobile/core/session/session_controller.dart';
import 'package:bayios_mobile/core/ui/components.dart';
import 'package:bayios_mobile/core/ui/tokens.dart';
import 'package:bayios_mobile/core/ui/widgets.dart';
import 'package:bayios_mobile/l10n/app_localizations.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

/// A read-only fact: small muted label above the value (the value is what
/// is read).
class _InfoRow extends StatelessWidget {
  const _InfoRow({
    required this.icon,
    required this.label,
    required this.value,
  });

  final IconData icon;
  final String label;
  final String value;

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.all(BosSpace.s16),
      child: Row(
        children: [
          IconBadge(icon: icon, size: 44),
          const SizedBox(width: BosSpace.s16),
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(
                  label,
                  style: BosType.caption.copyWith(color: BosColors.textMuted),
                ),
                const SizedBox(height: BosSpace.s2),
                Text(
                  value,
                  style: BosType.bodyLg.copyWith(
                    color: BosColors.textPrimary,
                    fontWeight: FontWeight.w600,
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

class ProfileScreen extends ConsumerWidget {
  const ProfileScreen({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final l10n = AppLocalizations.of(context);
    final session = ref.watch(sessionControllerProvider);
    if (session is! SessionReady) return const LoadingView();

    return Scaffold(
      appBar: AppBar(title: Text(l10n.profileTitle)),
      body: ListView(
        padding: const EdgeInsets.all(BosSpace.s16),
        children: [
          Card(
            child: Column(
              children: [
                _InfoRow(
                  icon: Icons.mail_outline,
                  label: l10n.profileEmailLabel,
                  value: session.me.email ?? '—',
                ),
                const Divider(height: 1),
                _InfoRow(
                  icon: Icons.storefront_outlined,
                  label: l10n.profileTenantLabel,
                  value: session.tenantName,
                ),
                const Divider(height: 1),
                _InfoRow(
                  icon: Icons.badge_outlined,
                  label: l10n.profileRoleLabel,
                  value: roleLabel(l10n, session.role),
                ),
              ],
            ),
          ),
          const SizedBox(height: BosSpace.s24),
          if (session.memberships.length > 1) ...[
            OutlinedButton.icon(
              key: const Key('profile-switch-tenant'),
              onPressed: () =>
                  ref.read(sessionControllerProvider.notifier).switchTenant(),
              icon: const Icon(Icons.swap_horiz),
              label: Text(l10n.switchTenant),
              style: OutlinedButton.styleFrom(
                minimumSize: const Size.fromHeight(BosSize.primaryAction),
              ),
            ),
            const SizedBox(height: BosSpace.s12),
          ],
          OutlinedButton.icon(
            key: const Key('profile-sign-out'),
            onPressed: () =>
                ref.read(authControllerProvider.notifier).signOut(),
            icon: const Icon(Icons.logout),
            label: Text(l10n.signOut),
            style: OutlinedButton.styleFrom(
              foregroundColor: BosColors.danger.fg,
              side: BorderSide(color: BosColors.danger.border),
              minimumSize: const Size.fromHeight(BosSize.primaryAction),
            ),
          ),
        ],
      ),
    );
  }
}
