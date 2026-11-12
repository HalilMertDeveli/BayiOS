import 'package:bayios_mobile/core/api/api_exception.dart';
import 'package:bayios_mobile/core/api/problem_messages.dart';
import 'package:bayios_mobile/core/auth/auth_controller.dart';
import 'package:bayios_mobile/core/providers.dart';
import 'package:bayios_mobile/core/session/display_name.dart';
import 'package:bayios_mobile/core/session/session_controller.dart';
import 'package:bayios_mobile/core/theme/app_colors.dart';
import 'package:bayios_mobile/core/theme/app_icon.dart';
import 'package:bayios_mobile/core/theme/app_spacing.dart';
import 'package:bayios_mobile/core/theme/app_theme.dart';
import 'package:bayios_mobile/core/ui/components.dart';
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
            const SizedBox.square(
              dimension: 36,
              child: CircularProgressIndicator(strokeWidth: 3),
            ),
            const SizedBox(height: AppSpacing.md),
            ExcludeSemantics(
              child: Text(
                text,
                style: context.text.bodySmall?.copyWith(
                  color: context.colors.textMuted,
                ),
              ),
            ),
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
      child: EmptyState(
        icon: offline ? AppIcons.offline : AppIcons.infoCircle,
        tone: offline ? Tone.warning : Tone.danger,
        title: errorMessage(l10n, error),
        action: onRetry == null
            ? null
            : FilledButton.icon(
                onPressed: onRetry,
                icon: const AppIcon(AppIcons.refresh),
                label: Text(l10n.retry),
                style: FilledButton.styleFrom(
                  minimumSize: const Size(200, AppSize.minTouchTarget),
                ),
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
    final danger = context.colors.danger;
    return Semantics(
      liveRegion: true,
      container: true,
      child: DecoratedBox(
        decoration: BoxDecoration(
          color: danger.bg,
          border: Border.all(color: danger.border),
          borderRadius: AppRadius.mdAll,
        ),
        child: Padding(
          padding: const EdgeInsets.all(AppSpacing.sm),
          child: Row(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              AppIcon(
                AppIcons.infoCircle,
                color: danger.fg,
                size: AppSize.icon,
              ),
              const SizedBox(width: AppSpacing.xs),
              Expanded(
                child: Text(
                  message,
                  style: context.text.bodySmall?.medium.copyWith(
                    color: danger.fg,
                  ),
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
    final warning = context.colors.warning;
    return Material(
      color: warning.bg,
      child: SafeArea(
        bottom: false,
        child: Semantics(
          liveRegion: true,
          container: true,
          child: Padding(
            padding: const EdgeInsets.symmetric(
              horizontal: AppSpacing.md,
              vertical: AppSpacing.xxs,
            ),
            child: Row(
              children: [
                AppIcon(
                  AppIcons.wifiOff,
                  color: warning.fg,
                  size: AppSize.icon,
                ),
                const SizedBox(width: AppSpacing.sm),
                Expanded(
                  child: Text(
                    l10n.offlineBanner,
                    style: context.text.bodySmall?.semiBold.copyWith(
                      color: warning.fg,
                    ),
                  ),
                ),
                TextButton(
                  key: const Key('offline-banner-retry'),
                  onPressed: () =>
                      ref.read(networkStatusProvider.notifier).probe(),
                  style: TextButton.styleFrom(foregroundColor: warning.fg),
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

/// The signed-in user's initials avatar (top bar of the top-level pages).
class UserAvatar extends ConsumerWidget {
  const UserAvatar({super.key, this.size = 40});

  final double size;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final session = ref.watch(sessionControllerProvider);
    if (session is! SessionReady) return const SizedBox.shrink();
    final auth = ref.watch(authControllerProvider);
    final name =
        displayNameFor(session.me, auth is SignedIn ? auth.user : null) ??
        session.tenantName;
    return AppAvatar(name: name, size: size);
  }
}

/// Loading state of a list page: card-shaped skeletons (announced as
/// "Yükleniyor…"), scrollable so pull-to-refresh keeps working.
class ListLoadingView extends StatelessWidget {
  const ListLoadingView({super.key, this.count = 4});

  final int count;

  @override
  Widget build(BuildContext context) => ListView(
    physics: const AlwaysScrollableScrollPhysics(),
    padding: const EdgeInsets.all(AppSpacing.md),
    children: [SkeletonList(count: count)],
  );
}
