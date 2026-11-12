import 'package:bayios_mobile/core/session/access.dart';
import 'package:bayios_mobile/core/session/session_controller.dart';
import 'package:bayios_mobile/core/theme/app_icon.dart';
import 'package:bayios_mobile/core/theme/app_spacing.dart';
import 'package:bayios_mobile/core/ui/components.dart';
import 'package:bayios_mobile/core/ui/widgets.dart';
import 'package:bayios_mobile/features/picking/picking_api.dart';
import 'package:bayios_mobile/features/picking/picking_errors.dart';
import 'package:bayios_mobile/features/picking/picking_models.dart';
import 'package:bayios_mobile/features/picking/tasks_providers.dart';
import 'package:bayios_mobile/features/picking/tasks_widgets.dart';
import 'package:bayios_mobile/l10n/app_localizations.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

/// Tabs the permissions allow, in order.
List<TaskTab> tabsFor(Access access) => [
  if (access.canSome('tasks.read.own')) ...[TaskTab.assigned, TaskTab.pool],
  if (access.canSome('tasks.read.all')) TaskTab.all,
];

/// `?sekme=havuz|tumu` (dashboard quick links).
const tabQueryKey = 'sekme';

String tabQueryValue(TaskTab tab) => switch (tab) {
  TaskTab.assigned => 'bana',
  TaskTab.pool => 'havuz',
  TaskTab.all => 'tumu',
};

/// /gorevler — the warehouse work list (M02).
class TaskListScreen extends ConsumerWidget {
  const TaskListScreen({super.key, this.initialTab});

  final String? initialTab;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final l10n = AppLocalizations.of(context);
    final session = ref.watch(sessionControllerProvider);
    if (session is! SessionReady) return const LoadingView();
    final tabs = tabsFor(session.access);
    if (tabs.isEmpty) {
      // The route guard hides the feature; this is only a safety net.
      return Scaffold(
        appBar: AppTopBar(title: l10n.tasksTitle, trailing: const UserAvatar()),
        body: Center(child: Text(l10n.errorForbidden)),
      );
    }
    var initial = tabs.indexWhere((t) => tabQueryValue(t) == initialTab);
    if (initial < 0) initial = 0;
    return DefaultTabController(
      length: tabs.length,
      initialIndex: initial,
      child: Scaffold(
        appBar: AppTopBar(
          title: l10n.tasksTitle,
          trailing: const UserAvatar(),
          bottom: tabs.length < 2
              ? null
              : TabBar(
                  tabs: [
                    for (final t in tabs)
                      Tab(
                        key: Key('tasks-tab-${t.name}'),
                        height: AppSize.primaryAction,
                        text: _label(l10n, t),
                      ),
                  ],
                ),
        ),
        body: TabBarView(
          children: [
            for (final t in tabs)
              _TaskTabView(tab: t, userId: session.me.userId),
          ],
        ),
      ),
    );
  }

  static String _label(AppLocalizations l10n, TaskTab tab) => switch (tab) {
    TaskTab.assigned => l10n.tasksTabAssigned,
    TaskTab.pool => l10n.tasksTabPool,
    TaskTab.all => l10n.tasksTabAll,
  };
}

class _TaskTabView extends ConsumerStatefulWidget {
  const _TaskTabView({required this.tab, required this.userId});

  final TaskTab tab;
  final String userId;

  @override
  ConsumerState<_TaskTabView> createState() => _TaskTabViewState();
}

class _TaskTabViewState extends ConsumerState<_TaskTabView>
    with AutomaticKeepAliveClientMixin {
  /// Pages after the first ("Daha fazla göster", `tasks.read.all` only).
  /// [_page] is the paging info of the last loaded extra page.
  final _more = <PickingTaskSummary>[];
  FulfillmentPage? _page;
  bool _loadingMore = false;
  Object? _moreError;

  @override
  bool get wantKeepAlive => true;

  Future<void> _refresh() async {
    setState(_resetMore);
    await ref
        .refresh(tasksPageProvider(widget.tab).future)
        .then<void>((_) {}, onError: (Object _) {});
  }

  void _resetMore() {
    _more.clear();
    _page = null;
    _moreError = null;
  }

  Future<void> _loadMore(String cursor) async {
    if (_loadingMore) return;
    setState(() {
      _loadingMore = true;
      _moreError = null;
    });
    try {
      final page = await ref.read(pickingApiProvider).list(cursor: cursor);
      if (!mounted) return;
      setState(() {
        _more.addAll(page.items);
        _page = page.page;
      });
    } on Object catch (error) {
      if (mounted) setState(() => _moreError = error);
    } finally {
      if (mounted) setState(() => _loadingMore = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    super.build(context);
    final l10n = AppLocalizations.of(context);
    ref.listen(tasksPageProvider(widget.tab), (previous, next) {
      // A new first page (refresh / invalidation): later pages are stale.
      if (next.hasValue && !next.isLoading && _page != null) {
        setState(_resetMore);
      }
    });
    final async = ref.watch(tasksPageProvider(widget.tab));

    final Widget content;
    if (async.hasValue) {
      final first = async.requireValue;
      final paging = _page ?? first.page;
      final cursor = paging.hasMore ? paging.nextCursor : null;
      final items = [...first.items, ..._more];
      content = ListView(
        key: Key('tasks-list-${widget.tab.name}'),
        physics: const AlwaysScrollableScrollPhysics(),
        padding: const EdgeInsets.all(AppSpacing.md),
        children: [
          if (items.isEmpty)
            _empty(l10n)
          else
            for (final task in items) ...[
              TaskCard(
                task: task,
                userId: widget.userId,
                onTap: () => context.go(taskPath(task.id)),
              ),
              const SizedBox(height: AppSpacing.xs),
            ],
          if (_moreError != null)
            ErrorMessageBox(pickingErrorMessage(l10n, _moreError!)),
          if (cursor != null) ...[
            const SizedBox(height: AppSpacing.xs),
            OutlinedButton(
              key: const Key('tasks-load-more'),
              onPressed: _loadingMore ? null : () => _loadMore(cursor),
              style: OutlinedButton.styleFrom(
                minimumSize: const Size.fromHeight(AppSize.minTouchTarget),
              ),
              child: Text(_loadingMore ? l10n.loading : l10n.tasksLoadMore),
            ),
          ],
        ],
      );
    } else if (async.hasError) {
      content = ListView(
        physics: const AlwaysScrollableScrollPhysics(),
        children: [
          const SizedBox(height: AppSpacing.xxl),
          ErrorView(
            error: async.error!,
            onRetry: () => ref.invalidate(tasksPageProvider(widget.tab)),
          ),
        ],
      );
    } else {
      content = const ListLoadingView();
    }
    return RefreshIndicator(onRefresh: _refresh, child: content);
  }

  Widget _empty(AppLocalizations l10n) => switch (widget.tab) {
    TaskTab.assigned => TasksMessage(
      key: const Key('tasks-empty'),
      icon: AppIcons.packageOpen,
      title: l10n.tasksEmptyAssigned,
      body: l10n.tasksEmptyAssignedHint,
    ),
    TaskTab.pool => TasksMessage(
      key: const Key('tasks-empty'),
      icon: AppIcons.packageOpen,
      title: l10n.tasksEmptyPool,
      body: l10n.tasksEmptyHint,
    ),
    TaskTab.all => TasksMessage(
      key: const Key('tasks-empty'),
      icon: AppIcons.packageOpen,
      title: l10n.tasksEmptyAll,
      body: l10n.tasksEmptyHint,
    ),
  };
}
