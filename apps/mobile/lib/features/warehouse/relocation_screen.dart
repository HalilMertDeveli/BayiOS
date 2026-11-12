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
import 'package:bayios_mobile/features/warehouse/relocation_controller.dart';
import 'package:bayios_mobile/features/warehouse/warehouse_api.dart';
import 'package:bayios_mobile/features/warehouse/warehouse_errors.dart';
import 'package:bayios_mobile/features/warehouse/warehouse_widgets.dart';
import 'package:bayios_mobile/l10n/app_localizations.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

/// Raf taşıma: 1 source shelf → 2 product → 3 quantity → 4 target shelf →
/// "Taşı" (one `POST /inventory/relocations`).
class RelocationScreen extends ConsumerStatefulWidget {
  const RelocationScreen({super.key});

  @override
  ConsumerState<RelocationScreen> createState() => _RelocationScreenState();
}

class _RelocationScreenState extends ConsumerState<RelocationScreen> {
  late final RelocationController _controller;

  @override
  void initState() {
    super.initState();
    _controller = RelocationController(
      api: ref.read(warehouseApiProvider),
      stock: ref.read(stockApiProvider),
      alerts: ref.read(scanAlertsProvider),
      wait: ref.read(stockDelayProvider),
    );
    // Without the permission the screen only explains (no API call).
    if (ref.read(stockAccessProvider).canSome(WarehousePermissions.relocate)) {
      unawaited(_controller.load());
    }
  }

  @override
  void dispose() {
    _controller.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final access = ref.watch(stockAccessProvider);
    if (!access.canSome(WarehousePermissions.relocate)) {
      return WarehouseForbidden(
        title: l10n.relocationTitle,
        message: l10n.relocationForbidden,
      );
    }
    return ListenableBuilder(
      listenable: _controller,
      builder: (context, _) {
        final c = _controller;
        final Widget body;
        if (c.loading) {
          body = const LoadingView();
        } else if (c.loadError != null) {
          body = Center(
            child: StockMessagePanel(
              key: const Key('relocation-load-error'),
              icon: AppIcons.offline,
              tone: Tone.danger,
              message: documentErrorMessage(l10n, c.loadError!),
              onRetry: c.load,
              error: c.loadError,
            ),
          );
        } else if (c.warehouses.isEmpty) {
          body = StockMessagePanel(
            key: const Key('relocation-no-warehouse'),
            icon: AppIcons.shelves,
            message: l10n.relocationNoWarehouse,
          );
        } else {
          body = _RelocationForm(controller: c);
        }
        return Scaffold(
          appBar: AppTopBar(title: l10n.relocationTitle),
          body: body,
          bottomNavigationBar:
              c.loading || c.loadError != null || c.warehouses.isEmpty
              ? null
              : _MoveBar(controller: c),
        );
      },
    );
  }
}

class _RelocationForm extends StatelessWidget {
  const _RelocationForm({required this.controller});

  final RelocationController controller;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final c = controller;
    final submission = c.submission;
    return ListView(
      key: const Key('relocation-form'),
      padding: const EdgeInsets.all(AppSpacing.md),
      children: [
        if (submission is MoveDone) ...[
          StockNotice(
            key: const Key('relocation-done'),
            tone: Tone.success,
            icon: AppIcons.checkCircle1,
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(l10n.relocationDone, style: AppTextStyles.strong),
                Text(
                  l10n.relocationDoneLine(
                    submission.sku,
                    formatQuantity(submission.quantity),
                    submission.from,
                    submission.to,
                  ),
                ),
                if (submission.result.reservationMoves > 0)
                  Text(
                    l10n.relocationReservationsMoved(
                      submission.result.reservationMoves,
                    ),
                    key: const Key('relocation-reservations-moved'),
                  ),
              ],
            ),
          ),
          const SizedBox(height: AppSpacing.md),
        ],
        if (submission is MoveFailed) ...[
          _FailureNotice(controller: c, failure: submission),
          const SizedBox(height: AppSpacing.md),
        ],
        if (c.warehouses.length > 1) ...[
          LabeledField(
            label: l10n.relocationWarehouseLabel,
            child: DropdownButtonFormField<String>(
              key: ValueKey('relocation-warehouse-${c.warehouse?.id}'),
              initialValue: c.warehouse?.id,
              isExpanded: true,
              style: context.text.bodyMedium?.copyWith(
                color: context.colors.textPrimary,
              ),
              icon: const AppIcon(AppIcons.arrowDown2, size: AppSize.icon),
              decoration: const InputDecoration(),
              items: [
                for (final w in c.warehouses)
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
              onChanged: c.locked
                  ? null
                  : (id) {
                      if (id != null) unawaited(c.selectWarehouse(id));
                    },
            ),
          ),
          const SizedBox(height: AppSpacing.md),
        ],
        if (c.shelvesLoading)
          LoadingView(label: l10n.receivingBins)
        else if (c.shelvesError != null)
          StockMessagePanel(
            icon: AppIcons.offline,
            tone: Tone.danger,
            message: documentErrorMessage(l10n, c.shelvesError!),
            onRetry: c.retryShelves,
            error: c.shelvesError,
          )
        else ...[
          _Step(
            index: 1,
            title: l10n.relocationStepSource,
            value: c.source?.code,
            active: c.step == MoveStep.source,
            changeKey: const Key('relocation-change-source'),
            onChange: c.locked ? null : c.changeSource,
            child: CodeEntryField(
              fieldKey: const Key('relocation-source-field'),
              scanKey: const Key('relocation-source-scan'),
              label: l10n.docShelfLabel,
              scanTitle: l10n.docShelfScanTitle,
              enabled: !c.locked,
              onSubmit: c.scanSource,
            ),
          ),
          _Step(
            index: 2,
            title: l10n.relocationStepProduct,
            value: c.variant == null
                ? null
                : [c.variant!.sku, ?c.variant!.title].join(' · '),
            active: c.step == MoveStep.product,
            changeKey: const Key('relocation-change-product'),
            onChange: c.locked ? null : c.changeProduct,
            child: CodeEntryField(
              fieldKey: const Key('relocation-product-field'),
              scanKey: const Key('relocation-product-scan'),
              label: l10n.docProductLabel,
              scanTitle: l10n.docProductScanTitle,
              enabled: !c.locked,
              busy: c.lookingUp,
              onSubmit: c.scanProduct,
            ),
          ),
          if (c.variant != null) ...[
            _Step(
              index: 3,
              title: l10n.relocationStepQuantity,
              active: true,
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.stretch,
                children: [
                  QuantityStepper(
                    keyPrefix: 'relocation',
                    text: c.quantityText,
                    label: l10n.docQuantityLabel,
                    error: c.overAvailable
                        ? (c.includeReserved
                              ? l10n.relocationOverOnHand(
                                  formatQuantity(c.movable ?? 0),
                                )
                              : l10n.relocationOverAvailable(
                                  formatQuantity(c.movable ?? 0),
                                ))
                        : quantityErrorText(l10n, c.quantityError),
                    enabled: !c.locked,
                    onChanged: c.setQuantityText,
                    onStep: c.stepQuantity,
                  ),
                  const SizedBox(height: AppSpacing.xs),
                  _AvailableLine(controller: c),
                  if (c.hasReserved)
                    SwitchListTile(
                      key: const Key('relocation-include-reserved'),
                      contentPadding: EdgeInsets.zero,
                      value: c.includeReserved,
                      onChanged: c.locked
                          ? null
                          : (value) => c.setIncludeReserved(value: value),
                      title: Text(
                        l10n.relocationIncludeReserved(
                          formatQuantity(c.balance!.reserved),
                        ),
                      ),
                      subtitle: Text(l10n.relocationIncludeReservedHint),
                    ),
                ],
              ),
            ),
            _Step(
              index: 4,
              title: l10n.relocationStepTarget,
              value: c.target?.code,
              active: c.step == MoveStep.target,
              changeKey: const Key('relocation-change-target'),
              onChange: c.locked ? null : c.changeTarget,
              child: CodeEntryField(
                fieldKey: const Key('relocation-target-field'),
                scanKey: const Key('relocation-target-scan'),
                label: l10n.docShelfLabel,
                scanTitle: l10n.docShelfScanTitle,
                enabled: !c.locked,
                onSubmit: c.scanTarget,
              ),
            ),
          ],
          if (c.problem case final MoveScanProblem problem) ...[
            const SizedBox(height: AppSpacing.xs),
            StockNotice(
              key: const Key('relocation-problem'),
              tone: Tone.danger,
              icon: AppIcons.block,
              child: Text(_problemText(l10n, problem)),
            ),
          ],
        ],
      ],
    );
  }

  static String _problemText(AppLocalizations l10n, MoveScanProblem p) =>
      switch (p) {
        ShelfNotFound(:final code) => l10n.relocationShelfNotFound(code),
        SameShelf() => l10n.docFieldSameShelf,
        ProductNotFound(:final code) => l10n.receivingScanNotFound(code),
        ProductInactive(:final sku) => l10n.receivingScanInactive(sku),
        LookupFailed(:final error) => documentErrorMessage(l10n, error),
      };
}

/// One numbered step: done (value + "Değiştir"), active (its scan field)
/// or still waiting (title only).
class _Step extends StatelessWidget {
  const _Step({
    required this.index,
    required this.title,
    required this.active,
    required this.child,
    this.value,
    this.changeKey,
    this.onChange,
  });

  final int index;
  final String title;
  final String? value;
  final bool active;
  final Widget child;
  final Key? changeKey;
  final VoidCallback? onChange;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final done = value != null;
    return Padding(
      padding: const EdgeInsets.only(bottom: AppSpacing.xs),
      child: AppCard(
        borderColor: active ? context.colors.brandPrimary : null,
        padding: const EdgeInsets.all(AppSpacing.sm),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            Row(
              children: [
                Container(
                  width: AppSize.iconLg,
                  height: AppSize.iconLg,
                  alignment: Alignment.center,
                  decoration: BoxDecoration(
                    shape: BoxShape.circle,
                    color: done
                        ? context.colors.success.bg
                        : active
                        ? context.colors.brandPrimary
                        : context.colors.surfaceSunken,
                    border: Border.all(
                      color: done
                          ? context.colors.success.border
                          : active
                          ? context.colors.brandPrimary
                          : context.colors.borderDefault,
                    ),
                  ),
                  child: done
                      ? AppIcon(
                          AppIcons.check,
                          size: AppSize.iconSm,
                          color: context.colors.success.fg,
                        )
                      : Text(
                          '$index',
                          style: context.text.titleSmall?.bold.copyWith(
                            color: active
                                ? context.colors.brandOnPrimary
                                : context.colors.textMuted,
                          ),
                        ),
                ),
                const SizedBox(width: AppSpacing.sm),
                Expanded(
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Text(title, style: context.text.titleMedium),
                      if (value != null)
                        Text(value!, style: context.text.titleMedium?.tabular),
                    ],
                  ),
                ),
                if (done && onChange != null)
                  TextButton(
                    key: changeKey,
                    onPressed: onChange,
                    child: Text(l10n.docChange),
                  ),
              ],
            ),
            if (active && !done) ...[
              const SizedBox(height: AppSpacing.sm),
              child,
            ],
          ],
        ),
      ),
    );
  }
}

class _AvailableLine extends StatelessWidget {
  const _AvailableLine({required this.controller});

  final RelocationController controller;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final c = controller;
    if (c.balanceLoading) {
      return Text(
        l10n.loading,
        style: TextStyle(color: context.colors.textMuted),
      );
    }
    final available = c.balance?.available;
    return Row(
      children: [
        Expanded(
          child: Text(
            available == null
                ? l10n.relocationNoStock
                : l10n.relocationAvailable(formatQuantity(available)),
            key: const Key('relocation-available'),
            style: TextStyle(
              color: available == null || available <= 0
                  ? context.colors.danger.fg
                  : context.colors.textMuted,
            ),
          ),
        ),
        if ((c.movable ?? 0) > 0)
          TextButton(
            key: const Key('relocation-take-all'),
            onPressed: c.locked ? null : c.takeAll,
            child: Text(l10n.relocationTakeAll),
          ),
      ],
    );
  }
}

class _FailureNotice extends StatelessWidget {
  const _FailureNotice({required this.controller, required this.failure});

  final RelocationController controller;
  final MoveFailed failure;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    return StockNotice(
      key: const Key('relocation-failure'),
      tone: Tone.danger,
      icon: AppIcons.infoCircle,
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text(l10n.relocationFailed, style: AppTextStyles.strong),
          Text(
            documentErrorMessage(
              l10n,
              failure.error,
              forbidden: l10n.relocationForbidden,
            ),
            key: const Key('relocation-failure-message'),
          ),
          if (shortageAvailable(failure.error) case final num movable)
            Text(
              l10n.relocationShortage(formatQuantity(movable)),
              key: const Key('relocation-shortage'),
            ),
          if (failure.pending) ...[
            Text(l10n.docEntryPendingHint),
            const SizedBox(height: AppSpacing.xs),
            Wrap(
              spacing: AppSpacing.xs,
              runSpacing: AppSpacing.xs,
              children: [
                FilledButton.icon(
                  key: const Key('relocation-resend'),
                  onPressed: controller.submit,
                  icon: const AppIcon(AppIcons.refresh),
                  label: Text(l10n.resend),
                  style: FilledButton.styleFrom(
                    backgroundColor: context.colors.dangerSolid,
                    minimumSize: const Size(0, AppSize.primaryAction),
                  ),
                ),
                TextButton(
                  key: const Key('relocation-discard'),
                  onPressed: controller.discardPending,
                  style: TextButton.styleFrom(
                    foregroundColor: context.colors.danger.fg,
                  ),
                  child: Text(l10n.docEntryDiscard),
                ),
              ],
            ),
          ],
        ],
      ),
    );
  }
}

class _MoveBar extends StatelessWidget {
  const _MoveBar({required this.controller});

  final RelocationController controller;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final c = controller;
    final sending = c.submission is MoveSending;
    final retrying = switch (c.submission) {
      MoveSending(:final retryingBusy) => retryingBusy,
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
              if (retrying) ...[
                Text(
                  l10n.docBusyRetrying,
                  key: const Key('relocation-busy-retry'),
                  textAlign: TextAlign.center,
                ),
                const SizedBox(height: AppSpacing.xs),
              ],
              FilledButton.icon(
                key: const Key('relocation-submit'),
                onPressed: c.step == MoveStep.ready && !c.locked
                    ? c.submit
                    : null,
                icon: sending
                    ? SizedBox.square(
                        dimension: AppSize.icon,
                        child: CircularProgressIndicator(
                          strokeWidth: 2,
                          color: context.colors.brandOnPrimary,
                        ),
                      )
                    : const AppIcon(AppIcons.trolley02),
                label: Text(
                  sending ? l10n.relocationSubmitting : l10n.relocationSubmit,
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
