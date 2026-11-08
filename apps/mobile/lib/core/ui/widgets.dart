import 'package:bayios_mobile/core/api/api_exception.dart';
import 'package:bayios_mobile/core/api/problem_messages.dart';
import 'package:bayios_mobile/core/providers.dart';
import 'package:bayios_mobile/core/ui/tokens.dart';
import 'package:bayios_mobile/l10n/app_localizations.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

/// Centered spinner with a label (screen readers announce the label).
class LoadingView extends StatelessWidget {
  const LoadingView({super.key, this.label});

  final String? label;

  @override
  Widget build(BuildContext context) {
    final text = label ?? AppLocalizations.of(context).loading;
    return Center(
      child: Semantics(
        liveRegion: true,
        label: text,
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            const CircularProgressIndicator(),
            const SizedBox(height: BosSpace.s16),
            ExcludeSemantics(child: Text(text)),
          ],
        ),
      ),
    );
  }
}

/// No HTTP response (offline, DNS, refused, timeout): the request may never
/// have reached the API, and it is worth retrying once the network is back.
bool isConnectionError(Object error) =>
    error is ApiException &&
    (error.kind == ApiErrorKind.network || error.kind == ApiErrorKind.timeout);

/// Re-runs a failed **load** by itself when the API becomes reachable again
/// (the offline banner clears: connectivity came back and the probe answered,
/// or any other call got a response). Only for connection errors and only
/// while the failure is on screen. Writes are never resent automatically:
/// they show "Tekrar gönder" and wait for the user.
class RefetchOnReconnect extends ConsumerWidget {
  const RefetchOnReconnect({
    required this.error,
    required this.onReconnect,
    required this.child,
    super.key,
  });

  final Object? error;
  final VoidCallback? onReconnect;
  final Widget child;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    ref.listen<bool>(networkStatusProvider, (previous, next) {
      final failure = error;
      if (previous == false &&
          next &&
          failure != null &&
          isConnectionError(failure)) {
        onReconnect?.call();
      }
    });
    return child;
  }
}

/// The retry pattern: "what happened + what to do" and a big "Tekrar dene"
/// button. Use it for every failed load (re-fetched by itself when the
/// connection returns, [RefetchOnReconnect]); for writes, the retry calls the
/// same `IdempotentAction.run()` so the Idempotency-Key is reused.
class ErrorView extends StatelessWidget {
  const ErrorView({required this.error, super.key, this.onRetry});

  final Object error;
  final VoidCallback? onRetry;

  @override
  Widget build(BuildContext context) {
    return RefetchOnReconnect(
      error: error,
      onReconnect: onRetry,
      child: _content(context),
    );
  }

  Widget _content(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final offline = isConnectionError(error);
    return Center(
      child: Padding(
        padding: const EdgeInsets.all(BosSpace.s24),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            Icon(
              offline ? Icons.cloud_off_outlined : Icons.error_outline,
              size: 48,
              color: BosColors.danger.fg,
            ),
            const SizedBox(height: BosSpace.s16),
            Semantics(
              liveRegion: true,
              child: Text(
                errorMessage(l10n, error),
                textAlign: TextAlign.center,
                style: Theme.of(context).textTheme.bodyLarge,
              ),
            ),
            if (onRetry != null) ...[
              const SizedBox(height: BosSpace.s24),
              FilledButton.icon(
                onPressed: onRetry,
                icon: const Icon(Icons.refresh),
                label: Text(l10n.retry),
                style: FilledButton.styleFrom(
                  minimumSize: const Size(200, BosSize.primaryAction),
                ),
              ),
            ],
          ],
        ),
      ),
    );
  }
}

/// Inline error message box (forms).
class ErrorMessageBox extends StatelessWidget {
  const ErrorMessageBox(this.message, {super.key});

  final String message;

  @override
  Widget build(BuildContext context) {
    return Semantics(
      liveRegion: true,
      container: true,
      child: DecoratedBox(
        decoration: BoxDecoration(
          color: BosColors.danger.bg,
          border: Border.all(color: BosColors.danger.border),
          borderRadius: BorderRadius.circular(BosRadius.lg),
        ),
        child: Padding(
          padding: const EdgeInsets.all(BosSpace.s12),
          child: Row(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Icon(Icons.error_outline, color: BosColors.danger.fg),
              const SizedBox(width: BosSpace.s8),
              Expanded(
                child: Text(
                  message,
                  style: TextStyle(color: BosColors.danger.fg),
                ),
              ),
            ],
          ),
        ),
      ),
    );
  }
}

/// Shown above the content while the API is unreachable. "Tekrar dene"
/// probes `GET /api/v1/meta`; any successful API response clears it.
class OfflineBanner extends ConsumerWidget {
  const OfflineBanner({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final reachable = ref.watch(networkStatusProvider);
    if (reachable) return const SizedBox.shrink();
    final l10n = AppLocalizations.of(context);
    return Material(
      color: BosColors.warning.bg,
      child: SafeArea(
        bottom: false,
        child: Semantics(
          liveRegion: true,
          container: true,
          child: Padding(
            padding: const EdgeInsets.symmetric(
              horizontal: BosSpace.s16,
              vertical: BosSpace.s4,
            ),
            child: Row(
              children: [
                Icon(Icons.wifi_off, color: BosColors.warning.fg),
                const SizedBox(width: BosSpace.s12),
                Expanded(
                  child: Text(
                    l10n.offlineBanner,
                    style: TextStyle(
                      color: BosColors.warning.fg,
                      fontWeight: FontWeight.w600,
                    ),
                  ),
                ),
                TextButton(
                  key: const Key('offline-banner-retry'),
                  onPressed: () =>
                      ref.read(networkStatusProvider.notifier).probe(),
                  style: TextButton.styleFrom(
                    foregroundColor: BosColors.warning.fg,
                  ),
                  child: Text(l10n.retry),
                ),
              ],
            ),
          ),
        ),
      ),
    );
  }
}

/// Role id → Turkish label (display only).
String roleLabel(AppLocalizations l10n, String role) => switch (role) {
  'owner' => l10n.roleOwner,
  'admin' => l10n.roleAdmin,
  'manager' => l10n.roleManager,
  'sales' => l10n.roleSales,
  'warehouse_staff' => l10n.roleWarehouseStaff,
  'viewer' => l10n.roleViewer,
  _ => l10n.roleUnknown,
};
