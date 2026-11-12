import 'dart:async';

import 'package:bayios_mobile/core/api/api_exception.dart';
import 'package:bayios_mobile/core/theme/app_colors.dart';
import 'package:bayios_mobile/core/theme/app_icon.dart';
import 'package:bayios_mobile/core/theme/app_spacing.dart';
import 'package:bayios_mobile/core/theme/app_theme.dart';
import 'package:bayios_mobile/core/ui/components.dart';
import 'package:bayios_mobile/core/ui/widgets.dart';
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
      appBar: AppTopBar(title: l10n.stockTitle, trailing: const UserAvatar()),
      body: RefreshIndicator(
        onRefresh: controller.refresh,
        child: ListView(
          padding: const EdgeInsets.all(AppSpacing.md),
          children: [
            if (access.can(StockPermissions.receive)) ...[
              OutlinedButton.icon(
                key: const Key('stock-receive'),
                onPressed: () => context.push('/stok/mal-kabul'),
                icon: const AppIcon(AppIcons.trolley01),
                label: Text(l10n.receivingTitle),
                style: OutlinedButton.styleFrom(
                  minimumSize: const Size.fromHeight(AppSize.minTouchTarget),
                ),
              ),
              const SizedBox(height: AppSpacing.md),
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
            const SizedBox(height: AppSpacing.md),
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
            icon: AppIcons.search,
            message: l10n.stockIdle,
          ),
        ];
      case StockQueryLoading():
        return [SkeletonList(count: 2, label: l10n.stockSearching)];
      case StockQueryFailed(:final error):
        final retryable = error is! ApiException || error.isTransient;
        return [
          StockMessagePanel(
            key: const Key('stock-error'),
            icon: retryable ? AppIcons.offline : AppIcons.infoCircle,
            tone: Tone.danger,
            message: stockErrorMessage(l10n, error),
            onRetry: retryable ? controller.refresh : null,
            error: error,
          ),
        ];
      case StockQueryEmpty(:final term, :final variant):
        return [
          if (variant != null) ...[
            AppCard(child: VariantHeader(variant: variant)),
            const SizedBox(height: AppSpacing.xs),
          ],
          StockMessagePanel(
            key: const Key('stock-empty'),
            icon: AppIcons.package,
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
            const SizedBox(height: AppSpacing.sm),
          ],
          if (hasMore)
            StockNotice(
              tone: Tone.info,
              icon: AppIcons.infoCircle,
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
    return AppCard(
      key: Key('stock-group-${group.variant.id}'),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          VariantHeader(variant: group.variant),
          const SizedBox(height: AppSpacing.xxs),
          Text(
            l10n.stockTotals(
              formatQuantity(group.totalOnHand),
              formatQuantity(group.totalAvailable),
            ),
            style: context.text.bodySmall?.copyWith(
              color: context.colors.textSecondary,
            ),
          ),
          for (final row in group.rows) ...[
            const Divider(height: AppSpacing.lg),
            _BalanceRow(
              row: row,
              onAdjust: canAdjust ? () => onAdjust(row) : null,
            ),
          ],
        ],
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
    final b = row.balance;
    return Column(
      key: Key('stock-balance-${b.id}'),
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        Row(
          children: [
            AppIcon(
              AppIcons.pin3,
              size: AppSize.icon,
              color: context.colors.textMuted,
            ),
            const SizedBox(width: AppSpacing.xs),
            Expanded(
              child: Text(
                placeLabel(row.warehouse, row.location),
                style: context.text.titleSmall?.copyWith(
                  color: context.colors.textPrimary,
                ),
              ),
            ),
            if (b.belowMinimum)
              StatusChip(
                l10n.stockCritical,
                Tone.danger,
                key: Key('stock-critical-${b.id}'),
                icon: AppIcons.trendDownGraph,
              ),
          ],
        ),
        const SizedBox(height: AppSpacing.xs),
        Row(
          children: [
            _Figure(label: l10n.stockOnHand, value: b.onHand),
            const SizedBox(width: AppSpacing.xs),
            _Figure(label: l10n.stockReserved, value: b.reserved),
            const SizedBox(width: AppSpacing.xs),
            _Figure(label: l10n.stockAvailable, value: b.available, bold: true),
          ],
        ),
        if (b.minQuantity != null) ...[
          const SizedBox(height: AppSpacing.xxs),
          Text(
            l10n.stockMinimum(formatQuantity(b.minQuantity!)),
            style: context.text.bodySmall?.copyWith(
              color: b.belowMinimum
                  ? context.colors.danger.fg
                  : context.colors.textMuted,
            ),
          ),
        ],
        if (onAdjust != null) ...[
          const SizedBox(height: AppSpacing.xs),
          Align(
            alignment: Alignment.centerRight,
            child: OutlinedButton.icon(
              key: Key('stock-adjust-${b.id}'),
              onPressed: onAdjust,
              icon: const AppIcon(AppIcons.preferences2),
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
    final c = context.colors;
    return Expanded(
      child: DecoratedBox(
        decoration: BoxDecoration(
          color: bold ? c.brandPrimarySubtle : c.surfaceSunken,
          border: Border.all(color: bold ? c.brand.border : c.borderDefault),
          borderRadius: AppRadius.smAll,
        ),
        child: Padding(
          padding: const EdgeInsets.all(AppSpacing.xs),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Text(
                label,
                style: context.text.labelMedium?.copyWith(color: c.textMuted),
              ),
              Text(
                formatQuantity(value),
                style:
                    (bold
                            ? context.text.titleLarge?.bold
                            : context.text.titleLarge)
                        ?.tabular
                        .copyWith(color: bold ? c.brand.fg : c.textPrimary),
              ),
            ],
          ),
        ),
      ),
    );
  }
}
