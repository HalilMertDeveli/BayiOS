import 'dart:async';

import 'package:bayios_mobile/core/api/api_exception.dart';
import 'package:bayios_mobile/core/ui/tokens.dart';
import 'package:bayios_mobile/features/stock/adjustment_screen.dart';
import 'package:bayios_mobile/features/stock/quantity.dart';
import 'package:bayios_mobile/features/stock/stock_errors.dart';
import 'package:bayios_mobile/features/stock/stock_query_controller.dart';
import 'package:bayios_mobile/features/stock/stock_widgets.dart';
import 'package:bayios_mobile/l10n/app_localizations.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

/// Stok — search by barcode / SKU / name; balances per warehouse and bin.
/// "Mal kabul" and "Düzelt" appear only with `inventory.receive` /
/// `inventory.adjust` (from `GET /me`); the API checks them again.
class StockQueryScreen extends ConsumerStatefulWidget {
  const StockQueryScreen({super.key, this.initialQuery});

  /// `/stok?q=…` (e.g. "Stokta gör" after a receipt).
  final String? initialQuery;

  @override
  ConsumerState<StockQueryScreen> createState() => _StockQueryScreenState();
}

class _StockQueryScreenState extends ConsumerState<StockQueryScreen> {
  @override
  void initState() {
    super.initState();
    _searchInitial();
  }

  @override
  void didUpdateWidget(StockQueryScreen oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (widget.initialQuery != oldWidget.initialQuery) _searchInitial();
  }

  void _searchInitial() {
    final q = widget.initialQuery?.trim();
    if (q == null || q.isEmpty) return;
    // Providers may not change while the tree builds.
    scheduleMicrotask(() {
      if (mounted) {
        unawaited(ref.read(stockQueryControllerProvider.notifier).search(q));
      }
    });
  }

  Future<void> _adjust(StockGroup group, StockRow row) async {
    final controller = ref.read(stockQueryControllerProvider.notifier);
    final saved = await context.push<bool>(
      '/stok/duzeltme',
      extra: AdjustmentTarget(
        variant: group.variant,
        balance: row.balance,
        warehouse: row.warehouse,
        location: row.location,
      ),
    );
    if (saved ?? false) await controller.refresh();
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final access = ref.watch(stockAccessProvider);
    final state = ref.watch(stockQueryControllerProvider);
    final controller = ref.read(stockQueryControllerProvider.notifier);
    final canAdjust = access.can(StockPermissions.adjust);

    return Scaffold(
      appBar: AppBar(title: Text(l10n.stockTitle)),
      body: RefreshIndicator(
        onRefresh: controller.refresh,
        child: ListView(
          padding: const EdgeInsets.all(BosSpace.s16),
          children: [
            if (access.can(StockPermissions.receive)) ...[
              FilledButton.icon(
                key: const Key('stock-receive'),
                onPressed: () => context.push('/stok/mal-kabul'),
                icon: const Icon(Icons.move_to_inbox_outlined),
                label: Text(l10n.receivingTitle),
                style: FilledButton.styleFrom(
                  minimumSize: const Size.fromHeight(BosSize.primaryAction),
                ),
              ),
              const SizedBox(height: BosSpace.s16),
            ],
            CodeEntryField(
              fieldKey: const Key('stock-search-field'),
              scanKey: const Key('stock-scan'),
              label: l10n.stockSearchLabel,
              hint: l10n.stockSearchHint,
              scanTitle: l10n.stockScanTitle,
              initialValue: widget.initialQuery,
              clearOnSubmit: false,
              busy: state is StockQueryLoading,
              onSubmit: controller.search,
            ),
            const SizedBox(height: BosSpace.s16),
            ..._body(context, l10n, state, controller, canAdjust: canAdjust),
          ],
        ),
      ),
    );
  }

  List<Widget> _body(
    BuildContext context,
    AppLocalizations l10n,
    StockQueryState state,
    StockQueryController controller, {
    required bool canAdjust,
  }) {
    switch (state) {
      case StockQueryIdle():
        return [
          StockMessagePanel(
            key: const Key('stock-idle'),
            icon: Icons.manage_search,
            message: l10n.stockIdle,
          ),
        ];
      case StockQueryLoading():
        return [
          Padding(
            padding: const EdgeInsets.all(BosSpace.s24),
            child: Center(
              child: Semantics(
                liveRegion: true,
                label: l10n.stockSearching,
                child: const CircularProgressIndicator(),
              ),
            ),
          ),
        ];
      case StockQueryFailed(:final error):
        final retryable = error is! ApiException || error.isTransient;
        return [
          StockMessagePanel(
            key: const Key('stock-error'),
            icon: retryable ? Icons.cloud_off_outlined : Icons.error_outline,
            tone: BosColors.danger,
            message: stockErrorMessage(l10n, error),
            onRetry: retryable ? controller.refresh : null,
          ),
        ];
      case StockQueryEmpty(:final term, :final variant):
        return [
          if (variant != null) ...[
            Card(
              child: Padding(
                padding: const EdgeInsets.all(BosSpace.s16),
                child: VariantHeader(variant: variant),
              ),
            ),
            const SizedBox(height: BosSpace.s8),
          ],
          StockMessagePanel(
            key: const Key('stock-empty'),
            icon: Icons.inventory_2_outlined,
            message: variant != null
                ? l10n.stockNoBalanceForVariant
                : l10n.stockNoResults(term),
          ),
        ];
      case StockQueryResults(:final groups, :final hasMore):
        return [
          for (final group in groups) ...[
            _GroupCard(
              group: group,
              canAdjust: canAdjust,
              onAdjust: (row) => _adjust(group, row),
            ),
            const SizedBox(height: BosSpace.s12),
          ],
          if (hasMore)
            StockNotice(
              tone: BosColors.info,
              icon: Icons.info_outline,
              child: Text(l10n.stockMoreResults),
            ),
        ];
    }
  }
}

class _GroupCard extends StatelessWidget {
  const _GroupCard({
    required this.group,
    required this.canAdjust,
    required this.onAdjust,
  });

  final StockGroup group;
  final bool canAdjust;
  final ValueChanged<StockRow> onAdjust;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    return Card(
      key: Key('stock-group-${group.variant.id}'),
      child: Padding(
        padding: const EdgeInsets.all(BosSpace.s16),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            VariantHeader(variant: group.variant),
            const SizedBox(height: BosSpace.s4),
            Text(
              l10n.stockTotals(
                formatQuantity(group.totalOnHand),
                formatQuantity(group.totalAvailable),
              ),
              style: Theme.of(context).textTheme.bodyMedium,
            ),
            for (final row in group.rows) ...[
              const Divider(height: BosSpace.s24),
              _BalanceRow(
                row: row,
                onAdjust: canAdjust ? () => onAdjust(row) : null,
              ),
            ],
          ],
        ),
      ),
    );
  }
}

class _BalanceRow extends StatelessWidget {
  const _BalanceRow({required this.row, this.onAdjust});

  final StockRow row;
  final VoidCallback? onAdjust;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final theme = Theme.of(context);
    final b = row.balance;
    return Column(
      key: Key('stock-balance-${b.id}'),
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        Row(
          children: [
            const Icon(Icons.warehouse_outlined, color: BosColors.textMuted),
            const SizedBox(width: BosSpace.s8),
            Expanded(
              child: Text(
                placeLabel(row.warehouse, row.location),
                style: theme.textTheme.titleMedium,
              ),
            ),
            if (b.belowMinimum)
              Container(
                key: Key('stock-critical-${b.id}'),
                padding: const EdgeInsets.symmetric(
                  horizontal: BosSpace.s8,
                  vertical: BosSpace.s2,
                ),
                decoration: BoxDecoration(
                  color: BosColors.danger.bg,
                  border: Border.all(color: BosColors.danger.border),
                  borderRadius: BorderRadius.circular(BosRadius.sm),
                ),
                child: Text(
                  l10n.stockCritical,
                  style: TextStyle(
                    color: BosColors.danger.fg,
                    fontWeight: FontWeight.w600,
                  ),
                ),
              ),
          ],
        ),
        const SizedBox(height: BosSpace.s8),
        Row(
          children: [
            _Figure(label: l10n.stockOnHand, value: b.onHand),
            _Figure(label: l10n.stockReserved, value: b.reserved),
            _Figure(label: l10n.stockAvailable, value: b.available, bold: true),
          ],
        ),
        if (b.minQuantity != null) ...[
          const SizedBox(height: BosSpace.s4),
          Text(
            l10n.stockMinimum(formatQuantity(b.minQuantity!)),
            style: theme.textTheme.bodyMedium?.copyWith(
              color: b.belowMinimum ? BosColors.danger.fg : BosColors.textMuted,
            ),
          ),
        ],
        if (onAdjust != null) ...[
          const SizedBox(height: BosSpace.s8),
          Align(
            alignment: Alignment.centerRight,
            child: OutlinedButton.icon(
              key: Key('stock-adjust-${b.id}'),
              onPressed: onAdjust,
              icon: const Icon(Icons.tune),
              label: Text(l10n.stockAdjustAction),
            ),
          ),
        ],
      ],
    );
  }
}

class _Figure extends StatelessWidget {
  const _Figure({required this.label, required this.value, this.bold = false});

  final String label;
  final num value;
  final bool bold;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return Expanded(
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text(
            label,
            style: theme.textTheme.bodySmall?.copyWith(
              color: BosColors.textMuted,
            ),
          ),
          Text(
            formatQuantity(value),
            style: BosType.h3.copyWith(
              fontWeight: bold ? FontWeight.w700 : FontWeight.w500,
              fontFeatures: const [FontFeature.tabularFigures()],
            ),
          ),
        ],
      ),
    );
  }
}
