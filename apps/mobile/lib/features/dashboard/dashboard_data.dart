import 'package:bayios_mobile/core/session/session_controller.dart';
import 'package:bayios_mobile/features/picking/picking_api.dart';
import 'package:bayios_mobile/features/picking/picking_models.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

/// What the dashboard shows about warehouse tasks.
class DashboardSummary {
  const DashboardSummary({
    required this.readOwn,
    required this.assigned,
    required this.pool,
    this.activeAll,
    this.activeAllHasMore = false,
  });

  /// `tasks.read.own`: [assigned] and [pool] come from `/picking-tasks/mine`.
  final bool readOwn;

  /// My active tasks (open + in progress), pick order of the API.
  final List<PickingTaskSummary> assigned;

  /// Active, unassigned tasks.
  final List<PickingTaskSummary> pool;

  /// `tasks.read.all` without `tasks.read.own` (e.g. viewer): active tasks
  /// of the tenant (first page).
  final int? activeAll;
  final bool activeAllHasMore;

  int get inProgress =>
      assigned.where((t) => t.status == PickingTaskStatus.inProgress).length;
}

/// Null when the permissions show no task data at all.
final FutureProvider<DashboardSummary?> dashboardSummaryProvider =
    FutureProvider.autoDispose<DashboardSummary?>(_loadSummary);

Future<DashboardSummary?> _loadSummary(Ref ref) async {
  final session = ref.watch(sessionControllerProvider);
  if (session is! SessionReady) return null;
  final access = session.access;
  final api = ref.watch(pickingApiProvider);
  if (access.canSome('tasks.read.own')) {
    final items = await api.mine(MyTasksScope.all);
    final me = session.me.userId;
    return DashboardSummary(
      readOwn: true,
      assigned: [
        for (final t in items)
          if (t.assignedTo == me) t,
      ],
      pool: [
        for (final t in items)
          if (t.assignedTo == null) t,
      ],
    );
  }
  if (access.canSome('tasks.read.all')) {
    final page = await api.list();
    return DashboardSummary(
      readOwn: false,
      assigned: const [],
      pool: const [],
      activeAll: page.items.length,
      activeAllHasMore: page.page.hasMore,
    );
  }
  return null;
}
