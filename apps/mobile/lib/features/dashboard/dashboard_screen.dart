import 'package:bayios_mobile/core/api/problem_messages.dart';
import 'package:bayios_mobile/core/auth/auth_controller.dart';
import 'package:bayios_mobile/core/session/access.dart';
import 'package:bayios_mobile/core/session/display_name.dart';
import 'package:bayios_mobile/core/session/session_controller.dart';
import 'package:bayios_mobile/core/ui/components.dart';
import 'package:bayios_mobile/core/ui/tokens.dart';
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
            _Header(
              greeting: name == null
                  ? l10n.dashboardWelcomeAnonymous
                  : l10n.dashboardWelcome(name),
              tenantName: session.tenantName,
              role: roleLabel(l10n, session.role),
            ),
            const SizedBox(height: BosSpace.s16),
            ..._tasks(context, ref, l10n, session, summary),
            if (actions.isNotEmpty) ...[
              SectionHeader(l10n.dashboardQuickLinks),
              _ActionGrid(actions: actions),
            ],
            const SizedBox(height: BosSpace.s24),
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
          icon: Icons.assignment_ind_outlined,
          label: l10n.dashboardLinkMyTasks,
          onTap: () => context.go('/gorevler'),
        ),
        _QuickAction(
          key: const Key('dashboard-link-pool'),
          icon: Icons.inbox_outlined,
          label: l10n.dashboardLinkPool,
          onTap: () => context.go(
            '/gorevler?$tabQueryKey=${tabQueryValue(TaskTab.pool)}',
          ),
        ),
      ],
      if (readAll)
        _QuickAction(
          key: const Key('dashboard-link-all'),
          icon: Icons.list_alt_outlined,
          label: l10n.dashboardLinkAllTasks,
          onTap: () => context.go(
            '/gorevler?$tabQueryKey=${tabQueryValue(TaskTab.all)}',
          ),
        ),
      if (access.canSome(StockPermissions.receive))
        _QuickAction(
          key: const Key('dashboard-link-receiving'),
          icon: Icons.move_to_inbox_outlined,
          label: l10n.receivingTitle,
          onTap: () => context.go('/stok/mal-kabul'),
        ),
      if (access.canSome(StockPermissions.read))
        _QuickAction(
          key: const Key('dashboard-link-stock'),
          icon: Icons.qr_code_scanner,
          label: l10n.stockTitle,
          onTap: () => context.go('/stok'),
        ),
      if (access.canSome(WarehousePermissions.receiveTransfer))
        _QuickAction(
          key: const Key('dashboard-link-transfers'),
          icon: Icons.local_shipping_outlined,
          label: l10n.transferListTitle,
          onTap: () => context.go('/depo/transferler'),
        ),
      if (access.canSome(WarehousePermissions.performCount))
        _QuickAction(
          key: const Key('dashboard-link-counts'),
          icon: Icons.fact_check_outlined,
          label: l10n.countListTitle,
          onTap: () => context.go('/depo/sayimlar'),
        ),
      if (access.canSome(WarehousePermissions.relocate))
        _QuickAction(
          key: const Key('dashboard-link-relocation'),
          icon: Icons.swap_vert,
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
        child: Card(
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
        ),
      );
    } else {
      body = const Padding(
        padding: EdgeInsets.all(BosSpace.s24),
        child: Center(child: CircularProgressIndicator()),
      );
    }

    return [body, const SizedBox(height: BosSpace.s16)];
  }
}

/// Greeting, business and role on the brand colour.
class _Header extends StatelessWidget {
  const _Header({
    required this.greeting,
    required this.tenantName,
    required this.role,
  });

  final String greeting;
  final String tenantName;
  final String role;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    return DecoratedBox(
      decoration: BoxDecoration(
        gradient: const LinearGradient(
          begin: Alignment.topLeft,
          end: Alignment.bottomRight,
          colors: [BosColors.brandPrimary, BosPetrol.p900],
        ),
        borderRadius: BorderRadius.circular(BosRadius.xl * 1.5),
      ),
      child: Padding(
        padding: const EdgeInsets.all(BosSpace.s20),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text(
              greeting,
              key: const Key('dashboard-welcome'),
              style: BosType.h2.copyWith(
                color: BosColors.brandOnPrimary,
                fontWeight: FontWeight.w700,
              ),
            ),
            const SizedBox(height: BosSpace.s12),
            Semantics(
              container: true,
              label: l10n.dashboardTenantLabel,
              child: Row(
                key: const Key('dashboard-tenant'),
                children: [
                  const Icon(
                    Icons.storefront_outlined,
                    color: BosPetrol.p100,
                    size: 22,
                  ),
                  const SizedBox(width: BosSpace.s8),
                  Expanded(
                    child: Text(
                      tenantName,
                      style: BosType.bodyLg.copyWith(
                        color: BosColors.brandOnPrimary,
                        fontWeight: FontWeight.w600,
                      ),
                    ),
                  ),
                ],
              ),
            ),
            const SizedBox(height: BosSpace.s8),
            Semantics(
              container: true,
              label: l10n.dashboardRoleLabel,
              child: DecoratedBox(
                key: const Key('dashboard-role'),
                decoration: BoxDecoration(
                  color: BosPetrol.p950,
                  borderRadius: BorderRadius.circular(BosRadius.xl * 2),
                ),
                child: Padding(
                  padding: const EdgeInsets.symmetric(
                    horizontal: BosSpace.s12,
                    vertical: BosSpace.s4,
                  ),
                  child: Row(
                    mainAxisSize: MainAxisSize.min,
                    children: [
                      const Icon(
                        Icons.badge_outlined,
                        color: BosPetrol.p100,
                        size: 18,
                      ),
                      const SizedBox(width: BosSpace.s4),
                      Text(
                        role,
                        style: BosType.caption.copyWith(
                          color: BosPetrol.p100,
                          fontWeight: FontWeight.w700,
                        ),
                      ),
                    ],
                  ),
                ),
              ),
            ),
            const SizedBox(height: BosSpace.s16),
            Container(
              width: 40,
              height: 4,
              decoration: BoxDecoration(
                color: BosColors.brandAccent,
                borderRadius: BorderRadius.circular(2),
              ),
            ),
          ],
        ),
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
          _Count(
            key: const Key('dashboard-count-active'),
            icon: Icons.list_alt_outlined,
            tone: BosColors.brand,
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
        SectionHeader(l10n.dashboardTasksTitle),
        Row(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            _Count(
              key: const Key('dashboard-count-assigned'),
              icon: Icons.assignment_ind_outlined,
              tone: BosColors.brand,
              label: l10n.dashboardCountAssigned,
              value: '${summary.assigned.length}',
            ),
            const SizedBox(width: BosSpace.s8),
            _Count(
              key: const Key('dashboard-count-in-progress'),
              icon: Icons.timelapse,
              tone: BosColors.warning,
              label: l10n.dashboardCountInProgress,
              value: '${summary.inProgress}',
            ),
            const SizedBox(width: BosSpace.s8),
            _Count(
              key: const Key('dashboard-count-pool'),
              icon: Icons.inbox_outlined,
              tone: BosColors.info,
              label: l10n.dashboardCountPool,
              value: '${summary.pool.length}',
            ),
          ],
        ),
        const SizedBox(height: BosSpace.s16),
        SectionHeader(l10n.dashboardOpenTasks),
        if (preview.isEmpty)
          Card(
            key: const Key('dashboard-no-tasks'),
            child: EmptyState(
              compact: true,
              icon: Icons.task_alt,
              tone: BosColors.success,
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
            const SizedBox(height: BosSpace.s8),
          ],
      ],
    );
  }
}

class _Count extends StatelessWidget {
  const _Count({
    required this.label,
    required this.value,
    required this.icon,
    required this.tone,
    super.key,
  });

  final String label;
  final String value;
  final IconData icon;
  final BosTone tone;

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
                // The number first: it is what the eye looks for.
                Row(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Expanded(child: Text(value, style: BosType.quantity)),
                    ExcludeSemantics(
                      child: IconBadge(icon: icon, tone: tone, size: 32),
                    ),
                  ],
                ),
                Text(
                  label,
                  style: BosType.caption.copyWith(
                    color: BosColors.textSecondary,
                    fontWeight: FontWeight.w600,
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

/// A big quick-action tile (two per row, glove-friendly).
class _QuickAction extends StatelessWidget {
  const _QuickAction({
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
    return Card(
      clipBehavior: Clip.antiAlias,
      child: InkWell(
        onTap: onTap,
        child: ConstrainedBox(
          constraints: const BoxConstraints(minHeight: 104),
          child: Padding(
            padding: const EdgeInsets.all(BosSpace.s12),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              mainAxisAlignment: MainAxisAlignment.spaceBetween,
              children: [
                IconBadge(icon: icon),
                const SizedBox(height: BosSpace.s12),
                Text(
                  label,
                  maxLines: 2,
                  style: BosType.body.copyWith(
                    color: BosColors.textPrimary,
                    fontWeight: FontWeight.w700,
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

class _ActionGrid extends StatelessWidget {
  const _ActionGrid({required this.actions});

  final List<_QuickAction> actions;

  @override
  Widget build(BuildContext context) {
    return LayoutBuilder(
      builder: (context, constraints) {
        const gap = BosSpace.s8;
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
