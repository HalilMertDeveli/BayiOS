import 'package:bayios_mobile/core/auth/auth_controller.dart';
import 'package:bayios_mobile/core/session/session_controller.dart';
import 'package:bayios_mobile/core/ui/tokens.dart';
import 'package:bayios_mobile/core/ui/widgets.dart';
import 'package:bayios_mobile/l10n/app_localizations.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

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
                ListTile(
                  leading: const Icon(Icons.mail_outline),
                  title: Text(l10n.profileEmailLabel),
                  subtitle: Text(session.me.email ?? '—'),
                ),
                const Divider(height: 1),
                ListTile(
                  leading: const Icon(Icons.storefront_outlined),
                  title: Text(l10n.profileTenantLabel),
                  subtitle: Text(session.tenantName),
                ),
                const Divider(height: 1),
                ListTile(
                  leading: const Icon(Icons.badge_outlined),
                  title: Text(l10n.profileRoleLabel),
                  subtitle: Text(roleLabel(l10n, session.role)),
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
