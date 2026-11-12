import 'dart:async';

import 'package:bayios_mobile/core/api/api_exception.dart';
import 'package:bayios_mobile/core/session/session_controller.dart';
import 'package:bayios_mobile/core/theme/app_colors.dart';
import 'package:bayios_mobile/core/theme/app_icon.dart';
import 'package:bayios_mobile/core/theme/app_spacing.dart';
import 'package:bayios_mobile/core/theme/app_theme.dart';
import 'package:bayios_mobile/core/ui/components.dart';
import 'package:bayios_mobile/core/ui/widgets.dart';
import 'package:bayios_mobile/features/dashboard/dashboard_data.dart';
import 'package:bayios_mobile/features/picking/pick_controller.dart';
import 'package:bayios_mobile/features/picking/pick_session.dart';
import 'package:bayios_mobile/features/picking/picking_api.dart';
import 'package:bayios_mobile/features/picking/picking_models.dart';
import 'package:bayios_mobile/features/picking/scan_alerts.dart';
import 'package:bayios_mobile/features/picking/shortage_dialog.dart';
import 'package:bayios_mobile/features/picking/tasks_providers.dart';
import 'package:bayios_mobile/features/picking/tasks_widgets.dart';
import 'package:bayios_mobile/features/stock/scanner.dart';
import 'package:bayios_mobile/l10n/app_localizations.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';
import 'package:intl/intl.dart';

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
      appBar: AppTopBar(title: l10n.pickingDefaultTitle),
      body: switch (async.error) {
        null => const LoadingView(),
        final ApiException e when e.kind == ApiErrorKind.notFound => Center(
          child: Padding(
            padding: const EdgeInsets.all(AppSpacing.lg),
            child: Column(
              mainAxisSize: MainAxisSize.min,
              children: [
                const IconBadge(
                  icon: AppIcons.search,
                  tone: Tone.neutral,
                  size: AppSize.primaryAction,
                  round: true,
                ),
                const SizedBox(height: AppSpacing.md),
                Text(
                  l10n.pickingNotFound,
                  key: const Key('pick-not-found'),
                  textAlign: TextAlign.center,
                  style: context.text.bodyLarge,
                ),
                const SizedBox(height: AppSpacing.lg),
                FilledButton(
                  onPressed: () => context.go('/gorevler'),
                  style: FilledButton.styleFrom(
                    minimumSize: const Size(200, AppSize.primaryAction),
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
    final l10n = AppLocalizations.of(context);
    // The shared camera scanner of #43 (lib/features/stock/scanner.dart).
    final code = await ref
        .read(barcodeScannerProvider)
        .scan(
          context,
          title: controller.step == PickStep.location
              ? l10n.pickingScanTitleShelf
              : l10n.pickingScanTitleItem,
        );
    if (!mounted) return;
    if (code == null) {
      // Closed, "Kodu elle gir", no camera or no permission: type it.
      _manualFocus.requestFocus();
      return;
    }
    await controller.handleCode(code);
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
    // A resend repeats the already confirmed request: no second confirmation.
    if (totals.short > 0 && !controller.completeNeedsResend) {
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
      // The full order number is what the picker compares with the paper /
      // label; the status badge sits in the body so it is not truncated.
      appBar: AppTopBar(
        title: l10n.pickingTitle(task.orderNumber),
        fitTitle: true,
      ),
      body: RefreshIndicator(
        onRefresh: c.reload,
        child: ListView(
          key: const Key('pick-scroll'),
          padding: const EdgeInsets.all(AppSpacing.md),
          children: [
            Semantics(
              container: true,
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.stretch,
                children: [
                  Wrap(
                    spacing: AppSpacing.xs,
                    runSpacing: AppSpacing.xxs,
                    children: [
                      TaskStatusBadge(task.status),
                      TaskPriorityBadge(task.priority),
                    ],
                  ),
                  if (taskCustomerLine(l10n, task) case final String customer)
                    Padding(
                      padding: const EdgeInsets.only(top: AppSpacing.xxs),
                      child: Text(
                        customer,
                        key: const Key('pick-customer'),
                        style: context.text.titleMedium,
                      ),
                    ),
                  const SizedBox(height: AppSpacing.sm),
                  PickProgressBar(value: totals.progress),
                  const SizedBox(height: AppSpacing.xxs),
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
                    style: context.text.bodySmall?.copyWith(
                      color: context.colors.textMuted,
                    ),
                  ),
                ],
              ),
            ),
            const SizedBox(height: AppSpacing.sm),
            // After completion the "Toplama tamamlandı" banner says it all.
            if (c.notice case final notice?
                when c.active || notice.tone != PickTone.success) ...[
              _Banner(feedback: notice, key: const Key('pick-notice')),
              const SizedBox(height: AppSpacing.sm),
            ],
            ..._status(context, l10n),
            // Result and manual entry first: "✗ Yanlış ürün" must be visible
            // right after a scan without scrolling, also with the keyboard
            // open.
            if (c.canWork) ...[
              _FeedbackBox(controller: c, current: current),
              const SizedBox(height: AppSpacing.sm),
            ],
            if (c.canWork && current != null) ...[
              _manualEntry(l10n),
              const SizedBox(height: AppSpacing.sm),
              _CurrentLine(controller: c, line: current),
              const SizedBox(height: AppSpacing.md),
            ],
            if (showOrder) ...[
              _OrderInfo(orderId: task.orderId),
              const SizedBox(height: AppSpacing.md),
            ],
            SectionHeader(l10n.pickingLinesTitle),
            for (final line in c.lines) ...[
              _LineTile(
                line: line,
                isCurrent: c.canWork && current?.id == line.id,
                onFocus: c.canWork && line.isOpen && current?.id != line.id
                    ? () => c.focusLine(line.id)
                    : null,
              ),
              const SizedBox(height: AppSpacing.xs),
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
        const SizedBox(height: AppSpacing.sm),
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
        const SizedBox(height: AppSpacing.sm),
        FilledButton.icon(
          key: const Key('pick-claim'),
          onPressed: c.pending == null ? c.claim : null,
          icon: const AppIcon(AppIcons.handPointer),
          label: Text(
            c.pending == PickPending.claim
                ? l10n.loading
                : c.claimNeedsResend
                ? l10n.pickingResend
                : l10n.pickingClaim,
          ),
          style: FilledButton.styleFrom(
            minimumSize: const Size.fromHeight(AppSize.primaryAction),
          ),
        ),
      ],
      const SizedBox(height: AppSpacing.sm),
    ];
  }

  Widget _manualEntry(AppLocalizations l10n) {
    final shelf = controller.step == PickStep.location;
    return LabeledField(
      label: l10n.pickingManualLabel,
      trailing: OutlinedButton(
        key: const Key('pick-manual-submit'),
        onPressed: controller.sending ? null : _submitManual,
        child: Text(l10n.pickingManualSubmit),
      ),
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
          hintText: shelf
              ? l10n.pickingManualHintShelf
              : l10n.pickingManualHintItem,
          prefixIcon: const AppIcon(AppIcons.scan),
        ),
      ),
    );
  }

  Widget _actions(
    AppLocalizations l10n,
    PickTotals totals,
    PickingTaskLine? current,
  ) {
    final c = controller;
    final done = totals.openLines == 0;
    // Thumb-zone action bar: card surface with a hairline on top.
    return DecoratedBox(
      decoration: BoxDecoration(
        color: context.colors.surfaceCard,
        border: Border(top: BorderSide(color: context.colors.borderDefault)),
      ),
      child: SafeArea(
        top: false,
        child: Padding(
          padding: const EdgeInsets.fromLTRB(
            AppSpacing.md,
            AppSpacing.xs,
            AppSpacing.md,
            AppSpacing.sm,
          ),
          child: Column(
            mainAxisSize: MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              if (!done) ...[
                LabeledField(
                  label: l10n.pickingQuantityLabel,
                  leading: StepperButton(
                    key: const Key('pick-qty-minus'),
                    tooltip: l10n.pickingQuantityDecrease,
                    onPressed: c.canDecrement ? c.decrementQuantity : null,
                    icon: AppIcons.minus4,
                  ),
                  trailing: StepperButton(
                    key: const Key('pick-qty-plus'),
                    tooltip: l10n.pickingQuantityIncrease,
                    onPressed: c.canIncrement ? c.incrementQuantity : null,
                    icon: AppIcons.plus4,
                  ),
                  child: TextField(
                    key: const Key('pick-qty'),
                    controller: _quantity,
                    onChanged: c.setQuantityText,
                    textAlign: TextAlign.center,
                    style: context.text.titleLarge?.tabular,
                    keyboardType: const TextInputType.numberWithOptions(
                      decimal: true,
                    ),
                    decoration: InputDecoration(
                      // 16 + 24 + 16 = 56 dp, level with the − / + buttons.
                      contentPadding: const EdgeInsets.all(AppSpacing.md),
                      errorText: switch (c.quantityError) {
                        final e? => quantityErrorText(l10n, e, c.quantityMax),
                        null => null,
                      },
                      errorMaxLines: 2,
                    ),
                  ),
                ),
                const SizedBox(height: AppSpacing.sm),
                Row(
                  children: [
                    if (c.permissions.reportShortage && current != null) ...[
                      OutlinedButton(
                        key: const Key('pick-shortage'),
                        onPressed: c.sending ? null : () => _shortage(current),
                        style: OutlinedButton.styleFrom(
                          minimumSize: const Size(0, AppSize.primaryAction),
                          foregroundColor: context.colors.danger.fg,
                        ),
                        child: Text(l10n.pickingReportShortage),
                      ),
                      const SizedBox(width: AppSpacing.xs),
                    ],
                    Expanded(
                      child: FilledButton.icon(
                        key: const Key('pick-camera'),
                        onPressed: c.sending ? null : _openCamera,
                        icon: const AppIcon(AppIcons.scan),
                        label: Text(l10n.pickingScanCamera),
                        style: FilledButton.styleFrom(
                          minimumSize: const Size.fromHeight(
                            AppSize.primaryAction,
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
                  icon: const AppIcon(AppIcons.doneAll),
                  label: Text(
                    c.pending == PickPending.complete
                        ? l10n.loading
                        : c.completeNeedsResend
                        ? l10n.pickingResend
                        : l10n.pickingComplete,
                  ),
                  style: FilledButton.styleFrom(
                    minimumSize: const Size.fromHeight(AppSize.primaryAction),
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
    final shelfStep = c.step == PickStep.location;
    return AppCard(
      key: const Key('pick-current'),
      borderColor: context.colors.brandPrimary,
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          ScanStepIndicator(
            key: const Key('pick-steps'),
            current: shelfStep ? 0 : 1,
            steps: [
              ScanStep(
                l10n.scanStepShelf,
                detail: line.locationCode,
                skipped: line.locationId == null,
              ),
              ScanStep(l10n.scanStepProduct, detail: line.sku),
              ScanStep(
                l10n.scanStepQuantity,
                detail:
                    '${formatQuantity(line.quantityRemaining)} ${line.unit}',
              ),
            ],
          ),
          const SizedBox(height: AppSpacing.sm),
          // What to do now, in words (the indicator is the overview).
          DecoratedBox(
            decoration: BoxDecoration(
              color: context.colors.brand.bg,
              border: Border.all(color: context.colors.brand.border),
              borderRadius: AppRadius.mdAll,
            ),
            child: Padding(
              padding: const EdgeInsets.symmetric(
                horizontal: AppSpacing.sm,
                vertical: AppSpacing.sm,
              ),
              child: Row(
                children: [
                  AppIcon(
                    shelfStep ? AppIcons.shelves : AppIcons.scan,
                    color: context.colors.brand.fg,
                    size: AppSize.iconMd,
                  ),
                  const SizedBox(width: AppSpacing.sm),
                  Expanded(
                    child: Text(
                      stepText,
                      key: const Key('pick-step'),
                      style: context.text.titleMedium?.bold.copyWith(
                        color: context.colors.brand.fg,
                      ),
                    ),
                  ),
                ],
              ),
            ),
          ),
          const SizedBox(height: AppSpacing.sm),
          if (line.locationCode != null)
            Row(
              children: [
                AppIcon(
                  AppIcons.pin3,
                  size: AppSize.iconLg,
                  color: context.colors.brandPrimary,
                ),
                const SizedBox(width: AppSpacing.xs),
                Expanded(
                  child: Text(
                    line.locationCode!,
                    key: const Key('pick-current-location'),
                    style: context.text.displaySmall?.tabular.copyWith(
                      color: context.colors.textPrimary,
                    ),
                  ),
                ),
              ],
            ),
          if (!line.locationIsActive) _InactiveShelfWarning(line: line),
          Text(
            line.sku,
            style: context.text.titleMedium?.tabular.copyWith(
              color: context.colors.textPrimary,
            ),
          ),
          Text(
            line.name,
            style: context.text.bodyMedium?.copyWith(
              color: context.colors.textSecondary,
            ),
          ),
          if (line.barcode != null)
            Text(
              l10n.pickingBarcode(line.barcode!),
              style: context.text.bodySmall!.copyWith(
                color: context.colors.textMuted,
              ),
            ),
          const SizedBox(height: AppSpacing.xs),
          Text(
            l10n.pickingRemaining(
              formatQuantity(line.quantityRemaining),
              line.unit,
            ),
            key: const Key('pick-current-remaining'),
            style: context.text.displaySmall!.tabular,
          ),
          Text(
            l10n.pickingLineQuantity(
              formatQuantity(line.quantityPicked),
              formatQuantity(line.quantityToPick),
              line.unit,
            ),
            style: context.text.bodyMedium!.copyWith(
              color: context.colors.textSecondary,
            ),
          ),
          if (location != null) ...[
            const SizedBox(height: AppSpacing.xs),
            Wrap(
              spacing: AppSpacing.xs,
              crossAxisAlignment: WrapCrossAlignment.center,
              alignment: WrapAlignment.spaceBetween,
              children: [
                StatusChip(
                  l10n.pickingShelfChip(location.label),
                  location.isKnown ? Tone.success : Tone.warning,
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
          const SizedBox(height: AppSpacing.xs),
          FilledButton.icon(
            key: const Key('pick-resend'),
            onPressed: c.resend,
            icon: const AppIcon(AppIcons.refresh),
            label: Text(l10n.pickingResend),
            style: FilledButton.styleFrom(
              minimumSize: const Size.fromHeight(AppSize.primaryAction),
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
  Widget build(BuildContext context) => FeedbackBanner(
    tone: switch (feedback.tone) {
      PickTone.success => FeedbackTone.success,
      PickTone.info => FeedbackTone.info,
      PickTone.warning => FeedbackTone.warning,
      PickTone.error => FeedbackTone.error,
    },
    title: feedback.title,
    text: feedback.text,
  );
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
    return AppCard(
      key: Key('pick-line-${line.sku}'),
      borderColor: isCurrent ? context.colors.brandPrimary : null,
      padding: const EdgeInsets.all(AppSpacing.sm),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Padding(
                padding: const EdgeInsets.only(right: AppSpacing.sm),
                child: StatusChip(
                  line.locationCode ?? l10n.pickingNoLocation,
                  Tone.neutral,
                  icon: AppIcons.pin3,
                ),
              ),
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(
                      line.sku,
                      style: context.text.bodyMedium?.semiBold.tabular,
                    ),
                    Text(
                      line.name,
                      maxLines: 2,
                      overflow: TextOverflow.ellipsis,
                      style: context.text.bodySmall!.copyWith(
                        color: context.colors.textMuted,
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
                style: context.text.bodySmall?.semiBold.tabular,
              ),
            ],
          ),
          if (!line.locationIsActive && line.isOpen)
            _InactiveShelfWarning(line: line),
          if (line.quantityShort > 0 || !line.isOpen || onFocus != null)
            Padding(
              padding: const EdgeInsets.only(top: AppSpacing.xxs),
              child: Row(
                children: [
                  if (line.quantityShort > 0)
                    Flexible(
                      child: StatusChip(
                        reason == null
                            ? l10n.tasksShort(
                                formatQuantity(line.quantityShort),
                              )
                            : l10n.pickingShortageLabel(
                                formatQuantity(line.quantityShort),
                                reason,
                              ),
                        Tone.danger,
                      ),
                    )
                  else if (line.quantityToPick == 0 && line.quantityPicked == 0)
                    // Emptied by a re-allocation (#98): kept, not deleted.
                    StatusChip(l10n.pickingLineMoved, Tone.neutral)
                  else if (!line.isOpen)
                    StatusChip(l10n.pickingLineDone, Tone.success),
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
        style: context.text.bodySmall!.copyWith(
          color: context.colors.textMuted,
        ),
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
    return AppCard(
      key: const Key('pick-order'),
      title: l10n.pickingOrderTitle,
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          for (final (label, value) in rows)
            Padding(
              padding: const EdgeInsets.only(bottom: AppSpacing.xxs),
              child: Text.rich(
                TextSpan(
                  children: [
                    TextSpan(
                      text: '$label: ',
                      style: context.text.bodySmall?.semiBold.copyWith(
                        color: context.colors.textPrimary,
                      ),
                    ),
                    TextSpan(text: value),
                  ],
                ),
                style: context.text.bodySmall?.copyWith(
                  color: context.colors.textSecondary,
                ),
              ),
            ),
          if (order.hasShortage)
            StatusChip(l10n.pickingOrderShortageFlag, Tone.danger),
        ],
      ),
    );
  }

  /// `2026-11-08` → `08.11.2026`.
  static String _date(String iso) {
    final date = DateTime.tryParse(iso);
    return date == null ? iso : DateFormat('dd.MM.yyyy', 'tr').format(date);
  }
}

/// The line's shelf was deactivated and its open quantity could not move to
/// another shelf yet (#98, `locationIsActive: false`).
class _InactiveShelfWarning extends StatelessWidget {
  const _InactiveShelfWarning({required this.line});

  final PickingTaskLine line;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    return Padding(
      padding: const EdgeInsets.only(top: AppSpacing.xxs),
      child: Row(
        key: Key('pick-line-inactive-${line.sku}'),
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          AppIcon(
            AppIcons.warning,
            size: AppSize.icon,
            color: context.colors.warning.fg,
          ),
          const SizedBox(width: AppSpacing.xxs),
          Expanded(
            child: Text(
              l10n.pickingShelfInactive,
              style: context.text.bodySmall!.semiBold.copyWith(
                color: context.colors.warning.fg,
              ),
            ),
          ),
        ],
      ),
    );
  }
}
