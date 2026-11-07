import 'dart:async';

import 'package:bayios_mobile/core/api/api_exception.dart';
import 'package:bayios_mobile/core/session/session_controller.dart';
import 'package:bayios_mobile/core/ui/tokens.dart';
import 'package:bayios_mobile/core/ui/widgets.dart';
import 'package:bayios_mobile/features/dashboard/dashboard_data.dart';
import 'package:bayios_mobile/features/picking/camera_scanner.dart';
import 'package:bayios_mobile/features/picking/pick_controller.dart';
import 'package:bayios_mobile/features/picking/pick_session.dart';
import 'package:bayios_mobile/features/picking/picking_api.dart';
import 'package:bayios_mobile/features/picking/picking_models.dart';
import 'package:bayios_mobile/features/picking/scan_alerts.dart';
import 'package:bayios_mobile/features/picking/shortage_dialog.dart';
import 'package:bayios_mobile/features/picking/tasks_providers.dart';
import 'package:bayios_mobile/features/picking/tasks_widgets.dart';
import 'package:bayios_mobile/l10n/app_localizations.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

/// /gorevler/:id — task / order detail with the shelf-ordered pick list and
/// the scan flow (M03/M04): camera or manual code, quantity, shortage,
/// complete.
class PickScreen extends ConsumerWidget {
  const PickScreen({required this.taskId, super.key});

  final String taskId;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final l10n = AppLocalizations.of(context);
    final session = ref.watch(sessionControllerProvider);
    if (session is! SessionReady) return const LoadingView();
    final async = ref.watch(pickingTaskProvider(taskId));
    if (async.hasValue) {
      return _PickView(
        // A different task (or a re-entered route) gets a fresh controller.
        key: ValueKey(taskId),
        initial: async.requireValue,
        session: session,
      );
    }
    return Scaffold(
      appBar: AppBar(title: Text(l10n.pickingDefaultTitle)),
      body: switch (async.error) {
        null => const LoadingView(),
        final ApiException e when e.kind == ApiErrorKind.notFound => Center(
          child: Padding(
            padding: const EdgeInsets.all(BosSpace.s24),
            child: Column(
              mainAxisSize: MainAxisSize.min,
              children: [
                const Icon(
                  Icons.search_off,
                  size: 48,
                  color: BosColors.textMuted,
                ),
                const SizedBox(height: BosSpace.s16),
                Text(
                  l10n.pickingNotFound,
                  key: const Key('pick-not-found'),
                  textAlign: TextAlign.center,
                  style: BosType.bodyLg,
                ),
                const SizedBox(height: BosSpace.s24),
                FilledButton(
                  onPressed: () => context.go('/gorevler'),
                  style: FilledButton.styleFrom(
                    minimumSize: const Size(200, BosSize.primaryAction),
                  ),
                  child: Text(l10n.pickingBackToTasks),
                ),
              ],
            ),
          ),
        ),
        final error => ErrorView(
          error: error,
          onRetry: () => ref.invalidate(pickingTaskProvider(taskId)),
        ),
      },
    );
  }
}

class _PickView extends ConsumerStatefulWidget {
  const _PickView({required this.initial, required this.session, super.key});

  final PickingTask initial;
  final SessionReady session;

  @override
  ConsumerState<_PickView> createState() => _PickViewState();
}

class _PickViewState extends ConsumerState<_PickView> {
  PickController? _controller;
  final _manual = TextEditingController();
  final _manualFocus = FocusNode();
  final _quantity = TextEditingController(text: '1');

  PickController get controller => _controller!;

  @override
  void didChangeDependencies() {
    super.didChangeDependencies();
    _controller ??= PickController(
      api: ref.read(pickingApiProvider),
      task: widget.initial,
      userId: widget.session.me.userId,
      permissions: PickPermissions.of(widget.session.access),
      l10n: AppLocalizations.of(context),
      alerts: ref.read(scanAlertsProvider),
      onWrite: () {
        ref
          ..invalidate(tasksPageProvider)
          ..invalidate(dashboardSummaryProvider);
      },
    )..addListener(_syncQuantity);
  }

  void _syncQuantity() {
    final text = controller.quantityText;
    if (_quantity.text != text) {
      _quantity.value = TextEditingValue(
        text: text,
        selection: TextSelection.collapsed(offset: text.length),
      );
    }
  }

  @override
  void dispose() {
    _controller?.dispose();
    _manual.dispose();
    _manualFocus.dispose();
    _quantity.dispose();
    super.dispose();
  }

  Future<void> _openCamera() async {
    final scanner = ref.read(codeScannerProvider);
    final outcome = await scanner(
      context,
      shelf: controller.step == PickStep.location,
    );
    if (!mounted) return;
    switch (outcome) {
      case ScannedCode(:final code):
        await controller.handleCode(code);
      case ManualEntryRequested():
        _manualFocus.requestFocus();
      case null:
        break;
    }
  }

  Future<void> _submitManual() async {
    final code = _manual.text.trim();
    if (code.isEmpty || controller.sending) return;
    _manual.clear();
    await controller.handleCode(code);
  }

  Future<void> _shortage(PickingTaskLine line) async {
    await showShortageDialog(context, line: line, controller: controller);
  }

  Future<void> _complete() async {
    final l10n = AppLocalizations.of(context);
    final totals = controller.totals;
    if (totals.short > 0) {
      final ok = await showDialog<bool>(
        context: context,
        barrierDismissible: false,
        builder: (context) => AlertDialog(
          title: Text(l10n.pickingCompleteShortTitle),
          content: Text(
            l10n.pickingCompleteShortBody(
              totals.shortLines,
              formatQuantity(totals.short),
            ),
          ),
          actions: [
            TextButton(
              onPressed: () => Navigator.of(context).pop(false),
              child: Text(l10n.pickingCancel),
            ),
            FilledButton(
              key: const Key('pick-complete-confirm'),
              onPressed: () => Navigator.of(context).pop(true),
              child: Text(l10n.pickingCompleteShortConfirm),
            ),
          ],
        ),
      );
      if (ok != true) return;
    }
    await controller.complete();
  }

  @override
  Widget build(BuildContext context) {
    return ListenableBuilder(
      listenable: controller,
      builder: (context, _) => _build(context),
    );
  }

  Widget _build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final c = controller;
    final task = c.task.task;
    final totals = c.totals;
    final current = c.current;
    final showOrder = widget.session.access.canSome('orders.read');

    return Scaffold(
      appBar: AppBar(
        title: Text(l10n.pickingTitle(task.orderNumber)),
        actions: [
          Padding(
            padding: const EdgeInsets.only(right: BosSpace.s16),
            child: Center(child: TaskStatusBadge(task.status)),
          ),
        ],
      ),
      body: RefreshIndicator(
        onRefresh: c.reload,
        child: ListView(
          key: const Key('pick-scroll'),
          padding: const EdgeInsets.all(BosSpace.s16),
          children: [
            Semantics(
              container: true,
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.stretch,
                children: [
                  PickProgressBar(value: totals.progress),
                  const SizedBox(height: BosSpace.s4),
                  Text(
                    [
                      l10n.pickingProgress(
                        formatQuantity(totals.picked),
                        formatQuantity(totals.toPick),
                        totals.openLines,
                      ),
                      if (totals.short > 0)
                        l10n.tasksShort(formatQuantity(totals.short)),
                    ].join(' · '),
                    key: const Key('pick-progress'),
                    style: BosType.body.copyWith(
                      color: BosColors.textSecondary,
                    ),
                  ),
                ],
              ),
            ),
            const SizedBox(height: BosSpace.s12),
            if (c.notice case final notice?) ...[
              _Banner(feedback: notice, key: const Key('pick-notice')),
              const SizedBox(height: BosSpace.s12),
            ],
            ..._status(context, l10n),
            // Result and manual entry first: "✗ Yanlış ürün" must be visible
            // right after a scan without scrolling, also with the keyboard
            // open.
            if (c.canWork) ...[
              _FeedbackBox(controller: c, current: current),
              const SizedBox(height: BosSpace.s12),
            ],
            if (c.canWork && current != null) ...[
              _manualEntry(l10n),
              const SizedBox(height: BosSpace.s12),
              _CurrentLine(controller: c, line: current),
              const SizedBox(height: BosSpace.s16),
            ],
            if (showOrder) ...[
              _OrderInfo(orderId: task.orderId),
              const SizedBox(height: BosSpace.s16),
            ],
            Text(
              l10n.pickingLinesTitle,
              style: Theme.of(context).textTheme.titleMedium,
            ),
            const SizedBox(height: BosSpace.s8),
            for (final line in c.lines) ...[
              _LineTile(
                line: line,
                isCurrent: c.canWork && current?.id == line.id,
                onFocus: c.canWork && line.isOpen && current?.id != line.id
                    ? () => c.focusLine(line.id)
                    : null,
              ),
              const SizedBox(height: BosSpace.s8),
            ],
          ],
        ),
      ),
      bottomNavigationBar: c.canWork ? _actions(l10n, totals, current) : null,
    );
  }

  List<Widget> _status(BuildContext context, AppLocalizations l10n) {
    final c = controller;
    final task = c.task.task;
    if (!c.active) {
      final cancelled = task.status == PickingTaskStatus.cancelled;
      return [
        _Banner(
          key: const Key('pick-done'),
          feedback: PickFeedback(
            cancelled ? PickTone.warning : PickTone.success,
            cancelled ? l10n.pickingCancelledTitle : l10n.pickingDoneTitle,
            cancelled
                ? l10n.pickingCancelledBody
                : c.task.shipmentId != null
                ? l10n.pickingDoneShipment
                : l10n.pickingDoneNoShipment,
          ),
        ),
        const SizedBox(height: BosSpace.s12),
      ];
    }
    if (c.canWork) return const [];
    final PickFeedback info;
    if (!c.permissions.pick) {
      info = PickFeedback(PickTone.info, l10n.pickingNoPickPermission);
    } else if (task.isPool) {
      info = PickFeedback(
        PickTone.info,
        l10n.pickingPoolTitle,
        c.canClaim ? l10n.pickingPoolClaimBody : l10n.pickingPoolReadOnly,
      );
    } else {
      info = PickFeedback(
        PickTone.info,
        l10n.pickingOtherAssigneeTitle,
        task.assigneeName == null
            ? l10n.pickingOtherAssigneeBodyAnonymous
            : l10n.pickingOtherAssigneeBody(task.assigneeName!),
      );
    }
    return [
      _Banner(feedback: info, key: const Key('pick-readonly')),
      if (c.canClaim) ...[
        const SizedBox(height: BosSpace.s12),
        FilledButton.icon(
          key: const Key('pick-claim'),
          onPressed: c.pending == null ? c.claim : null,
          icon: const Icon(Icons.pan_tool_alt_outlined),
          label: Text(
            c.pending == PickPending.claim ? l10n.loading : l10n.pickingClaim,
          ),
          style: FilledButton.styleFrom(
            minimumSize: const Size.fromHeight(BosSize.primaryAction),
          ),
        ),
      ],
      const SizedBox(height: BosSpace.s12),
    ];
  }

  Widget _manualEntry(AppLocalizations l10n) {
    final shelf = controller.step == PickStep.location;
    return Row(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Expanded(
          child: TextField(
            key: const Key('pick-manual-code'),
            controller: _manual,
            focusNode: _manualFocus,
            textInputAction: TextInputAction.send,
            autocorrect: false,
            enableSuggestions: false,
            // Hardware (keyboard-wedge) scanners type the code + Enter here.
            onSubmitted: (_) => unawaited(_submitManual()),
            decoration: InputDecoration(
              labelText: l10n.pickingManualLabel,
              hintText: shelf
                  ? l10n.pickingManualHintShelf
                  : l10n.pickingManualHintItem,
            ),
          ),
        ),
        const SizedBox(width: BosSpace.s8),
        FilledButton.tonal(
          key: const Key('pick-manual-submit'),
          onPressed: controller.sending ? null : _submitManual,
          style: FilledButton.styleFrom(
            minimumSize: const Size(BosSize.minTouchTarget, 56),
          ),
          child: Text(l10n.pickingManualSubmit),
        ),
      ],
    );
  }

  Widget _actions(
    AppLocalizations l10n,
    PickTotals totals,
    PickingTaskLine? current,
  ) {
    final c = controller;
    final done = totals.openLines == 0;
    return Material(
      elevation: 8,
      color: BosColors.surfaceCard,
      child: SafeArea(
        top: false,
        child: Padding(
          padding: const EdgeInsets.fromLTRB(
            BosSpace.s16,
            BosSpace.s8,
            BosSpace.s16,
            BosSpace.s12,
          ),
          child: Column(
            mainAxisSize: MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              if (!done) ...[
                Row(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    IconButton.outlined(
                      key: const Key('pick-qty-minus'),
                      tooltip: l10n.pickingQuantityDecrease,
                      onPressed: c.canDecrement ? c.decrementQuantity : null,
                      icon: const Icon(Icons.remove),
                      constraints: const BoxConstraints.tightFor(
                        width: 56,
                        height: 56,
                      ),
                    ),
                    const SizedBox(width: BosSpace.s8),
                    Expanded(
                      child: TextField(
                        key: const Key('pick-qty'),
                        controller: _quantity,
                        onChanged: c.setQuantityText,
                        textAlign: TextAlign.center,
                        style: BosType.h3,
                        keyboardType: const TextInputType.numberWithOptions(
                          decimal: true,
                        ),
                        decoration: InputDecoration(
                          labelText: l10n.pickingQuantityLabel,
                          errorText: switch (c.quantityError) {
                            final e? => quantityErrorText(
                              l10n,
                              e,
                              c.quantityMax,
                            ),
                            null => null,
                          },
                          errorMaxLines: 2,
                        ),
                      ),
                    ),
                    const SizedBox(width: BosSpace.s8),
                    IconButton.outlined(
                      key: const Key('pick-qty-plus'),
                      tooltip: l10n.pickingQuantityIncrease,
                      onPressed: c.canIncrement ? c.incrementQuantity : null,
                      icon: const Icon(Icons.add),
                      constraints: const BoxConstraints.tightFor(
                        width: 56,
                        height: 56,
                      ),
                    ),
                  ],
                ),
                const SizedBox(height: BosSpace.s8),
                Row(
                  children: [
                    if (c.permissions.reportShortage && current != null) ...[
                      OutlinedButton(
                        key: const Key('pick-shortage'),
                        onPressed: c.sending ? null : () => _shortage(current),
                        style: OutlinedButton.styleFrom(
                          minimumSize: const Size(0, BosSize.primaryAction),
                          foregroundColor: BosColors.danger.fg,
                        ),
                        child: Text(l10n.pickingReportShortage),
                      ),
                      const SizedBox(width: BosSpace.s8),
                    ],
                    Expanded(
                      child: FilledButton.icon(
                        key: const Key('pick-camera'),
                        onPressed: c.sending ? null : _openCamera,
                        icon: const Icon(Icons.qr_code_scanner),
                        label: Text(l10n.pickingScanCamera),
                        style: FilledButton.styleFrom(
                          minimumSize: const Size.fromHeight(
                            BosSize.primaryAction,
                          ),
                        ),
                      ),
                    ),
                  ],
                ),
              ] else
                FilledButton.icon(
                  key: const Key('pick-complete'),
                  onPressed: c.pending == null ? _complete : null,
                  icon: const Icon(Icons.done_all),
                  label: Text(
                    c.pending == PickPending.complete
                        ? l10n.loading
                        : l10n.pickingComplete,
                  ),
                  style: FilledButton.styleFrom(
                    minimumSize: const Size.fromHeight(BosSize.primaryAction),
                  ),
                ),
            ],
          ),
        ),
      ),
    );
  }
}

class _CurrentLine extends StatelessWidget {
  const _CurrentLine({required this.controller, required this.line});

  final PickController controller;
  final PickingTaskLine line;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final c = controller;
    final location = c.location;
    final stepText = switch (c.step) {
      PickStep.location => l10n.pickingStepLocation(line.locationCode ?? ''),
      _ when line.locationId != null => l10n.pickingStepItem(line.sku),
      _ => l10n.pickingStepItemNoLocation(line.sku),
    };
    return DecoratedBox(
      key: const Key('pick-current'),
      decoration: BoxDecoration(
        color: BosColors.surfaceCard,
        border: Border.all(color: BosColors.brandPrimary, width: 2),
        borderRadius: BorderRadius.circular(BosRadius.xl),
      ),
      child: Padding(
        padding: const EdgeInsets.all(BosSpace.s16),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            if (line.locationCode != null)
              Row(
                children: [
                  const Icon(
                    Icons.place_outlined,
                    size: 32,
                    color: BosColors.brandPrimary,
                  ),
                  const SizedBox(width: BosSpace.s8),
                  Expanded(
                    child: Text(
                      line.locationCode!,
                      key: const Key('pick-current-location'),
                      style: BosType.display.copyWith(fontFamily: 'monospace'),
                    ),
                  ),
                ],
              ),
            Text(line.sku, style: BosType.h3.copyWith(fontFamily: 'monospace')),
            Text(
              line.name,
              style: BosType.bodyLg.copyWith(color: BosColors.textSecondary),
            ),
            if (line.barcode != null)
              Text(
                l10n.pickingBarcode(line.barcode!),
                style: BosType.caption.copyWith(color: BosColors.textMuted),
              ),
            const SizedBox(height: BosSpace.s8),
            Text(
              l10n.pickingRemaining(
                formatQuantity(line.quantityRemaining),
                line.unit,
              ),
              key: const Key('pick-current-remaining'),
              style: BosType.quantity,
            ),
            Text(
              l10n.pickingLineQuantity(
                formatQuantity(line.quantityPicked),
                formatQuantity(line.quantityToPick),
                line.unit,
              ),
              style: BosType.body.copyWith(color: BosColors.textSecondary),
            ),
            const SizedBox(height: BosSpace.s8),
            DecoratedBox(
              decoration: BoxDecoration(
                color: BosColors.surfaceSunken,
                borderRadius: BorderRadius.circular(BosRadius.lg),
              ),
              child: Padding(
                padding: const EdgeInsets.symmetric(
                  horizontal: BosSpace.s12,
                  vertical: BosSpace.s8,
                ),
                child: Text(
                  stepText,
                  key: const Key('pick-step'),
                  style: BosType.body.copyWith(fontWeight: FontWeight.w600),
                ),
              ),
            ),
            if (location != null) ...[
              const SizedBox(height: BosSpace.s8),
              Wrap(
                spacing: BosSpace.s8,
                crossAxisAlignment: WrapCrossAlignment.center,
                alignment: WrapAlignment.spaceBetween,
                children: [
                  ToneBadge(
                    l10n.pickingShelfChip(location.label),
                    location.isKnown ? BosColors.success : BosColors.warning,
                  ),
                  TextButton(
                    key: const Key('pick-change-shelf'),
                    onPressed: c.clearLocation,
                    child: Text(l10n.pickingChangeShelf),
                  ),
                ],
              ),
            ],
          ],
        ),
      ),
    );
  }
}

class _FeedbackBox extends StatelessWidget {
  const _FeedbackBox({required this.controller, required this.current});

  final PickController controller;
  final PickingTaskLine? current;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final c = controller;
    final feedback = c.sending
        ? PickFeedback(
            PickTone.info,
            c.retryingBusy ? l10n.pickingBusyRetrying : l10n.pickingSending,
          )
        : c.feedback ??
              PickFeedback(
                PickTone.info,
                current == null ? l10n.pickingAllLinesDone : l10n.pickingReady,
              );
    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        _Banner(feedback: feedback, key: const Key('pick-feedback')),
        if (c.hasFailedScan && !c.sending) ...[
          const SizedBox(height: BosSpace.s8),
          FilledButton.icon(
            key: const Key('pick-resend'),
            onPressed: c.resend,
            icon: const Icon(Icons.refresh),
            label: Text(l10n.pickingResend),
            style: FilledButton.styleFrom(
              minimumSize: const Size.fromHeight(BosSize.primaryAction),
            ),
          ),
        ],
        if (current == null && c.totals.openLines > 0)
          Text(l10n.pickingCompleteBlocked(c.totals.openLines)),
      ],
    );
  }
}

/// Coloured message box; errors are announced (live region).
class _Banner extends StatelessWidget {
  const _Banner({required this.feedback, super.key});

  final PickFeedback feedback;

  @override
  Widget build(BuildContext context) {
    final (tone, icon) = switch (feedback.tone) {
      PickTone.success => (BosColors.success, Icons.check_circle_outline),
      PickTone.info => (BosColors.info, Icons.info_outline),
      PickTone.warning => (BosColors.warning, Icons.warning_amber_outlined),
      PickTone.error => (BosColors.danger, Icons.cancel_outlined),
    };
    return Semantics(
      liveRegion: true,
      container: true,
      child: DecoratedBox(
        decoration: BoxDecoration(
          color: tone.bg,
          border: Border.all(
            color: tone.border,
            width: feedback.tone == PickTone.error ? 2 : 1,
          ),
          borderRadius: BorderRadius.circular(BosRadius.lg),
        ),
        child: Padding(
          padding: const EdgeInsets.all(BosSpace.s12),
          child: Row(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Icon(icon, color: tone.fg, size: 28),
              const SizedBox(width: BosSpace.s8),
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(
                      feedback.title,
                      style: BosType.bodyLg.copyWith(
                        color: tone.fg,
                        fontWeight: FontWeight.w700,
                      ),
                    ),
                    if (feedback.text case final text?)
                      Text(text, style: BosType.body.copyWith(color: tone.fg)),
                  ],
                ),
              ),
            ],
          ),
        ),
      ),
    );
  }
}

class _LineTile extends StatelessWidget {
  const _LineTile({
    required this.line,
    required this.isCurrent,
    required this.onFocus,
  });

  final PickingTaskLine line;
  final bool isCurrent;
  final VoidCallback? onFocus;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final reason = switch (line.shortageReason) {
      ShortageReason.notFound => l10n.pickingShortageReasonNotFound,
      ShortageReason.damaged => l10n.pickingShortageReasonDamaged,
      ShortageReason.insufficient => l10n.pickingShortageReasonInsufficient,
      final other => other,
    };
    return DecoratedBox(
      key: Key('pick-line-${line.sku}'),
      decoration: BoxDecoration(
        color: BosColors.surfaceCard,
        border: Border.all(
          color: isCurrent ? BosColors.brandPrimary : BosColors.borderDefault,
          width: isCurrent ? 2 : 1,
        ),
        borderRadius: BorderRadius.circular(BosRadius.lg),
      ),
      child: Padding(
        padding: const EdgeInsets.all(BosSpace.s12),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Row(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                SizedBox(
                  width: 80,
                  child: Text(
                    line.locationCode ?? l10n.pickingNoLocation,
                    style: BosType.mono.copyWith(fontWeight: FontWeight.w700),
                  ),
                ),
                Expanded(
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Text(line.sku, style: BosType.mono),
                      Text(
                        line.name,
                        maxLines: 2,
                        overflow: TextOverflow.ellipsis,
                        style: BosType.caption.copyWith(
                          color: BosColors.textMuted,
                        ),
                      ),
                    ],
                  ),
                ),
                Text(
                  l10n.pickingLineQuantity(
                    formatQuantity(line.quantityPicked),
                    formatQuantity(line.quantityToPick),
                    line.unit,
                  ),
                  style: BosType.body,
                ),
              ],
            ),
            if (line.quantityShort > 0 || !line.isOpen || onFocus != null)
              Padding(
                padding: const EdgeInsets.only(top: BosSpace.s4),
                child: Row(
                  children: [
                    if (line.quantityShort > 0)
                      Flexible(
                        child: ToneBadge(
                          reason == null
                              ? l10n.tasksShort(
                                  formatQuantity(line.quantityShort),
                                )
                              : l10n.pickingShortageLabel(
                                  formatQuantity(line.quantityShort),
                                  reason,
                                ),
                          BosColors.danger,
                        ),
                      )
                    else if (!line.isOpen)
                      ToneBadge(l10n.pickingLineDone, BosColors.success),
                    const Spacer(),
                    if (onFocus != null)
                      TextButton(
                        key: Key('pick-focus-${line.sku}'),
                        onPressed: onFocus,
                        child: Text(l10n.pickingSwitchToLine),
                      ),
                  ],
                ),
              ),
          ],
        ),
      ),
    );
  }
}

/// Order header (no prices); optional — the pick list works without it.
class _OrderInfo extends ConsumerWidget {
  const _OrderInfo({required this.orderId});

  final String orderId;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final l10n = AppLocalizations.of(context);
    final async = ref.watch(pickOrderInfoProvider(orderId));
    final order = async.value;
    if (order == null) {
      if (!async.hasError) return const SizedBox.shrink();
      return Text(
        l10n.pickingOrderUnavailable,
        key: const Key('pick-order-unavailable'),
        style: BosType.caption.copyWith(color: BosColors.textMuted),
      );
    }
    final rows = <(String, String)>[
      if (order.customerName case final name?)
        (l10n.pickingOrderCustomer, name),
      if (order.warehouseName case final name?)
        (l10n.pickingOrderWarehouse, name),
      if (order.requestedDeliveryDate case final date?)
        (l10n.pickingOrderDelivery, _date(date)),
      if (order.notes case final notes? when notes.trim().isNotEmpty)
        (l10n.pickingOrderNotes, notes),
    ];
    return Card(
      key: const Key('pick-order'),
      child: Padding(
        padding: const EdgeInsets.all(BosSpace.s16),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text(
              l10n.pickingOrderTitle,
              style: Theme.of(context).textTheme.titleMedium,
            ),
            const SizedBox(height: BosSpace.s8),
            for (final (label, value) in rows)
              Padding(
                padding: const EdgeInsets.only(bottom: BosSpace.s4),
                child: Text.rich(
                  TextSpan(
                    children: [
                      TextSpan(
                        text: '$label: ',
                        style: const TextStyle(fontWeight: FontWeight.w600),
                      ),
                      TextSpan(text: value),
                    ],
                  ),
                  style: BosType.body,
                ),
              ),
            if (order.hasShortage)
              ToneBadge(l10n.pickingOrderShortageFlag, BosColors.danger),
          ],
        ),
      ),
    );
  }

  /// `2026-11-08` → `08.11.2026`.
  static String _date(String iso) {
    final parts = iso.split('-');
    return parts.length == 3 ? '${parts[2]}.${parts[1]}.${parts[0]}' : iso;
  }
}
