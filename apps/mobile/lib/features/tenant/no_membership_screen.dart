import 'package:bayios_mobile/core/auth/auth_controller.dart';
import 'package:bayios_mobile/core/session/session_controller.dart';
import 'package:bayios_mobile/core/ui/tokens.dart';
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
    final theme = Theme.of(context);
    return Scaffold(
      body: SafeArea(
        child: Padding(
          padding: const EdgeInsets.all(BosSpace.s24),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              const Spacer(),
              const Icon(
                Icons.store_mall_directory_outlined,
                size: 56,
                color: BosColors.textMuted,
              ),
              const SizedBox(height: BosSpace.s16),
              Text(
                l10n.noMembershipTitle,
                textAlign: TextAlign.center,
                style: theme.textTheme.headlineSmall,
              ),
              const SizedBox(height: BosSpace.s12),
              Text(
                l10n.noMembershipBody,
                textAlign: TextAlign.center,
                style: theme.textTheme.bodyLarge,
              ),
              const Spacer(),
              FilledButton.icon(
                key: const Key('no-membership-retry'),
                onPressed: () =>
                    ref.read(sessionControllerProvider.notifier).reload(),
                icon: const Icon(Icons.refresh),
                label: Text(l10n.retry),
                style: FilledButton.styleFrom(
                  minimumSize: const Size.fromHeight(BosSize.primaryAction),
                ),
              ),
              const SizedBox(height: BosSpace.s12),
              OutlinedButton.icon(
                key: const Key('no-membership-sign-out'),
                onPressed: () =>
                    ref.read(authControllerProvider.notifier).signOut(),
                icon: const Icon(Icons.logout),
                label: Text(l10n.signOut),
                style: OutlinedButton.styleFrom(
                  minimumSize: const Size.fromHeight(BosSize.primaryAction),
                ),
              ),
            ],
          ),
        ),
      ),
    );
  }
}
