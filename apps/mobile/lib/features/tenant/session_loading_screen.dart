import 'package:bayios_mobile/core/auth/auth_controller.dart';
import 'package:bayios_mobile/core/session/session_controller.dart';
import 'package:bayios_mobile/core/ui/widgets.dart';
import 'package:bayios_mobile/l10n/app_localizations.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

/// While `GET /me` loads; on failure the retry pattern (ErrorView).
class SessionLoadingScreen extends ConsumerWidget {
  const SessionLoadingScreen({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final l10n = AppLocalizations.of(context);
    final session = ref.watch(sessionControllerProvider);
    return Scaffold(
      body: SafeArea(
        child: switch (session) {
          SessionFailed(:final error) => Column(
            children: [
              Expanded(
                child: ErrorView(
                  error: error,
                  onRetry: () =>
                      ref.read(sessionControllerProvider.notifier).reload(),
                ),
              ),
              TextButton.icon(
                key: const Key('session-sign-out'),
                onPressed: () =>
                    ref.read(authControllerProvider.notifier).signOut(),
                icon: const Icon(Icons.logout),
                label: Text(l10n.signOut),
              ),
            ],
          ),
          _ => LoadingView(label: l10n.sessionLoading),
        },
      ),
    );
  }
}
