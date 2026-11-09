import 'package:bayios_mobile/features/picking/picking_api.dart';
import 'package:bayios_mobile/features/picking/picking_models.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_riverpod/misc.dart' show FutureProviderFamily;

/// Tabs of the work list, from the permissions (never role names):
/// `tasks.read.own` → assigned + pool (`GET /picking-tasks/mine`),
/// `tasks.read.all` → all active tasks (`GET /picking-tasks`).
enum TaskTab { assigned, pool, all }

Future<PickingTaskPage> _loadTab(PickingApi api, TaskTab tab) async =>
    switch (tab) {
      TaskTab.assigned => PickingTaskPage(
        items: await api.mine(MyTasksScope.assigned),
      ),
      TaskTab.pool => PickingTaskPage(items: await api.mine(MyTasksScope.pool)),
      TaskTab.all => await api.list(),
    };

/// First page of a tab. Pick-screen writes invalidate it.
final FutureProviderFamily<PickingTaskPage, TaskTab> tasksPageProvider =
    FutureProvider.autoDispose.family<PickingTaskPage, TaskTab>(
      (ref, tab) => _loadTab(ref.watch(pickingApiProvider), tab),
    );

/// One task for the pick screen's first load.
final FutureProviderFamily<PickingTask, String> pickingTaskProvider =
    FutureProvider.autoDispose.family<PickingTask, String>(
      (ref, id) => ref.watch(pickingApiProvider).get(id),
    );

/// The order behind a task (header info only; optional).
final FutureProviderFamily<PickOrderInfo, String> pickOrderInfoProvider =
    FutureProvider.autoDispose.family<PickOrderInfo, String>(
      (ref, orderId) => ref.watch(pickingApiProvider).order(orderId),
    );
