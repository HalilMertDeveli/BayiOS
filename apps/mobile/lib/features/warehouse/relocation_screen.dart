import 'dart:async';

import 'package:bayios_mobile/core/ui/tokens.dart';
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
              icon: Icons.cloud_off_outlined,
              tone: BosColors.danger,
              message: documentErrorMessage(l10n, c.loadError!),
              onRetry: c.load,
              error: c.loadError,
            ),
          );
        } else if (c.warehouses.isEmpty) {
          body = StockMessagePanel(
            key: const Key('relocation-no-warehouse'),
            icon: Icons.shelves,
            message: l10n.relocationNoWarehouse,
          );
        } else {
          body = _RelocationForm(controller: c);
        }
        return Scaffold(
          appBar: AppBar(title: Text(l10n.relocationTitle)),
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
      padding: const EdgeInsets.all(BosSpace.s16),
      children: [
        if (submission is MoveDone) ...[
          StockNotice(
            key: const Key('relocation-done'),
            tone: BosColors.success,
            icon: Icons.check_circle_outline,
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(
                  l10n.relocationDone,
                  style: const TextStyle(fontWeight: FontWeight.w700),
                ),
                Text(
                  l10n.relocationDoneLine(
                    submission.sku,
                    formatQuantity(submission.quantity),
                    submission.from,
                    submission.to,
                  ),
                ),
              ],
            ),
          ),
          const SizedBox(height: BosSpace.s16),
        ],
        if (submission is MoveFailed) ...[
          _FailureNotice(controller: c, failure: submission),
          const SizedBox(height: BosSpace.s16),
        ],
        if (c.warehouses.length > 1) ...[
          DropdownButtonFormField<String>(
            key: ValueKey('relocation-warehouse-${c.warehouse?.id}'),
            initialValue: c.warehouse?.id,
            isExpanded: true,
            decoration: InputDecoration(
              labelText: l10n.relocationWarehouseLabel,
            ),
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
          const SizedBox(height: BosSpace.s16),
        ],
        if (c.shelvesLoading)
          LoadingView(label: l10n.receivingBins)
        else if (c.shelvesError != null)
          StockMessagePanel(
            icon: Icons.cloud_off_outlined,
            tone: BosColors.danger,
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
                        ? l10n.relocationOverAvailable(
                            formatQuantity(c.balance?.available ?? 0),
                          )
                        : quantityErrorText(l10n, c.quantityError),
                    enabled: !c.locked,
                    onChanged: c.setQuantityText,
                    onStep: c.stepQuantity,
                  ),
                  const SizedBox(height: BosSpace.s8),
                  _AvailableLine(controller: c),
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
            const SizedBox(height: BosSpace.s8),
            StockNotice(
              key: const Key('relocation-problem'),
              tone: BosColors.danger,
              icon: Icons.block,
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
    final theme = Theme.of(context);
    final done = value != null;
    return Card(
      shape: active
          ? RoundedRectangleBorder(
              side: const BorderSide(color: BosColors.brandPrimary, width: 2),
              borderRadius: BorderRadius.circular(BosRadius.xl),
            )
          : null,
      child: Padding(
        padding: const EdgeInsets.all(BosSpace.s12),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            Row(
              children: [
                CircleAvatar(
                  radius: 14,
                  backgroundColor: done
                      ? BosColors.success.bg
                      : BosColors.brandPrimarySubtle,
                  child: done
                      ? Icon(Icons.check, size: 18, color: BosColors.success.fg)
                      : Text(
                          '$index',
                          style: const TextStyle(
                            color: BosColors.brandPrimary,
                            fontWeight: FontWeight.w700,
                          ),
                        ),
                ),
                const SizedBox(width: BosSpace.s8),
                Expanded(
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Text(title, style: theme.textTheme.titleMedium),
                      if (value != null)
                        Text(
                          value!,
                          style: BosType.mono.copyWith(fontSize: 17),
                        ),
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
              const SizedBox(height: BosSpace.s12),
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
        style: const TextStyle(color: BosColors.textMuted),
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
                  ? BosColors.danger.fg
                  : BosColors.textMuted,
            ),
          ),
        ),
        if (available != null && available > 0)
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
      tone: BosColors.danger,
      icon: Icons.error_outline,
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text(
            l10n.relocationFailed,
            style: const TextStyle(fontWeight: FontWeight.w700),
          ),
          Text(
            documentErrorMessage(
              l10n,
              failure.error,
              forbidden: l10n.relocationForbidden,
            ),
            key: const Key('relocation-failure-message'),
          ),
          if (failure.pending) ...[
            Text(l10n.docEntryPendingHint),
            const SizedBox(height: BosSpace.s8),
            Wrap(
              spacing: BosSpace.s8,
              runSpacing: BosSpace.s8,
              children: [
                FilledButton.icon(
                  key: const Key('relocation-resend'),
                  onPressed: controller.submit,
                  icon: const Icon(Icons.refresh),
                  label: Text(l10n.resend),
                  style: FilledButton.styleFrom(
                    backgroundColor: BosColors.dangerSolid,
                    minimumSize: const Size(0, BosSize.primaryAction),
                  ),
                ),
                TextButton(
                  key: const Key('relocation-discard'),
                  onPressed: controller.discardPending,
                  style: TextButton.styleFrom(
                    foregroundColor: BosColors.danger.fg,
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
    return Material(
      elevation: 8,
      color: BosColors.surfaceCard,
      child: SafeArea(
        top: false,
        child: Padding(
          padding: const EdgeInsets.all(BosSpace.s12),
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
                const SizedBox(height: BosSpace.s8),
              ],
              FilledButton.icon(
                key: const Key('relocation-submit'),
                onPressed: c.step == MoveStep.ready && !c.locked
                    ? c.submit
                    : null,
                icon: sending
                    ? const SizedBox.square(
                        dimension: 20,
                        child: CircularProgressIndicator(
                          strokeWidth: 2,
                          color: BosColors.brandOnPrimary,
                        ),
                      )
                    : const Icon(Icons.swap_vert),
                label: Text(
                  sending ? l10n.relocationSubmitting : l10n.relocationSubmit,
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
