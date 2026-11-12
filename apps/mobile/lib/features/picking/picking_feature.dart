import 'package:bayios_mobile/app/feature.dart';
import 'package:bayios_mobile/core/theme/app_icon.dart';
import 'package:bayios_mobile/features/picking/pick_screen.dart';
import 'package:bayios_mobile/features/picking/picking_api.dart';
import 'package:bayios_mobile/features/picking/task_list_screen.dart';
import 'package:go_router/go_router.dart';

/// Görevler — work list (#41) and the pick screen with scanning (#42).
/// Visible with `tasks.read.own` (assigned + pool) or `tasks.read.all`.
final pickingFeature = AppFeature(
  id: 'tasks',
  path: '/gorevler',
  label: (l10n) => l10n.tasksNavLabel,
  icon: AppIcons.document,
  selectedIcon: AppIcons.document,
  isVisible: (access) => access.canAny(['tasks.read.own', 'tasks.read.all']),
  builder: (context, state) =>
      TaskListScreen(initialTab: state.uri.queryParameters[tabQueryKey]),
  routes: [
    GoRoute(
      path: ':id',
      builder: (context, state) =>
          PickScreen(taskId: state.pathParameters['id']!),
    ),
  ],
  endpoints: PickingEndpoints.all,
  schemas: PickingEndpoints.schemas,
);
