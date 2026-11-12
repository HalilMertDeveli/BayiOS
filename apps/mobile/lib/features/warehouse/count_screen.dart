import 'dart:async';

import 'package:bayios_mobile/core/theme/app_colors.dart';
import 'package:bayios_mobile/core/theme/app_icon.dart';
import 'package:bayios_mobile/core/theme/app_spacing.dart';
import 'package:bayios_mobile/core/theme/app_theme.dart';
import 'package:bayios_mobile/core/ui/components.dart';
import 'package:bayios_mobile/core/ui/widgets.dart';
import 'package:bayios_mobile/features/picking/scan_alerts.dart';
import 'package:bayios_mobile/features/stock/quantity.dart';
import 'package:bayios_mobile/features/stock/stock_api.dart';
import 'package:bayios_mobile/features/stock/stock_errors.dart';
import 'package:bayios_mobile/features/stock/stock_widgets.dart';
import 'package:bayios_mobile/features/warehouse/count_controller.dart';
import 'package:bayios_mobile/features/warehouse/count_list_screen.dart';
import 'package:bayios_mobile/features/warehouse/document_scan_controller.dart';
import 'package:bayios_mobile/features/warehouse/warehouse_api.dart';
import 'package:bayios_mobile/features/warehouse/warehouse_errors.dart';
import 'package:bayios_mobile/features/warehouse/warehouse_models.dart';
import 'package:bayios_mobile/features/warehouse/warehouse_widgets.dart';
import 'package:bayios_mobile/l10n/app_localizations.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

/// Sayım girişi: mode (ekle / sabitle) → shelf → quantity → product scan;
/// counted lines below; "Sayımı bitir" submits for review. Applying the
/// differences is a manager action on the web.
class CountScreen extends ConsumerStatefulWidget {
  const CountScreen({required this.countId, super.key});

  final String countId;

  @override
  ConsumerState<CountScreen> createState() => _CountScreenState();
}

class _CountScreenState extends ConsumerState<CountScreen> {
  late final CountController _controller;

  @override
  void initState() {
    super.initState();
    _controller = CountController(
      api: ref.read(warehouseApiProvider),
      stock: ref.read(stockApiProvider),
      countId: widget.countId,
      alerts: ref.read(scanAlertsProvider),
      wait: ref.read(stockDelayProvider),
    );
    // Without the permission the screen only explains (no API call).
    if (ref
        .read(stockAccessProvider)
        .canSome(WarehousePermissions.performCount)) {
      unawaited(_controller.load());
    }
  }

  @override
  void dispose() {
    _controller.dispose();
    super.dispose();
  }

  Future<void> _confirmSubmit() async {
    final l10n = AppLocalizations.of(context);
    final count = _controller.count;
    if (count == null) return;
    final uncounted = count.lineCount - count.countedLineCount;
    final ok = await showDialog<bool>(
      context: context,
      builder: (context) => AlertDialog(
        title: Text(l10n.countSubmitConfirmTitle),
        content: Text(
          uncounted > 0
              ? l10n.countSubmitConfirmUncounted(uncounted)
              : l10n.countSubmitConfirmBody,
        ),
        actions: [
          TextButton(
            key: const Key('count-submit-cancel'),
            onPressed: () => Navigator.of(context).pop(false),
            child: Text(l10n.docCancel),
          ),
          FilledButton(
            key: const Key('count-submit-confirm'),
            onPressed: () => Navigator.of(context).pop(true),
            child: Text(l10n.countSubmit),
          ),
        ],
      ),
    );
    if (ok ?? false) await _controller.submit();
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final access = ref.watch(stockAccessProvider);
    if (!access.canSome(WarehousePermissions.performCount)) {
      return WarehouseForbidden(
        title: l10n.countTitle,
        message: l10n.countForbidden,
      );
    }
    return ListenableBuilder(
      listenable: _controller,
      builder: (context, _) {
        final c = _controller;
        final Widget body;
        if (c.loading && c.document == null) {
          body = const LoadingView();
        } else if (c.document == null) {
          body = Center(
            child: StockMessagePanel(
              key: const Key('count-load-error'),
              icon: AppIcons.offline,
              tone: Tone.danger,
              message: documentErrorMessage(
                l10n,
                c.loadError!,
                forbidden: l10n.countForbidden,
              ),
              onRetry: c.load,
              error: c.loadError,
            ),
          );
        } else {
          body = RefreshIndicator(
            onRefresh: c.load,
            child: _CountBody(controller: c),
          );
        }
        final open = c.document?.count.isOpen ?? false;
        return Scaffold(
          appBar: AppTopBar(title: c.count?.number ?? l10n.countTitle),
          body: body,
          bottomNavigationBar: open || c.submitState is SubmitFailed
              ? _SubmitBar(controller: c, onSubmit: _confirmSubmit)
              : null,
        );
      },
    );
  }
}

class _CountBody extends StatelessWidget {
  const _CountBody({required this.controller});

  final CountController controller;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final c = controller;
    final detail = c.document!;
    final count = detail.count;
    final counted = detail.countedLines;
    final locked = c.sending || c.hasPending;
    final showShelf = c.warehouse == null || c.requiresShelf;
    return ListView(
      key: const Key('count-body'),
      physics: const AlwaysScrollableScrollPhysics(),
      padding: const EdgeInsets.all(AppSpacing.md),
      children: [
        Card(
          child: Padding(
            padding: const EdgeInsets.all(AppSpacing.md),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Row(
                  children: [
                    Expanded(
                      child: Text(
                        '${count.warehouseName ?? '—'} · '
                        '${countScopeLabel(l10n, count.scope)}',
                        style: context.text.titleMedium,
                      ),
                    ),
                    if (count.isBlind) StatusChip(l10n.countBlind, Tone.purple),
                  ],
                ),
                Semantics(
                  liveRegion: true,
                  child: Text(
                    l10n.countProgress(count.countedLineCount, count.lineCount),
                    key: const Key('count-progress'),
                  ),
                ),
                if (count.quantitiesHidden)
                  Text(
                    l10n.countBlindHint,
                    key: const Key('count-blind-hint'),
                    style: TextStyle(color: context.colors.textMuted),
                  ),
              ],
            ),
          ),
        ),
        const SizedBox(height: AppSpacing.md),
        if (c.submitState is SubmitDone ||
            count.status == CountStatus.submitted)
          StockNotice(
            key: const Key('count-submitted'),
            tone: Tone.success,
            icon: AppIcons.checkCircle1,
            child: Text(l10n.countSubmitted),
          )
        else if (!count.isOpen)
          StockNotice(
            key: const Key('count-closed'),
            tone: Tone.warning,
            icon: AppIcons.infoCircle,
            child: Text(l10n.countNotOpen),
          )
        else ...[
          SegmentedButton<CountMode>(
            key: const Key('count-mode'),
            segments: [
              ButtonSegment(
                value: CountMode.add,
                label: Text(l10n.countModeAdd),
                icon: const AppIcon(AppIcons.plus4),
              ),
              ButtonSegment(
                value: CountMode.set,
                label: Text(l10n.countModeSet),
                icon: const AppIcon(AppIcons.edit),
              ),
            ],
            selected: {c.mode},
            onSelectionChanged: locked
                ? null
                : (selection) => c.setMode(selection.first),
            style: SegmentedButton.styleFrom(
              minimumSize: const Size(0, AppSize.minTouchTarget),
            ),
          ),
          const SizedBox(height: AppSpacing.xxs),
          Text(
            c.mode == CountMode.add
                ? l10n.countModeAddHint
                : l10n.countModeSetHint,
            style: context.text.bodyMedium?.copyWith(
              color: context.colors.textMuted,
            ),
          ),
          const SizedBox(height: AppSpacing.md),
          DocumentScanSteps(
            key: const Key('count-steps'),
            shelf: c.shelf,
            shelfRequired: showShelf,
            quantityText: c.quantityText,
          ),
          const SizedBox(height: AppSpacing.md),
          if (showShelf) ...[
            ShelfScanField(
              keyPrefix: 'count',
              shelf: c.shelf,
              enabled: !locked,
              required: c.requiresShelf,
              missing: c.entryProblem == EntryProblem.shelfRequired,
              onScanned: c.setShelf,
              onClear: c.clearShelf,
            ),
            const SizedBox(height: AppSpacing.md),
          ],
          QuantityStepper(
            keyPrefix: 'count',
            text: c.quantityText,
            label: c.mode == CountMode.add
                ? l10n.countQuantityAddLabel
                : l10n.countQuantitySetLabel,
            error: quantityErrorText(l10n, c.quantityError),
            enabled: !locked,
            onChanged: c.setQuantityText,
            onStep: c.stepQuantity,
          ),
          const SizedBox(height: AppSpacing.md),
          CodeEntryField(
            fieldKey: const Key('count-code-field'),
            scanKey: const Key('count-code-scan'),
            label: l10n.docProductLabel,
            hint: l10n.countProductHint,
            scanTitle: l10n.docProductScanTitle,
            enabled: c.canScan,
            busy: c.sending,
            onSubmit: c.enter,
          ),
        ],
        if (c.retryingBusy) ...[
          const SizedBox(height: AppSpacing.xs),
          Text(l10n.docBusyRetrying, key: const Key('count-busy-retry')),
        ],
        if (c.feedback case final EntryFeedback feedback) ...[
          const SizedBox(height: AppSpacing.sm),
          EntryFeedbackView(
            keyPrefix: 'count',
            feedback: feedback,
            sending: c.sending,
            forbidden: l10n.countForbidden,
            acceptedLine: (line) => l10n.countAcceptedLine(
              line.sku,
              line.shelf ?? '—',
              formatQuantity(line.total),
            ),
            onResend: c.resend,
            onDiscard: c.discardPending,
          ),
        ],
        const SizedBox(height: AppSpacing.md),
        Text(
          l10n.countLinesTitle(counted.length),
          style: context.text.titleMedium,
        ),
        const SizedBox(height: AppSpacing.xs),
        if (counted.isEmpty)
          Text(
            l10n.countNoLines,
            key: const Key('count-no-lines'),
            style: context.text.bodyLarge?.copyWith(
              color: context.colors.textMuted,
            ),
          )
        else
          for (final line in counted)
            _CountLineTile(line: line, showSystem: c.showsSystemQuantities),
      ],
    );
  }
}

class _CountLineTile extends StatelessWidget {
  const _CountLineTile({required this.line, required this.showSystem});

  final CountLine line;
  final bool showSystem;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final expected = line.expectedQuantity;
    final difference = line.difference;
    return Card(
      key: Key('count-line-${line.id}'),
      child: Padding(
        padding: const EdgeInsets.all(AppSpacing.sm),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Row(
              children: [
                Expanded(
                  child: Text(
                    line.sku,
                    style: context.text.titleMedium?.tabular,
                  ),
                ),
                if (line.locationCode != null)
                  Text(line.locationCode!, style: context.text.bodyLarge),
              ],
            ),
            Text(
              l10n.countLineCounted(formatQuantity(line.countedQuantity ?? 0)),
              style: context.text.titleMedium,
            ),
            // System quantities only when the API sends them (never for a
            // blind count seen without inventory.count.manage).
            if (showSystem && expected != null)
              Text(
                l10n.countLineSystem(
                  formatQuantity(expected),
                  difference == null ? '—' : signedQuantity(difference),
                ),
                key: Key('count-line-system-${line.id}'),
                style: context.text.bodyMedium?.copyWith(
                  color: difference == null || difference == 0
                      ? context.colors.textMuted
                      : context.colors.danger.fg,
                ),
              ),
          ],
        ),
      ),
    );
  }
}

class _SubmitBar extends StatelessWidget {
  const _SubmitBar({required this.controller, required this.onSubmit});

  final CountController controller;
  final VoidCallback onSubmit;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final c = controller;
    final state = c.submitState;
    final sending = state is SubmitSending;
    // Thumb-zone action bar: card surface with a hairline on top.
    return DecoratedBox(
      decoration: BoxDecoration(
        color: context.colors.surfaceCard,
        border: Border(top: BorderSide(color: context.colors.borderDefault)),
      ),
      child: SafeArea(
        top: false,
        child: Padding(
          padding: const EdgeInsets.all(AppSpacing.sm),
          child: Column(
            mainAxisSize: MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              if (state is SubmitFailed) ...[
                StockNotice(
                  key: const Key('count-submit-error'),
                  tone: Tone.danger,
                  icon: AppIcons.infoCircle,
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Text(
                        documentErrorMessage(
                          l10n,
                          state.error,
                          forbidden: l10n.countForbidden,
                        ),
                      ),
                      if (state.pending) Text(l10n.docEntryPendingHint),
                    ],
                  ),
                ),
                const SizedBox(height: AppSpacing.xs),
              ],
              if (state is SubmitFailed && state.pending)
                FilledButton.icon(
                  key: const Key('count-submit-resend'),
                  onPressed: c.submit,
                  icon: const AppIcon(AppIcons.refresh),
                  label: Text(l10n.resend),
                  style: FilledButton.styleFrom(
                    backgroundColor: context.colors.dangerSolid,
                    minimumSize: const Size.fromHeight(AppSize.primaryAction),
                  ),
                )
              else if (c.document?.count.isOpen ?? false)
                FilledButton.icon(
                  key: const Key('count-submit'),
                  onPressed: sending || c.sending || c.hasPending
                      ? null
                      : onSubmit,
                  icon: sending
                      ? SizedBox.square(
                          dimension: AppSize.icon,
                          child: CircularProgressIndicator(
                            strokeWidth: 2,
                            color: context.colors.brandOnPrimary,
                          ),
                        )
                      : const AppIcon(AppIcons.checkCircle1),
                  label: Text(
                    sending ? l10n.countSubmitting : l10n.countSubmit,
                  ),
                  style: FilledButton.styleFrom(
                    minimumSize: const Size.fromHeight(AppSize.primaryAction),
                  ),
                )
              else
                TextButton(
                  key: const Key('count-submit-dismiss'),
                  onPressed: c.dismissSubmitError,
                  child: Text(l10n.docDismiss),
                ),
            ],
          ),
        ),
      ),
    );
  }
}
