import 'package:bayios_mobile/core/api/problem_messages.dart';
import 'package:bayios_mobile/core/auth/auth_controller.dart';
import 'package:bayios_mobile/core/session/display_name.dart';
import 'package:bayios_mobile/core/session/session_controller.dart';
import 'package:bayios_mobile/core/ui/tokens.dart';
import 'package:bayios_mobile/core/ui/widgets.dart';
import 'package:bayios_mobile/features/dashboard/dashboard_data.dart';
import 'package:bayios_mobile/features/picking/task_list_screen.dart';
import 'package:bayios_mobile/features/picking/tasks_providers.dart';
import 'package:bayios_mobile/features/picking/tasks_widgets.dart';
import 'package:bayios_mobile/l10n/app_localizations.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

/// Pano (M01): greeting, my open tasks with counts, quick links, tenant and
/// role. Task data only when the permissions allow it.
class DashboardScreen extends ConsumerWidget {
  const DashboardScreen({super.key});

  /// How many of my tasks are listed (the rest is one tap away).
  static const previewCount = 3;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final l10n = AppLocalizations.of(context);
    final theme = Theme.of(context);
    final session = ref.watch(sessionControllerProvider);
    if (session is! SessionReady) return const LoadingView();

    final auth = ref.watch(authControllerProvider);
    final name = displayNameFor(
      session.me,
      auth is SignedIn ? auth.user : null,
    );
    final summary = ref.watch(dashboardSummaryProvider);
    return Scaffold(
      appBar: AppBar(title: Text(l10n.dashboardTitle)),
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
          padding: const EdgeInsets.all(BosSpace.s16),
          children: [
            Text(
              name == null
                  ? l10n.dashboardWelcomeAnonymous
                  : l10n.dashboardWelcome(name),
              key: const Key('dashboard-welcome'),
              style: theme.textTheme.headlineSmall,
            ),
            const SizedBox(height: BosSpace.s16),
            ..._tasks(context, ref, l10n, session, summary),
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
          ],
        ),
      ),
    );
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

    final links = <Widget>[
      if (readOwn) ...[
        _QuickLink(
          key: const Key('dashboard-link-mine'),
          icon: Icons.assignment_ind_outlined,
          label: l10n.dashboardLinkMyTasks,
          onTap: () => context.go('/gorevler'),
        ),
        _QuickLink(
          key: const Key('dashboard-link-pool'),
          icon: Icons.inventory_2_outlined,
          label: l10n.dashboardLinkPool,
          onTap: () => context.go(
            '/gorevler?$tabQueryKey=${tabQueryValue(TaskTab.pool)}',
          ),
        ),
      ],
      if (readAll)
        _QuickLink(
          key: const Key('dashboard-link-all'),
          icon: Icons.list_alt_outlined,
          label: l10n.dashboardLinkAllTasks,
          onTap: () => context.go(
            '/gorevler?$tabQueryKey=${tabQueryValue(TaskTab.all)}',
          ),
        ),
    ];

    final Widget body;
    if (summary.hasValue && summary.requireValue != null) {
      body = _SummaryView(summary: summary.requireValue!, session: session);
    } else if (summary.hasError) {
      body = Card(
        key: const Key('dashboard-tasks-error'),
        child: Padding(
          padding: const EdgeInsets.all(BosSpace.s16),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              ErrorMessageBox(
                '${l10n.dashboardTasksFailed} '
                '${errorMessage(l10n, summary.error!)}',
              ),
              const SizedBox(height: BosSpace.s8),
              OutlinedButton.icon(
                key: const Key('dashboard-tasks-retry'),
                onPressed: () => ref.invalidate(dashboardSummaryProvider),
                icon: const Icon(Icons.refresh),
                label: Text(l10n.retry),
                style: OutlinedButton.styleFrom(
                  minimumSize: const Size.fromHeight(BosSize.minTouchTarget),
                ),
              ),
            ],
          ),
        ),
      );
    } else {
      body = const Padding(
        padding: EdgeInsets.all(BosSpace.s24),
        child: Center(child: CircularProgressIndicator()),
      );
    }

    return [
      body,
      const SizedBox(height: BosSpace.s16),
      Text(
        l10n.dashboardQuickLinks,
        style: Theme.of(context).textTheme.titleMedium,
      ),
      const SizedBox(height: BosSpace.s8),
      Card(
        clipBehavior: Clip.antiAlias,
        child: Column(
          children: [
            for (var i = 0; i < links.length; i++) ...[
              if (i > 0) const Divider(height: 1),
              links[i],
            ],
          ],
        ),
      ),
      const SizedBox(height: BosSpace.s24),
    ];
  }
}

class _SummaryView extends StatelessWidget {
  const _SummaryView({required this.summary, required this.session});

  final DashboardSummary summary;
  final SessionReady session;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final theme = Theme.of(context);
    if (!summary.readOwn) {
      final count = summary.activeAll ?? 0;
      return Row(
        children: [
          _Count(
            key: const Key('dashboard-count-active'),
            label: l10n.dashboardCountActive,
            value: summary.activeAllHasMore ? '$count+' : '$count',
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
        Text(l10n.dashboardTasksTitle, style: theme.textTheme.titleMedium),
        const SizedBox(height: BosSpace.s8),
        Row(
          children: [
            _Count(
              key: const Key('dashboard-count-assigned'),
              label: l10n.dashboardCountAssigned,
              value: '${summary.assigned.length}',
            ),
            const SizedBox(width: BosSpace.s8),
            _Count(
              key: const Key('dashboard-count-in-progress'),
              label: l10n.dashboardCountInProgress,
              value: '${summary.inProgress}',
            ),
            const SizedBox(width: BosSpace.s8),
            _Count(
              key: const Key('dashboard-count-pool'),
              label: l10n.dashboardCountPool,
              value: '${summary.pool.length}',
            ),
          ],
        ),
        const SizedBox(height: BosSpace.s16),
        Text(l10n.dashboardOpenTasks, style: theme.textTheme.titleMedium),
        const SizedBox(height: BosSpace.s8),
        if (preview.isEmpty)
          Card(
            key: const Key('dashboard-no-tasks'),
            child: Padding(
              padding: const EdgeInsets.all(BosSpace.s16),
              child: Text(
                [
                  l10n.dashboardNoTasks,
                  if (summary.pool.isNotEmpty)
                    l10n.dashboardNoTasksPoolHint(summary.pool.length),
                ].join(' '),
                style: BosType.body,
              ),
            ),
          )
        else
          for (final task in preview) ...[
            TaskCard(
              task: task,
              userId: session.me.userId,
              onTap: () => context.go(taskPath(task.id)),
            ),
            const SizedBox(height: BosSpace.s8),
          ],
      ],
    );
  }
}

class _Count extends StatelessWidget {
  const _Count({required this.label, required this.value, super.key});

  final String label;
  final String value;

  @override
  Widget build(BuildContext context) {
    return Expanded(
      child: Card(
        child: Padding(
          padding: const EdgeInsets.all(BosSpace.s12),
          child: Semantics(
            container: true,
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(value, style: BosType.quantity),
                Text(
                  label,
                  style: BosType.caption.copyWith(
                    color: BosColors.textSecondary,
                  ),
                ),
              ],
            ),
          ),
        ),
      ),
    );
  }
}

class _QuickLink extends StatelessWidget {
  const _QuickLink({
    required this.icon,
    required this.label,
    required this.onTap,
    super.key,
  });

  final IconData icon;
  final String label;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    return ListTile(
      minTileHeight: BosSize.primaryAction,
      leading: Icon(icon, color: BosColors.brandPrimary),
      title: Text(label),
      trailing: const Icon(Icons.chevron_right),
      onTap: onTap,
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
