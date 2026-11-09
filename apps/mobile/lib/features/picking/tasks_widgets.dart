import 'package:bayios_mobile/core/ui/components.dart';
import 'package:bayios_mobile/core/ui/tokens.dart';
import 'package:bayios_mobile/features/picking/pick_session.dart';
import 'package:bayios_mobile/features/picking/picking_models.dart';
import 'package:bayios_mobile/l10n/app_localizations.dart';
import 'package:flutter/material.dart';
import 'package:intl/intl.dart';

/// Route of a task's pick screen.
String taskPath(String id) => '/gorevler/${Uri.encodeComponent(id)}';

(String, BosTone) taskStatusMeta(
  AppLocalizations l10n,
  String status,
) => switch (status) {
  PickingTaskStatus.open => (l10n.tasksStatusOpen, BosColors.info),
  PickingTaskStatus.inProgress => (
    l10n.tasksStatusInProgress,
    BosColors.warning,
  ),
  PickingTaskStatus.completed => (l10n.tasksStatusCompleted, BosColors.success),
  PickingTaskStatus.completedWithShortage => (
    l10n.tasksStatusCompletedWithShortage,
    BosColors.danger,
  ),
  PickingTaskStatus.cancelled => (l10n.tasksStatusCancelled, BosColors.neutral),
  _ => (status, BosColors.neutral),
};

/// Small coloured label (status, "Tamam", "2 eksik").
class ToneBadge extends StatelessWidget {
  const ToneBadge(this.text, this.tone, {super.key, this.icon});

  final String text;
  final BosTone tone;

  /// Optional leading glyph (status is never colour-only).
  final IconData? icon;

  @override
  Widget build(BuildContext context) {
    return DecoratedBox(
      decoration: BoxDecoration(
        color: tone.bg,
        border: Border.all(color: tone.border),
        borderRadius: BorderRadius.circular(BosRadius.xl * 2),
      ),
      child: Padding(
        padding: EdgeInsets.fromLTRB(
          icon == null ? BosSpace.s12 : BosSpace.s8,
          BosSpace.s4,
          BosSpace.s12,
          BosSpace.s4,
        ),
        child: Row(
          mainAxisSize: MainAxisSize.min,
          children: [
            if (icon != null) ...[
              Icon(icon, size: 16, color: tone.fg),
              const SizedBox(width: BosSpace.s4),
            ],
            Flexible(
              child: Text(
                text,
                style: BosType.caption.copyWith(
                  color: tone.fg,
                  fontWeight: FontWeight.w700,
                ),
              ),
            ),
          ],
        ),
      ),
    );
  }
}

/// Label and tone of a task priority; null for `normal` (no badge: most
/// tasks are normal, a badge on every card would be noise).
(String, BosTone)? taskPriorityMeta(AppLocalizations l10n, String priority) =>
    switch (priority) {
      TaskPriority.urgent => (l10n.tasksPriorityUrgent, BosColors.danger),
      TaskPriority.high => (l10n.tasksPriorityHigh, BosColors.warning),
      TaskPriority.low => (l10n.tasksPriorityLow, BosColors.neutral),
      _ => null,
    };

class TaskPriorityBadge extends StatelessWidget {
  const TaskPriorityBadge(this.priority, {super.key});

  final String priority;

  @override
  Widget build(BuildContext context) {
    final meta = taskPriorityMeta(AppLocalizations.of(context), priority);
    if (meta == null) return const SizedBox.shrink();
    return ToneBadge(
      meta.$1,
      meta.$2,
      key: Key('task-priority-$priority'),
      icon: switch (priority) {
        TaskPriority.urgent => Icons.bolt,
        TaskPriority.high => Icons.arrow_upward,
        _ => Icons.arrow_downward,
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
    return ToneBadge(label, tone);
  }
}

class PickProgressBar extends StatelessWidget {
  const PickProgressBar({required this.value, super.key});

  final double value;

  @override
  Widget build(BuildContext context) {
    return ExcludeSemantics(
      child: ClipRRect(
        borderRadius: BorderRadius.circular(BosRadius.xl),
        child: LinearProgressIndicator(
          value: value,
          minHeight: 10,
          backgroundColor: BosColors.surfaceSunken,
          // Finished work turns green: done at a glance.
          color: value >= 1 ? BosColors.success.fg : BosColors.brandPrimary,
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
    final theme = Theme.of(context);
    final who = task.assignedTo == null
        ? l10n.tasksInPool
        : task.assignedTo == userId
        ? l10n.tasksAssignedToMe
        : l10n.tasksAssignee(task.assigneeName ?? '—');
    final priority = taskPriorityMeta(l10n, task.priority);
    // Urgent / high work is marked on the card edge as well as by the badge
    // (colour is a second cue, never the only one).
    final stripe = switch (task.priority) {
      TaskPriority.urgent => BosColors.danger.fg,
      TaskPriority.high => BosColors.warning.border,
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
                : Border(left: BorderSide(color: stripe, width: 6)),
          ),
          child: ConstrainedBox(
            constraints: const BoxConstraints(minHeight: BosSize.primaryAction),
            child: Padding(
              padding: const EdgeInsets.fromLTRB(
                BosSpace.s16,
                BosSpace.s16,
                BosSpace.s8,
                BosSpace.s16,
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
                          style: theme.textTheme.titleMedium?.copyWith(
                            fontWeight: FontWeight.w700,
                          ),
                        ),
                      ),
                      const SizedBox(width: BosSpace.s8),
                      TaskStatusBadge(task.status),
                    ],
                  ),
                  if (priority != null || task.type == 'putaway') ...[
                    const SizedBox(height: BosSpace.s8),
                    Wrap(
                      spacing: BosSpace.s8,
                      runSpacing: BosSpace.s4,
                      children: [
                        TaskPriorityBadge(task.priority),
                        if (task.type == 'putaway')
                          ToneBadge(
                            l10n.tasksTypePutaway,
                            BosColors.purple,
                            icon: Icons.move_down,
                          ),
                      ],
                    ),
                  ],
                  if (customer != null)
                    _IconLine(
                      icon: Icons.storefront_outlined,
                      child: Text(
                        customer,
                        key: Key('task-customer-${task.id}'),
                        style: BosType.bodyLg.copyWith(
                          fontWeight: FontWeight.w600,
                        ),
                      ),
                    ),
                  _IconLine(
                    icon: task.assignedTo == null
                        ? Icons.inbox_outlined
                        : Icons.person_outline,
                    child: Text(
                      [l10n.tasksLines(task.lineCount), who].join(' · '),
                      style: BosType.body.copyWith(
                        color: BosColors.textSecondary,
                      ),
                    ),
                  ),
                  const SizedBox(height: BosSpace.s12),
                  Row(
                    children: [
                      Expanded(child: PickProgressBar(value: task.progress)),
                      const SizedBox(width: BosSpace.s12),
                      Text(
                        l10n.tasksPercent(percent),
                        style: BosType.body.copyWith(
                          fontWeight: FontWeight.w700,
                          fontFeatures: const [FontFeature.tabularFigures()],
                        ),
                      ),
                      const SizedBox(width: BosSpace.s4),
                    ],
                  ),
                  const SizedBox(height: BosSpace.s4),
                  Row(
                    children: [
                      Expanded(
                        child: Text(
                          l10n.tasksProgress(
                            formatQuantity(task.quantityPicked),
                            formatQuantity(task.quantityToPick),
                          ),
                          style: BosType.caption.copyWith(
                            color: BosColors.textMuted,
                          ),
                        ),
                      ),
                      if (task.quantityShort > 0)
                        ToneBadge(
                          l10n.tasksShort(formatQuantity(task.quantityShort)),
                          BosColors.danger,
                          icon: Icons.report_outlined,
                        ),
                      const Icon(
                        Icons.chevron_right,
                        color: BosColors.textMuted,
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

  final IconData icon;
  final Widget child;

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.only(top: BosSpace.s8),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Padding(
            padding: const EdgeInsets.only(top: 2),
            child: Icon(icon, size: 20, color: BosColors.textMuted),
          ),
          const SizedBox(width: BosSpace.s8),
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

  final IconData icon;
  final String title;
  final String? body;

  @override
  Widget build(BuildContext context) =>
      EmptyState(icon: icon, title: title, body: body);
}
