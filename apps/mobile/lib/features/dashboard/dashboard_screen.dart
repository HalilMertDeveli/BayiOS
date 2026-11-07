import 'package:bayios_mobile/core/session/session_controller.dart';
import 'package:bayios_mobile/core/ui/tokens.dart';
import 'package:bayios_mobile/core/ui/widgets.dart';
import 'package:bayios_mobile/l10n/app_localizations.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

class DashboardScreen extends ConsumerWidget {
  const DashboardScreen({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final l10n = AppLocalizations.of(context);
    final theme = Theme.of(context);
    final session = ref.watch(sessionControllerProvider);
    if (session is! SessionReady) return const LoadingView();

    final name = session.me.fullName?.trim();
    return Scaffold(
      appBar: AppBar(title: Text(l10n.dashboardTitle)),
      body: RefreshIndicator(
        onRefresh: () => ref.read(sessionControllerProvider.notifier).reload(),
        child: ListView(
          padding: const EdgeInsets.all(BosSpace.s16),
          children: [
            Text(
              name == null || name.isEmpty
                  ? l10n.dashboardWelcomeAnonymous
                  : l10n.dashboardWelcome(name),
              style: theme.textTheme.headlineSmall,
            ),
            const SizedBox(height: BosSpace.s16),
            _InfoCard(
              key: const Key('dashboard-tenant'),
              icon: Icons.storefront_outlined,
              label: l10n.dashboardTenantLabel,
              value: session.tenantName,
            ),
            const SizedBox(height: BosSpace.s12),
            _InfoCard(
              key: const Key('dashboard-role'),
              icon: Icons.badge_outlined,
              label: l10n.dashboardRoleLabel,
              value: roleLabel(l10n, session.role),
            ),
            const SizedBox(height: BosSpace.s24),
            DecoratedBox(
              decoration: BoxDecoration(
                color: BosColors.info.bg,
                border: Border.all(color: BosColors.info.border),
                borderRadius: BorderRadius.circular(BosRadius.xl),
              ),
              child: Padding(
                padding: const EdgeInsets.all(BosSpace.s16),
                child: Row(
                  children: [
                    Icon(Icons.construction_outlined, color: BosColors.info.fg),
                    const SizedBox(width: BosSpace.s12),
                    Expanded(
                      child: Text(
                        l10n.dashboardPlaceholder,
                        style: TextStyle(color: BosColors.info.fg),
                      ),
                    ),
                  ],
                ),
              ),
            ),
          ],
        ),
      ),
    );
  }
}

class _InfoCard extends StatelessWidget {
  const _InfoCard({
    required this.icon,
    required this.label,
    required this.value,
    super.key,
  });

  final IconData icon;
  final String label;
  final String value;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return Card(
      child: Padding(
        padding: const EdgeInsets.all(BosSpace.s16),
        child: Row(
          children: [
            Icon(icon, color: BosColors.brandPrimary, size: 32),
            const SizedBox(width: BosSpace.s16),
            Expanded(
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text(
                    label,
                    style: theme.textTheme.bodySmall?.copyWith(
                      color: BosColors.textMuted,
                    ),
                  ),
                  const SizedBox(height: BosSpace.s4),
                  Text(value, style: theme.textTheme.titleMedium),
                ],
              ),
            ),
          ],
        ),
      ),
    );
  }
}
