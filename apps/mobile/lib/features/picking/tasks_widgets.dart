import 'package:bayios_mobile/core/ui/tokens.dart';
import 'package:bayios_mobile/features/picking/pick_session.dart';
import 'package:bayios_mobile/features/picking/picking_models.dart';
import 'package:bayios_mobile/l10n/app_localizations.dart';
import 'package:flutter/material.dart';

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
  const ToneBadge(this.text, this.tone, {super.key});

  final String text;
  final BosTone tone;

  @override
  Widget build(BuildContext context) {
    return DecoratedBox(
      decoration: BoxDecoration(
        color: tone.bg,
        border: Border.all(color: tone.border),
        borderRadius: BorderRadius.circular(BosRadius.md),
      ),
      child: Padding(
        padding: const EdgeInsets.symmetric(
          horizontal: BosSpace.s8,
          vertical: BosSpace.s2,
        ),
        child: Text(
          text,
          style: BosType.caption.copyWith(
            color: tone.fg,
            fontWeight: FontWeight.w600,
          ),
        ),
      ),
    );
  }
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
        borderRadius: BorderRadius.circular(BosRadius.sm),
        child: LinearProgressIndicator(
          value: value,
          minHeight: 8,
          backgroundColor: BosColors.surfaceSunken,
          color: BosColors.brandPrimary,
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
    return Card(
      key: Key('task-${task.id}'),
      clipBehavior: Clip.antiAlias,
      child: InkWell(
        onTap: onTap,
        child: ConstrainedBox(
          constraints: const BoxConstraints(minHeight: BosSize.primaryAction),
          child: Padding(
            padding: const EdgeInsets.all(BosSpace.s16),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Row(
                  children: [
                    Expanded(
                      child: Text(
                        l10n.tasksOrder(task.orderNumber),
                        style: theme.textTheme.titleMedium,
                      ),
                    ),
                    TaskStatusBadge(task.status),
                  ],
                ),
                const SizedBox(height: BosSpace.s4),
                Text(
                  [
                    if (task.type == 'putaway') l10n.tasksTypePutaway,
                    l10n.tasksLines(task.lineCount),
                    who,
                  ].join(' · '),
                  style: BosType.body.copyWith(color: BosColors.textSecondary),
                ),
                const SizedBox(height: BosSpace.s8),
                PickProgressBar(value: task.progress),
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
                      ),
                    const Icon(Icons.chevron_right, color: BosColors.textMuted),
                  ],
                ),
              ],
            ),
          ),
        ),
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
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return Padding(
      padding: const EdgeInsets.symmetric(
        horizontal: BosSpace.s24,
        vertical: BosSpace.s48,
      ),
      child: Column(
        children: [
          Icon(icon, size: 48, color: BosColors.textMuted),
          const SizedBox(height: BosSpace.s16),
          Text(
            title,
            textAlign: TextAlign.center,
            style: theme.textTheme.titleMedium,
          ),
          if (body != null) ...[
            const SizedBox(height: BosSpace.s8),
            Text(
              body!,
              textAlign: TextAlign.center,
              style: BosType.body.copyWith(color: BosColors.textSecondary),
            ),
          ],
        ],
      ),
    );
  }
}
