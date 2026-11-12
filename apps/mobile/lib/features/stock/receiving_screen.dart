import 'dart:async';

import 'package:bayios_mobile/core/theme/app_colors.dart';
import 'package:bayios_mobile/core/theme/app_icon.dart';
import 'package:bayios_mobile/core/theme/app_spacing.dart';
import 'package:bayios_mobile/core/theme/app_theme.dart';
import 'package:bayios_mobile/core/ui/components.dart';
import 'package:bayios_mobile/core/ui/widgets.dart';
import 'package:bayios_mobile/features/stock/quantity.dart';
import 'package:bayios_mobile/features/stock/receiving_controller.dart';
import 'package:bayios_mobile/features/stock/scanner.dart';
import 'package:bayios_mobile/features/stock/stock_errors.dart';
import 'package:bayios_mobile/features/stock/stock_models.dart';
import 'package:bayios_mobile/features/stock/stock_widgets.dart';
import 'package:bayios_mobile/l10n/app_localizations.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

/// Mal kabul — scan / type products, enter quantity and bin, post once.
class ReceivingScreen extends ConsumerWidget {
  const ReceivingScreen({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final l10n = AppLocalizations.of(context);
    final access = ref.watch(stockAccessProvider);
    if (!access.can(StockPermissions.receive)) {
      // Deep link without the permission: say so instead of a broken form.
      return Scaffold(
        appBar: AppTopBar(title: l10n.receivingTitle),
        body: StockMessagePanel(
          key: const Key('receiving-no-permission'),
          icon: AppIcons.lock,
          message: l10n.receivingForbidden,
        ),
      );
    }

    final state = ref.watch(receivingControllerProvider);
    final controller = ref.read(receivingControllerProvider.notifier);

    final Widget body;
    if (state.loading) {
      body = const LoadingView();
    } else if (state.loadError != null) {
      body = Center(
        child: StockMessagePanel(
          key: const Key('receiving-load-error'),
          icon: AppIcons.offline,
          tone: Tone.danger,
          message: stockErrorMessage(
            l10n,
            state.loadError!,
            forbidden: l10n.receivingForbidden,
          ),
          onRetry: controller.load,
          error: state.loadError,
        ),
      );
    } else {
      body = _ReceivingForm(state: state, controller: controller);
    }

    return Scaffold(
      appBar: AppTopBar(title: l10n.receivingTitle),
      body: body,
      bottomNavigationBar: state.loading || state.loadError != null
          ? null
          : _SubmitBar(state: state, onSubmit: controller.submit),
    );
  }
}

class _ReceivingForm extends ConsumerWidget {
  const _ReceivingForm({required this.state, required this.controller});

  final ReceivingState state;
  final ReceivingController controller;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final l10n = AppLocalizations.of(context);
    final submission = state.submission;
    return ListView(
      padding: const EdgeInsets.all(AppSpacing.md),
      children: [
        if (submission is SubmissionSucceeded) ...[
          _SuccessNotice(
            submission: submission,
            onShowStock: () {
              final sku = submission.posted.first.variant.sku;
              context.go('/stok?q=${Uri.encodeQueryComponent(sku)}');
            },
            onNew: controller.startNew,
          ),
          const SizedBox(height: AppSpacing.md),
        ],
        if (submission is SubmissionFailed) ...[
          _FailureNotice(
            submission: submission,
            formErrors: state.formErrors,
            onRetry: controller.submit,
            onDiscard: controller.discardPending,
          ),
          const SizedBox(height: AppSpacing.md),
        ],
        _WarehouseSection(state: state, controller: controller),
        const SizedBox(height: AppSpacing.md),
        CodeEntryField(
          fieldKey: const Key('receiving-code-field'),
          scanKey: const Key('receiving-scan'),
          label: l10n.receivingCodeLabel,
          hint: l10n.receivingCodeHint,
          scanTitle: l10n.receivingScanTitle,
          enabled: !state.locked && state.current == null,
          busy: state.lookingUp,
          onSubmit: controller.lookup,
        ),
        const SizedBox(height: AppSpacing.sm),
        if (state.scan case final ScanFeedback scan)
          if (scan is! ScanMatched) ...[
            _ScanNotice(scan: scan),
            const SizedBox(height: AppSpacing.sm),
          ],
        if (state.current case final VariantRef variant) ...[
          _LineEditor(
            key: ValueKey('editor-${variant.id}'),
            variant: variant,
            state: state,
            controller: controller,
          ),
          const SizedBox(height: AppSpacing.md),
        ],
        _LinesSection(state: state, controller: controller),
      ],
    );
  }
}

class _WarehouseSection extends StatelessWidget {
  const _WarehouseSection({required this.state, required this.controller});

  final ReceivingState state;
  final ReceivingController controller;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final canChange = state.lines.isEmpty && !state.locked;
    return Card(
      child: Padding(
        padding: const EdgeInsets.all(AppSpacing.md),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            if (state.warehouses.isEmpty)
              Text(
                l10n.receivingNoWarehouse,
                key: const Key('receiving-no-warehouse'),
              )
            else
              LabeledField(
                label: l10n.receivingWarehouseLabel,
                child: DropdownButtonFormField<String>(
                  key: ValueKey('receiving-warehouse-${state.warehouse?.id}'),
                  initialValue: state.warehouse?.id,
                  isExpanded: true,
                  style: context.text.bodyMedium?.copyWith(
                    color: context.colors.textPrimary,
                  ),
                  icon: const AppIcon(AppIcons.arrowDown2, size: AppSize.icon),
                  decoration: InputDecoration(
                    helperText: canChange
                        ? null
                        : l10n.receivingWarehouseLocked,
                    helperMaxLines: 2,
                  ),
                  items: [
                    for (final w in state.warehouses)
                      DropdownMenuItem(
                        value: w.id,
                        child: Text(
                          w.isDefault
                              ? l10n.receivingWarehouseDefault(w.name)
                              : w.name,
                          overflow: TextOverflow.ellipsis,
                        ),
                      ),
                  ],
                  onChanged: canChange
                      ? (id) {
                          if (id != null) {
                            unawaited(controller.selectWarehouse(id));
                          }
                        }
                      : null,
                ),
              ),
            const SizedBox(height: AppSpacing.sm),
            // A new key per posted receipt clears the field.
            KeyedSubtree(
              key: ValueKey('receiving-reference-${state.receipts}'),
              child: LabeledField(
                label: l10n.receivingReferenceLabel,
                child: TextFormField(
                  key: const Key('receiving-reference'),
                  initialValue: state.reference,
                  enabled: !state.locked,
                  maxLength: referenceMaxLength,
                  decoration: InputDecoration(
                    helperText: l10n.receivingReferenceHint,
                  ),
                  onChanged: controller.setReference,
                ),
              ),
            ),
          ],
        ),
      ),
    );
  }
}

class _ScanNotice extends StatelessWidget {
  const _ScanNotice({required this.scan});

  final ScanFeedback scan;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final (key, message) = switch (scan) {
      ScanNotFound(:final code) => (
        'receiving-scan-not-found',
        l10n.receivingScanNotFound(code),
      ),
      ScanInactive(:final variant) => (
        'receiving-scan-inactive',
        l10n.receivingScanInactive(variant.sku),
      ),
      ScanFailed(:final error) => (
        'receiving-scan-failed',
        stockErrorMessage(l10n, error),
      ),
      ScanMatched() => ('', ''),
    };
    return StockNotice(
      key: Key(key),
      tone: Tone.danger,
      icon: AppIcons.infoCircle,
      child: Text(message),
    );
  }
}

/// Quantity and bin for the product that was just scanned.
class _LineEditor extends ConsumerStatefulWidget {
  const _LineEditor({
    required this.variant,
    required this.state,
    required this.controller,
    super.key,
  });

  final VariantRef variant;
  final ReceivingState state;
  final ReceivingController controller;

  @override
  ConsumerState<_LineEditor> createState() => _LineEditorState();
}

class _LineEditorState extends ConsumerState<_LineEditor> {
  final _quantity = TextEditingController(text: '1');
  String? _binId;
  String? _binScanError;
  LineValidation _validation = const LineValidation();

  @override
  void initState() {
    super.initState();
    // Same bin as the previous line: a pallet usually goes to one bin.
    final last = widget.state.lines.lastOrNull?.location;
    if (last != null && widget.controller.findBinById(last.id) != null) {
      _binId = last.id;
    }
  }

  @override
  void dispose() {
    _quantity.dispose();
    super.dispose();
  }

  void _step(int delta) {
    final parsed = parseQuantity(_quantity.text);
    final current = parsed.value ?? 0;
    final next = addQuantities(current, delta);
    if (next <= 0) return;
    setState(() => _quantity.text = quantityText(next));
  }

  Future<void> _scanBin() async {
    final l10n = AppLocalizations.of(context);
    final code = await ref
        .read(barcodeScannerProvider)
        .scan(context, title: l10n.receivingBinScanTitle);
    if (!mounted || code == null || code.trim().isEmpty) return;
    final bin = widget.controller.findBin(code);
    setState(() {
      // The scan result replaces an earlier "bin required" message.
      _validation = LineValidation(quantity: _validation.quantity);
      if (bin == null) {
        _binScanError = l10n.receivingBinNotFound(code.trim());
      } else {
        _binId = bin.id;
        _binScanError = null;
      }
    });
  }

  void _add() {
    final result = widget.controller.addCurrent(_quantity.text, _binId);
    setState(() => _validation = result);
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final state = widget.state;
    final quantityError = _validation.quantity;
    return AppCard(
      key: const Key('receiving-line-editor'),
      borderColor: context.colors.brandPrimary,
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Row(
            children: [
              AppIcon(
                AppIcons.checkCircle1,
                size: AppSize.icon,
                color: context.colors.success.fg,
              ),
              const SizedBox(width: AppSpacing.xs),
              Expanded(
                child: Semantics(
                  liveRegion: true,
                  child: Text(
                    l10n.receivingScanMatched,
                    style: AppTextStyles.semiStrong.copyWith(
                      color: context.colors.success.fg,
                    ),
                  ),
                ),
              ),
            ],
          ),
          const SizedBox(height: AppSpacing.xs),
          VariantHeader(variant: widget.variant),
          const SizedBox(height: AppSpacing.md),
          LabeledField(
            label: l10n.receivingQuantityLabel,
            leading: StepperButton(
              key: const Key('receiving-quantity-minus'),
              tooltip: l10n.receivingQuantityMinus,
              onPressed: () => _step(-1),
              icon: AppIcons.minus4,
            ),
            trailing: StepperButton(
              key: const Key('receiving-quantity-plus'),
              tooltip: l10n.receivingQuantityPlus,
              onPressed: () => _step(1),
              icon: AppIcons.plus4,
            ),
            child: TextField(
              key: const Key('receiving-quantity'),
              controller: _quantity,
              textAlign: TextAlign.center,
              style: context.text.titleLarge?.tabular,
              keyboardType: const TextInputType.numberWithOptions(
                decimal: true,
              ),
              decoration: InputDecoration(
                // 16 + 24 + 16 = 56 dp, level with the − / + buttons.
                contentPadding: const EdgeInsets.all(AppSpacing.md),
                errorText: quantityError == null
                    ? null
                    : quantityErrorMessage(l10n, quantityError),
                errorMaxLines: 2,
              ),
            ),
          ),
          if (state.usesLocations) ...[
            const SizedBox(height: AppSpacing.md),
            _binPicker(l10n, state),
          ],
          const SizedBox(height: AppSpacing.md),
          FilledButton.icon(
            key: const Key('receiving-add-line'),
            onPressed: _add,
            icon: const AppIcon(AppIcons.playlistAdd),
            label: Text(l10n.receivingAddLine),
            style: FilledButton.styleFrom(
              minimumSize: const Size.fromHeight(AppSize.primaryAction),
            ),
          ),
          const SizedBox(height: AppSpacing.xs),
          TextButton(
            key: const Key('receiving-cancel-line'),
            onPressed: widget.controller.cancelCurrent,
            child: Text(l10n.receivingCancelLine),
          ),
        ],
      ),
    );
  }

  Widget _binPicker(AppLocalizations l10n, ReceivingState state) {
    if (state.locationsLoading) return LoadingView(label: l10n.receivingBins);
    if (state.locationsError != null) {
      return StockMessagePanel(
        icon: AppIcons.offline,
        tone: Tone.danger,
        message: stockErrorMessage(l10n, state.locationsError!),
        onRetry: widget.controller.retryLocations,
        error: state.locationsError,
      );
    }
    if (state.locations.isEmpty) {
      return StockNotice(
        key: const Key('receiving-no-bins'),
        tone: Tone.warning,
        icon: AppIcons.warning,
        child: Text(l10n.receivingNoBins),
      );
    }
    final error = _validation.locationMissing
        ? l10n.stockFieldLocationRequired
        : _binScanError;
    return LabeledField(
      label: l10n.receivingBinLabel,
      trailing: StepperButton(
        key: const Key('receiving-bin-scan'),
        tooltip: l10n.receivingBinScanTitle,
        onPressed: _scanBin,
        icon: AppIcons.scan,
      ),
      child: DropdownButtonFormField<String>(
        key: ValueKey('receiving-bin-$_binId'),
        initialValue: _binId,
        isExpanded: true,
        style: context.text.bodyMedium?.copyWith(
          color: context.colors.textPrimary,
        ),
        icon: const AppIcon(AppIcons.arrowDown2, size: AppSize.icon),
        decoration: InputDecoration(errorText: error, errorMaxLines: 2),
        items: [
          for (final l in state.locations)
            DropdownMenuItem(
              value: l.id,
              child: Text(
                l.barcode == null ? l.code : '${l.code} · ${l.barcode}',
                overflow: TextOverflow.ellipsis,
              ),
            ),
        ],
        onChanged: (id) => setState(() {
          _binId = id;
          _binScanError = null;
        }),
      ),
    );
  }
}

class _LinesSection extends StatelessWidget {
  const _LinesSection({required this.state, required this.controller});

  final ReceivingState state;
  final ReceivingController controller;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    if (state.lines.isEmpty) {
      return EmptyState(
        key: const Key('receiving-no-lines'),
        compact: true,
        icon: AppIcons.trolley01,
        title: l10n.receivingNoLines,
        body: l10n.receivingNoLinesHint,
      );
    }
    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        Text(
          l10n.receivingLinesTitle(state.lines.length),
          style: context.text.titleMedium,
        ),
        const SizedBox(height: AppSpacing.xs),
        for (final line in state.lines)
          Card(
            key: Key('receiving-line-${line.key}'),
            child: Padding(
              padding: const EdgeInsets.fromLTRB(
                AppSpacing.md,
                AppSpacing.xs,
                AppSpacing.xxs,
                AppSpacing.xs,
              ),
              child: Row(
                children: [
                  Expanded(
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      children: [
                        VariantHeader(variant: line.variant),
                        Text(
                          line.location == null
                              ? l10n.receivingLineQuantity(
                                  formatQuantity(line.quantity),
                                )
                              : l10n.receivingLineQuantityBin(
                                  formatQuantity(line.quantity),
                                  line.location!.code,
                                ),
                          style: context.text.titleMedium,
                        ),
                        for (final message
                            in state.lineErrors[line.key] ?? const <String>[])
                          Text(
                            stockFieldMessage(l10n, message),
                            style: TextStyle(color: context.colors.danger.fg),
                          ),
                      ],
                    ),
                  ),
                  IconButton(
                    key: Key('receiving-remove-${line.key}'),
                    tooltip: l10n.receivingRemoveLine(line.variant.sku),
                    onPressed: state.locked
                        ? null
                        : () => controller.removeLine(line.key),
                    icon: const AppIcon(AppIcons.delete2),
                  ),
                ],
              ),
            ),
          ),
      ],
    );
  }
}

class _SuccessNotice extends StatelessWidget {
  const _SuccessNotice({
    required this.submission,
    required this.onShowStock,
    required this.onNew,
  });

  final SubmissionSucceeded submission;
  final VoidCallback onShowStock;
  final VoidCallback onNew;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final movements = submission.result.movements;
    return StockNotice(
      key: const Key('receiving-success'),
      tone: Tone.success,
      icon: AppIcons.checkCircle1,
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text(l10n.receivingSuccessTitle, style: AppTextStyles.strong),
          Text(
            l10n.receivingSuccessBody(
              submission.lines,
              formatQuantity(submission.total),
            ),
          ),
          for (var i = 0; i < submission.posted.length; i++)
            if (i < movements.length)
              Text(
                l10n.receivingSuccessLine(
                  submission.posted[i].variant.sku,
                  formatQuantity(submission.posted[i].quantity),
                  formatQuantity(movements[i].balanceAfter),
                ),
              ),
          const SizedBox(height: AppSpacing.xs),
          Wrap(
            spacing: AppSpacing.xs,
            children: [
              TextButton(
                key: const Key('receiving-show-stock'),
                onPressed: onShowStock,
                style: TextButton.styleFrom(
                  foregroundColor: context.colors.success.fg,
                ),
                child: Text(l10n.receivingShowStock),
              ),
              TextButton(
                key: const Key('receiving-new'),
                onPressed: onNew,
                style: TextButton.styleFrom(
                  foregroundColor: context.colors.success.fg,
                ),
                child: Text(l10n.receivingNew),
              ),
            ],
          ),
        ],
      ),
    );
  }
}

class _FailureNotice extends StatelessWidget {
  const _FailureNotice({
    required this.submission,
    required this.formErrors,
    required this.onRetry,
    required this.onDiscard,
  });

  final SubmissionFailed submission;
  final List<String> formErrors;
  final VoidCallback onRetry;
  final VoidCallback onDiscard;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    return StockNotice(
      key: const Key('receiving-failure'),
      tone: Tone.danger,
      icon: AppIcons.infoCircle,
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text(l10n.receivingFailedTitle, style: AppTextStyles.strong),
          Text(
            stockErrorMessage(
              l10n,
              submission.error,
              forbidden: l10n.receivingForbidden,
            ),
            key: const Key('receiving-failure-message'),
          ),
          for (final message in formErrors)
            Text('• ${stockFieldMessage(l10n, message)}'),
          if (submission.pending) ...[
            const SizedBox(height: AppSpacing.xxs),
            Text(l10n.receivingPendingHint),
            const SizedBox(height: AppSpacing.xs),
            Wrap(
              spacing: AppSpacing.xs,
              runSpacing: AppSpacing.xs,
              children: [
                FilledButton.icon(
                  key: const Key('receiving-retry'),
                  onPressed: onRetry,
                  icon: const AppIcon(AppIcons.refresh),
                  label: Text(l10n.resend),
                  style: FilledButton.styleFrom(
                    backgroundColor: context.colors.dangerSolid,
                  ),
                ),
                TextButton(
                  key: const Key('receiving-discard'),
                  onPressed: onDiscard,
                  style: TextButton.styleFrom(
                    foregroundColor: context.colors.danger.fg,
                  ),
                  child: Text(l10n.receivingDiscard),
                ),
              ],
            ),
          ],
        ],
      ),
    );
  }
}

class _SubmitBar extends StatelessWidget {
  const _SubmitBar({required this.state, required this.onSubmit});

  final ReceivingState state;
  final VoidCallback onSubmit;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final sending = state.submission is SubmissionSending;
    final retryingBusy = switch (state.submission) {
      SubmissionSending(:final retryingBusy) => retryingBusy,
      _ => false,
    };
    final pending = switch (state.submission) {
      SubmissionFailed(:final pending) => pending,
      _ => false,
    };
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
              Semantics(
                liveRegion: true,
                child: Text(
                  retryingBusy
                      ? l10n.receivingBusyRetrying
                      : l10n.receivingSummary(
                          state.lines.length,
                          formatQuantity(state.total),
                        ),
                  key: const Key('receiving-summary'),
                  textAlign: TextAlign.center,
                ),
              ),
              const SizedBox(height: AppSpacing.xs),
              FilledButton.icon(
                key: const Key('receiving-submit'),
                // After an unknown outcome the retry lives in the notice
                // (same key); the main button stays off until resolved.
                onPressed:
                    state.lines.isEmpty ||
                        sending ||
                        pending ||
                        state.current != null
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
                    : const AppIcon(AppIcons.trolley01),
                label: Text(
                  sending ? l10n.receivingSubmitting : l10n.receivingSubmit,
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
