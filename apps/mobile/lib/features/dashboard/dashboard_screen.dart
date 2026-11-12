import 'package:bayios_mobile/core/api/problem_messages.dart';
import 'package:bayios_mobile/core/auth/auth_controller.dart';
import 'package:bayios_mobile/core/session/access.dart';
import 'package:bayios_mobile/core/session/display_name.dart';
import 'package:bayios_mobile/core/session/session_controller.dart';
import 'package:bayios_mobile/core/theme/app_colors.dart';
import 'package:bayios_mobile/core/theme/app_icon.dart';
import 'package:bayios_mobile/core/theme/app_spacing.dart';
import 'package:bayios_mobile/core/theme/app_theme.dart';
import 'package:bayios_mobile/core/ui/components.dart';
import 'package:bayios_mobile/core/ui/widgets.dart';
import 'package:bayios_mobile/features/dashboard/dashboard_data.dart';
import 'package:bayios_mobile/features/picking/task_list_screen.dart';
import 'package:bayios_mobile/features/picking/tasks_providers.dart';
import 'package:bayios_mobile/features/picking/tasks_widgets.dart';
import 'package:bayios_mobile/features/stock/stock_errors.dart';
import 'package:bayios_mobile/features/warehouse/warehouse_errors.dart';
import 'package:bayios_mobile/l10n/app_localizations.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

/// Pano (M01): who and where (greeting, business, role), my task counts and
/// next tasks, and big quick actions for the work the permissions allow.
/// Task data only when the permissions allow it.
class DashboardScreen extends ConsumerWidget {
  const DashboardScreen({super.key});

  /// How many of my tasks are listed (the rest is one tap away).
  static const previewCount = 3;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final l10n = AppLocalizations.of(context);
    final session = ref.watch(sessionControllerProvider);
    if (session is! SessionReady) return const LoadingView();

    final auth = ref.watch(authControllerProvider);
    final name = displayNameFor(
      session.me,
      auth is SignedIn ? auth.user : null,
    );
    final summary = ref.watch(dashboardSummaryProvider);
    final actions = _quickActions(context, l10n, session.access);
    return Scaffold(
      appBar: AppTopBar(
        title: l10n.dashboardTitle,
        trailing: const UserAvatar(),
      ),
      body: RefreshIndicator(
        onRefresh: () async {
          ref.invalidate(tasksPageProvider);
          await Future.wait([
            ref.read(sessionControllerProvider.notifier).reload(),
            ref
                .refresh(dashboardSummaryProvider.future)
                .then<void>((_) {}, onError: (_) {}),
          ]);
        },
        child: ListView(
          physics: const AlwaysScrollableScrollPhysics(),
          padding: const EdgeInsets.all(AppSpacing.md),
          children: [
            _Header(
              name: name ?? session.tenantName,
              greeting: name == null
                  ? l10n.dashboardWelcomeAnonymous
                  : l10n.dashboardWelcome(name),
              tenantName: session.tenantName,
              role: roleLabel(l10n, session.role),
            ),
            const SizedBox(height: AppSpacing.md),
            ..._tasks(context, ref, l10n, session, summary),
            if (actions.isNotEmpty) ...[
              SectionHeader(l10n.dashboardQuickLinks),
              _ActionGrid(actions: actions),
            ],
            const SizedBox(height: AppSpacing.lg),
          ],
        ),
      ),
    );
  }

  List<_QuickAction> _quickActions(
    BuildContext context,
    AppLocalizations l10n,
    Access access,
  ) {
    final readOwn = access.canSome('tasks.read.own');
    final readAll = access.canSome('tasks.read.all');
    return [
      if (readOwn) ...[
        _QuickAction(
          key: const Key('dashboard-link-mine'),
          icon: AppIcons.profile,
          label: l10n.dashboardLinkMyTasks,
          onTap: () => context.go('/gorevler'),
        ),
        _QuickAction(
          key: const Key('dashboard-link-pool'),
          icon: AppIcons.twoUser,
          label: l10n.dashboardLinkPool,
          onTap: () => context.go(
            '/gorevler?$tabQueryKey=${tabQueryValue(TaskTab.pool)}',
          ),
        ),
      ],
      if (readAll)
        _QuickAction(
          key: const Key('dashboard-link-all'),
          icon: AppIcons.document,
          label: l10n.dashboardLinkAllTasks,
          onTap: () => context.go(
            '/gorevler?$tabQueryKey=${tabQueryValue(TaskTab.all)}',
          ),
        ),
      if (access.canSome(StockPermissions.receive))
        _QuickAction(
          key: const Key('dashboard-link-receiving'),
          icon: AppIcons.trolley01,
          label: l10n.receivingTitle,
          onTap: () => context.go('/stok/mal-kabul'),
        ),
      if (access.canSome(StockPermissions.read))
        _QuickAction(
          key: const Key('dashboard-link-stock'),
          icon: AppIcons.scan,
          label: l10n.stockTitle,
          onTap: () => context.go('/stok'),
        ),
      if (access.canSome(WarehousePermissions.receiveTransfer))
        _QuickAction(
          key: const Key('dashboard-link-transfers'),
          icon: AppIcons.packageMoving,
          label: l10n.transferListTitle,
          onTap: () => context.go('/depo/transferler'),
        ),
      if (access.canSome(WarehousePermissions.performCount))
        _QuickAction(
          key: const Key('dashboard-link-counts'),
          icon: AppIcons.receiptBill2,
          label: l10n.countListTitle,
          onTap: () => context.go('/depo/sayimlar'),
        ),
      if (access.canSome(WarehousePermissions.relocate))
        _QuickAction(
          key: const Key('dashboard-link-relocation'),
          icon: AppIcons.trolley02,
          label: l10n.relocationTitle,
          onTap: () => context.go('/depo/raf-tasima'),
        ),
    ];
  }

  List<Widget> _tasks(
    BuildContext context,
    WidgetRef ref,
    AppLocalizations l10n,
    SessionReady session,
    AsyncValue<DashboardSummary?> summary,
  ) {
    final access = session.access;
    final readOwn = access.canSome('tasks.read.own');
    final readAll = access.canSome('tasks.read.all');
    if (!readOwn && !readAll) return const [];

    final Widget body;
    if (summary.hasValue && summary.requireValue != null) {
      body = _SummaryView(summary: summary.requireValue!, session: session);
    } else if (summary.hasError) {
      body = RefetchOnReconnect(
        error: summary.error,
        onReconnect: () => ref.invalidate(dashboardSummaryProvider),
        child: AppCard(
          key: const Key('dashboard-tasks-error'),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              ErrorMessageBox(
                '${l10n.dashboardTasksFailed} '
                '${errorMessage(l10n, summary.error!)}',
              ),
              const SizedBox(height: AppSpacing.xs),
              OutlinedButton.icon(
                key: const Key('dashboard-tasks-retry'),
                onPressed: () => ref.invalidate(dashboardSummaryProvider),
                icon: const AppIcon(AppIcons.refresh),
                label: Text(l10n.retry),
                style: OutlinedButton.styleFrom(
                  minimumSize: const Size.fromHeight(AppSize.minTouchTarget),
                ),
              ),
            ],
          ),
        ),
      );
    } else {
      body = const SkeletonList(count: 2);
    }

    return [body, const SizedBox(height: AppSpacing.md)];
  }
}

//// Who and where: initials avatar, greeting, business and role.
class _Header extends StatelessWidget {
  const _Header({
    required this.name,
    required this.greeting,
    required this.tenantName,
    required this.role,
  });

  final String name;
  final String greeting;
  final String tenantName;
  final String role;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final c = context.colors;
    return AppCard(
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          AppAvatar(name: name, size: AppSize.minTouchTarget),
          const SizedBox(width: AppSpacing.sm),
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(
                  greeting,
                  key: const Key('dashboard-welcome'),
                  style: context.text.titleLarge?.copyWith(
                    color: c.textPrimary,
                  ),
                ),
                const SizedBox(height: AppSpacing.xxs),
                Semantics(
                  container: true,
                  label: l10n.dashboardTenantLabel,
                  child: Row(
                    key: const Key('dashboard-tenant'),
                    children: [
                      AppIcon(
                        AppIcons.store03,
                        color: c.textMuted,
                        size: AppSize.iconSm,
                      ),
                      const SizedBox(width: AppSpacing.xxs),
                      Expanded(
                        child: Text(
                          tenantName,
                          style: context.text.bodySmall?.copyWith(
                            color: c.textSecondary,
                          ),
                        ),
                      ),
                    ],
                  ),
                ),
                const SizedBox(height: AppSpacing.xs),
                Semantics(
                  container: true,
                  label: l10n.dashboardRoleLabel,
                  child: StatusChip(
                    role,
                    Tone.brand,
                    key: const Key('dashboard-role'),
                    icon: AppIcons.userStory,
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

class _SummaryView extends StatelessWidget {
  const _SummaryView({required this.summary, required this.session});

  final DashboardSummary summary;
  final SessionReady session;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    if (!summary.readOwn) {
      final count = summary.activeAll ?? 0;
      return Row(
        children: [
          Expanded(
            child: KpiCard(
              key: const Key('dashboard-count-active'),
              icon: AppIcons.document,
              label: l10n.dashboardCountActive,
              value: summary.activeAllHasMore ? '$count+' : '$count',
            ),
          ),
        ],
      );
    }
    final preview = summary.assigned
        .take(DashboardScreen.previewCount)
        .toList();
    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        SectionHeader(l10n.dashboardTasksTitle),
        IntrinsicHeight(
          child: Row(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              Expanded(
                child: KpiCard(
                  key: const Key('dashboard-count-assigned'),
                  icon: AppIcons.profile,
                  label: l10n.dashboardCountAssigned,
                  value: '${summary.assigned.length}',
                ),
              ),
              const SizedBox(width: AppSpacing.xs),
              Expanded(
                child: KpiCard(
                  key: const Key('dashboard-count-in-progress'),
                  icon: AppIcons.timelapse,
                  tone: Tone.warning,
                  label: l10n.dashboardCountInProgress,
                  value: '${summary.inProgress}',
                ),
              ),
              const SizedBox(width: AppSpacing.xs),
              Expanded(
                child: KpiCard(
                  key: const Key('dashboard-count-pool'),
                  icon: AppIcons.twoUser,
                  tone: Tone.info,
                  label: l10n.dashboardCountPool,
                  value: '${summary.pool.length}',
                ),
              ),
            ],
          ),
        ),
        const SizedBox(height: AppSpacing.md),
        SectionHeader(l10n.dashboardOpenTasks),
        if (preview.isEmpty)
          AppCard(
            key: const Key('dashboard-no-tasks'),
            padding: EdgeInsets.zero,
            child: EmptyState(
              compact: true,
              icon: AppIcons.checkCircle1,
              tone: Tone.success,
              title: l10n.dashboardNoTasks,
              body: summary.pool.isNotEmpty
                  ? l10n.dashboardNoTasksPoolHint(summary.pool.length)
                  : null,
            ),
          )
        else
          for (final task in preview) ...[
            TaskCard(
              task: task,
              userId: session.me.userId,
              onTap: () => context.go(taskPath(task.id)),
            ),
            const SizedBox(height: AppSpacing.xs),
          ],
      ],
    );
  }
}

/// A big quick-action tile (two per row, glove-friendly).
class _QuickAction extends StatelessWidget {
  const _QuickAction({
    required this.icon,
    required this.label,
    required this.onTap,
    super.key,
  });

  final AppIconData icon;
  final String label;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    return AppCard(
      onTap: onTap,
      padding: const EdgeInsets.all(AppSpacing.sm),
      child: ConstrainedBox(
        constraints: const BoxConstraints(minHeight: 88),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          mainAxisAlignment: MainAxisAlignment.spaceBetween,
          children: [
            IconBadge(icon: icon),
            const SizedBox(height: AppSpacing.sm),
            Text(
              label,
              maxLines: 2,
              style: context.text.bodySmall?.semiBold.copyWith(
                color: context.colors.textPrimary,
              ),
            ),
          ],
        ),
      ),
    );
  }
}

class _ActionGrid extends StatelessWidget {
  const _ActionGrid({required this.actions});

  final List<_QuickAction> actions;

  @override
  Widget build(BuildContext context) {
    return LayoutBuilder(
      builder: (context, constraints) {
        const gap = AppSpacing.xs;
        final columns = constraints.maxWidth >= 560 ? 3 : 2;
        final width = (constraints.maxWidth - gap * (columns - 1)) / columns;
        return Wrap(
          spacing: gap,
          runSpacing: gap,
          children: [
            for (final action in actions) SizedBox(width: width, child: action),
          ],
        );
      },
    );
  }
}
