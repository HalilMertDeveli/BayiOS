import 'package:bayios_mobile/core/theme/app_colors.dart';
import 'package:bayios_mobile/core/theme/app_icon.dart';
import 'package:bayios_mobile/core/theme/app_spacing.dart';
import 'package:bayios_mobile/core/theme/app_theme.dart';
import 'package:bayios_mobile/core/ui/components.dart';
import 'package:bayios_mobile/features/picking/pick_session.dart';
import 'package:bayios_mobile/features/picking/picking_models.dart';
import 'package:bayios_mobile/l10n/app_localizations.dart';
import 'package:flutter/material.dart';
import 'package:intl/intl.dart';

/// Route of a task's pick screen.
String taskPath(String id) => '/gorevler/${Uri.encodeComponent(id)}';

(String, Tone) taskStatusMeta(AppLocalizations l10n, String status) =>
    switch (status) {
      PickingTaskStatus.open => (l10n.tasksStatusOpen, Tone.info),
      PickingTaskStatus.inProgress => (
        l10n.tasksStatusInProgress,
        Tone.warning,
      ),
      PickingTaskStatus.completed => (l10n.tasksStatusCompleted, Tone.success),
      PickingTaskStatus.completedWithShortage => (
        l10n.tasksStatusCompletedWithShortage,
        Tone.danger,
      ),
      PickingTaskStatus.cancelled => (l10n.tasksStatusCancelled, Tone.neutral),
      _ => (status, Tone.neutral),
    };

/// Label and tone of a task priority; null for `normal` (no badge: most
/// tasks are normal, a badge on every card would be noise).
(String, Tone)? taskPriorityMeta(AppLocalizations l10n, String priority) =>
    switch (priority) {
      TaskPriority.urgent => (l10n.tasksPriorityUrgent, Tone.danger),
      TaskPriority.high => (l10n.tasksPriorityHigh, Tone.warning),
      TaskPriority.low => (l10n.tasksPriorityLow, Tone.neutral),
      _ => null,
    };

class TaskPriorityBadge extends StatelessWidget {
  const TaskPriorityBadge(this.priority, {super.key});

  final String priority;

  @override
  Widget build(BuildContext context) {
    final meta = taskPriorityMeta(AppLocalizations.of(context), priority);
    if (meta == null) return const SizedBox.shrink();
    return StatusChip(
      meta.$1,
      meta.$2,
      key: Key('task-priority-$priority'),
      icon: switch (priority) {
        TaskPriority.urgent => AppIcons.bolt,
        TaskPriority.high => AppIcons.arrowUp2,
        _ => AppIcons.arrowDown2,
      },
    );
  }
}

final _deliveryDate = DateFormat('d MMM', 'tr');

/// "Müşteri · Teslim 12 Kas" (whatever is known), or null.
String? taskCustomerLine(AppLocalizations l10n, PickingTaskSummary task) {
  final parts = [
    if (task.customerName case final String name when name.isNotEmpty) name,
    if (task.requestedDeliveryDate case final DateTime date)
      l10n.tasksDeliveryDate(_deliveryDate.format(date)),
  ];
  return parts.isEmpty ? null : parts.join(' · ');
}

class TaskStatusBadge extends StatelessWidget {
  const TaskStatusBadge(this.status, {super.key});

  final String status;

  @override
  Widget build(BuildContext context) {
    final (label, tone) = taskStatusMeta(AppLocalizations.of(context), status);
    return StatusChip(label, tone);
  }
}

class PickProgressBar extends StatelessWidget {
  const PickProgressBar({required this.value, super.key});

  final double value;

  @override
  Widget build(BuildContext context) {
    final c = context.colors;
    return ExcludeSemantics(
      child: ClipRRect(
        borderRadius: BorderRadius.circular(AppRadius.full),
        child: LinearProgressIndicator(
          value: value,
          minHeight: 8,
          backgroundColor: c.chartTrack,
          // Finished work turns green: done at a glance.
          color: value >= 1 ? c.success.fg : c.brandAccent,
        ),
      ),
    );
  }
}

/// One task in the work list / on the dashboard; the whole card is the tap
/// target (≥ 48 dp).
class TaskCard extends StatelessWidget {
  const TaskCard({
    required this.task,
    required this.userId,
    required this.onTap,
    super.key,
  });

  final PickingTaskSummary task;
  final String userId;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final c = context.colors;
    final who = task.assignedTo == null
        ? l10n.tasksInPool
        : task.assignedTo == userId
        ? l10n.tasksAssignedToMe
        : l10n.tasksAssignee(task.assigneeName ?? '—');
    final priority = taskPriorityMeta(l10n, task.priority);
    // Urgent / high work is marked on the card edge as well as by the badge
    // (colour is a second cue, never the only one).
    final stripe = switch (task.priority) {
      TaskPriority.urgent => c.danger.fg,
      TaskPriority.high => c.warning.border,
      _ => null,
    };
    final customer = taskCustomerLine(l10n, task);
    final percent = (task.progress.clamp(0, 1) * 100).round();
    return Card(
      key: Key('task-${task.id}'),
      clipBehavior: Clip.antiAlias,
      child: InkWell(
        onTap: onTap,
        child: DecoratedBox(
          decoration: BoxDecoration(
            border: stripe == null
                ? null
                : Border(left: BorderSide(color: stripe, width: 4)),
          ),
          child: ConstrainedBox(
            constraints: const BoxConstraints(minHeight: AppSize.primaryAction),
            child: Padding(
              padding: const EdgeInsets.fromLTRB(
                AppSpacing.md,
                AppSpacing.md,
                AppSpacing.sm,
                AppSpacing.md,
              ),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Row(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Expanded(
                        child: Text(
                          l10n.tasksOrder(task.orderNumber),
                          style: context.text.titleMedium?.copyWith(
                            color: c.textPrimary,
                          ),
                        ),
                      ),
                      const SizedBox(width: AppSpacing.xs),
                      TaskStatusBadge(task.status),
                    ],
                  ),
                  if (priority != null || task.type == 'putaway') ...[
                    const SizedBox(height: AppSpacing.xs),
                    Wrap(
                      spacing: AppSpacing.xs,
                      runSpacing: AppSpacing.xxs,
                      children: [
                        TaskPriorityBadge(task.priority),
                        if (task.type == 'putaway')
                          StatusChip(
                            l10n.tasksTypePutaway,
                            Tone.purple,
                            icon: AppIcons.rightDownLine,
                          ),
                      ],
                    ),
                  ],
                  if (customer != null)
                    _IconLine(
                      icon: AppIcons.store03,
                      child: Text(
                        customer,
                        key: Key('task-customer-${task.id}'),
                        style: context.text.bodyMedium?.medium.copyWith(
                          color: c.textPrimary,
                        ),
                      ),
                    ),
                  _IconLine(
                    icon: task.assignedTo == null
                        ? AppIcons.twoUser
                        : AppIcons.profile,
                    child: Text(
                      [l10n.tasksLines(task.lineCount), who].join(' · '),
                      style: context.text.bodySmall?.copyWith(
                        color: c.textMuted,
                      ),
                    ),
                  ),
                  const SizedBox(height: AppSpacing.sm),
                  Row(
                    children: [
                      Expanded(child: PickProgressBar(value: task.progress)),
                      const SizedBox(width: AppSpacing.sm),
                      Text(
                        l10n.tasksPercent(percent),
                        style: context.text.bodySmall?.semiBold.tabular
                            .copyWith(color: c.textPrimary),
                      ),
                    ],
                  ),
                  const SizedBox(height: AppSpacing.xs),
                  Row(
                    children: [
                      Expanded(
                        child: Text(
                          l10n.tasksProgress(
                            formatQuantity(task.quantityPicked),
                            formatQuantity(task.quantityToPick),
                          ),
                          style: context.text.labelMedium?.copyWith(
                            color: c.textMuted,
                          ),
                        ),
                      ),
                      if (task.quantityShort > 0)
                        StatusChip(
                          l10n.tasksShort(formatQuantity(task.quantityShort)),
                          Tone.danger,
                          icon: AppIcons.infoCircle,
                        ),
                      const SizedBox(width: AppSpacing.xxs),
                      AppIcon(
                        AppIcons.arrowRight2,
                        size: AppSize.icon,
                        color: c.textMuted,
                      ),
                    ],
                  ),
                ],
              ),
            ),
          ),
        ),
      ),
    );
  }
}

/// A small leading icon and its text (customer, assignee).
class _IconLine extends StatelessWidget {
  const _IconLine({required this.icon, required this.child});

  final AppIconData icon;
  final Widget child;

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.only(top: AppSpacing.xs),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Padding(
            padding: const EdgeInsets.only(top: 1),
            child: AppIcon(
              icon,
              size: AppSize.iconSm,
              color: context.colors.textMuted,
            ),
          ),
          const SizedBox(width: AppSpacing.xs),
          Expanded(child: child),
        ],
      ),
    );
  }
}

/// Empty / no-access state inside a scrollable (pull-to-refresh still works).
class TasksMessage extends StatelessWidget {
  const TasksMessage({
    required this.icon,
    required this.title,
    this.body,
    super.key,
  });

  final AppIconData icon;
  final String title;
  final String? body;

  @override
  Widget build(BuildContext context) =>
      EmptyState(icon: icon, title: title, body: body);
}
