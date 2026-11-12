import 'dart:async';

import 'package:bayios_mobile/core/theme/app_colors.dart';
import 'package:bayios_mobile/core/theme/app_icon.dart';
import 'package:bayios_mobile/core/theme/app_spacing.dart';
import 'package:bayios_mobile/core/ui/components.dart';
import 'package:bayios_mobile/core/ui/widgets.dart';
import 'package:bayios_mobile/features/picking/tasks_widgets.dart';
import 'package:bayios_mobile/features/stock/quantity.dart';
import 'package:bayios_mobile/features/stock/stock_api.dart';
import 'package:bayios_mobile/features/stock/stock_errors.dart';
import 'package:bayios_mobile/features/stock/stock_models.dart';
import 'package:bayios_mobile/features/warehouse/warehouse_api.dart';
import 'package:bayios_mobile/features/warehouse/warehouse_errors.dart';
import 'package:bayios_mobile/features/warehouse/warehouse_models.dart';
import 'package:bayios_mobile/features/warehouse/warehouse_widgets.dart';
import 'package:bayios_mobile/l10n/app_localizations.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

String transferPath(String id) =>
    '/depo/transferler/${Uri.encodeComponent(id)}';

/// Status badge of a transfer.
(String, Tone) transferStatusMeta(AppLocalizations l10n, String status) =>
    switch (status) {
      TransferStatus.inTransit => (l10n.transferStatusInTransit, Tone.info),
      TransferStatus.partiallyReceived => (
        l10n.transferStatusPartial,
        Tone.warning,
      ),
      TransferStatus.received => (l10n.transferStatusReceived, Tone.success),
      _ => (status, Tone.neutral),
    };

/// Gelen transferler: `in_transit` / `partially_received` transfers whose
/// destination is the chosen warehouse (every warehouse first).
class TransferListScreen extends ConsumerStatefulWidget {
  const TransferListScreen({super.key});

  @override
  ConsumerState<TransferListScreen> createState() => _TransferListState();
}

class _TransferListState extends ConsumerState<TransferListScreen> {
  bool _loading = true;
  Object? _error;
  List<Warehouse> _warehouses = const [];

  /// Destination filter; null = every warehouse.
  String? _warehouseId;
  final _items = <TransferSummary>[];
  String? _cursor;
  bool _loadingMore = false;
  Object? _moreError;

  @override
  void initState() {
    super.initState();
    if (ref
        .read(stockAccessProvider)
        .canSome(WarehousePermissions.receiveTransfer)) {
      unawaited(_load());
    }
  }

  Future<void> _load() async {
    setState(() {
      _loading = true;
      _error = null;
    });
    try {
      if (_warehouses.isEmpty) {
        final all = await ref.read(stockApiProvider).warehouses();
        final active = all.where((w) => w.isActive).toList();
        // Starts on "every warehouse": a transfer to a branch must not be
        // hidden behind the default warehouse; the receiver narrows it.
        _warehouses = active;
        _warehouseId = null;
      }
      final page = await _page(null);
      if (!mounted) return;
      setState(() {
        _items
          ..clear()
          ..addAll(_incoming(page.items));
        _cursor = page.page.hasMore ? page.page.nextCursor : null;
        _loading = false;
        _moreError = null;
      });
    } on Object catch (error) {
      if (mounted) {
        setState(() {
          _loading = false;
          _error = error;
        });
      }
    }
  }

  Future<TransferPage> _page(String? cursor) => ref
      .read(warehouseApiProvider)
      .transfers(
        statuses: TransferStatus.receivable,
        warehouseId: _warehouseId,
        cursor: cursor,
      );

  /// The API's warehouse filter matches source or destination; a receiver
  /// only wants what is coming in.
  List<TransferSummary> _incoming(List<TransferSummary> items) => [
    for (final t in items)
      if (_warehouseId == null || t.destinationWarehouseId == _warehouseId) t,
  ];

  Future<void> _loadMore() async {
    final cursor = _cursor;
    if (cursor == null || _loadingMore) return;
    setState(() {
      _loadingMore = true;
      _moreError = null;
    });
    try {
      final page = await _page(cursor);
      if (!mounted) return;
      setState(() {
        _items.addAll(_incoming(page.items));
        _cursor = page.page.hasMore ? page.page.nextCursor : null;
      });
    } on Object catch (error) {
      if (mounted) setState(() => _moreError = error);
    } finally {
      if (mounted) setState(() => _loadingMore = false);
    }
  }

  void _selectWarehouse(String? id) {
    if (id == _warehouseId) return;
    _warehouseId = id;
    unawaited(_load());
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final access = ref.watch(stockAccessProvider);
    if (!access.canSome(WarehousePermissions.receiveTransfer)) {
      return WarehouseForbidden(
        title: l10n.transferListTitle,
        message: l10n.transferForbidden,
      );
    }
    final Widget body;
    if (_loading && _items.isEmpty) {
      body = const ListLoadingView();
    } else if (_error != null) {
      body = ListView(
        physics: const AlwaysScrollableScrollPhysics(),
        children: [
          const SizedBox(height: AppSpacing.xxl),
          ErrorView(error: _error!, onRetry: _load),
        ],
      );
    } else {
      body = ListView(
        key: const Key('transfer-list'),
        physics: const AlwaysScrollableScrollPhysics(),
        padding: const EdgeInsets.all(AppSpacing.md),
        children: [
          if (_warehouses.length > 1) ...[
            _WarehouseFilter(
              warehouses: _warehouses,
              selected: _warehouseId,
              onChanged: _selectWarehouse,
            ),
            const SizedBox(height: AppSpacing.md),
          ],
          if (_items.isEmpty)
            TasksMessage(
              key: const Key('transfer-empty'),
              icon: AppIcons.packageMoving,
              title: l10n.transferEmpty,
              body: l10n.transferEmptyHint,
            )
          else
            for (final t in _items) ...[
              _TransferCard(
                transfer: t,
                onTap: () => context.go(transferPath(t.id)),
              ),
              const SizedBox(height: AppSpacing.xs),
            ],
          if (_moreError != null)
            ErrorMessageBox(documentErrorMessage(l10n, _moreError!)),
          if (_cursor != null) ...[
            const SizedBox(height: AppSpacing.xs),
            OutlinedButton(
              key: const Key('transfer-load-more'),
              onPressed: _loadingMore ? null : _loadMore,
              style: OutlinedButton.styleFrom(
                minimumSize: const Size.fromHeight(AppSize.minTouchTarget),
              ),
              child: Text(_loadingMore ? l10n.loading : l10n.tasksLoadMore),
            ),
          ],
        ],
      );
    }
    return Scaffold(
      appBar: AppTopBar(title: l10n.transferListTitle),
      body: RefreshIndicator(onRefresh: _load, child: body),
    );
  }
}

class _WarehouseFilter extends StatelessWidget {
  const _WarehouseFilter({
    required this.warehouses,
    required this.selected,
    required this.onChanged,
  });

  final List<Warehouse> warehouses;
  final String? selected;
  final ValueChanged<String?> onChanged;

  static const _all = '';

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    return LabeledField(
      label: l10n.transferDestinationLabel,
      child: DropdownButtonFormField<String>(
        key: const Key('transfer-warehouse'),
        initialValue: selected ?? _all,
        isExpanded: true,
        style: context.text.bodyMedium?.copyWith(
          color: context.colors.textPrimary,
        ),
        decoration: const InputDecoration(
          prefixIcon: AppIcon(AppIcons.store03),
        ),
        icon: const AppIcon(AppIcons.arrowDown2, size: AppSize.icon),
        items: [
          for (final w in warehouses)
            DropdownMenuItem(
              value: w.id,
              child: Text(
                w.isDefault ? l10n.receivingWarehouseDefault(w.name) : w.name,
                overflow: TextOverflow.ellipsis,
              ),
            ),
          DropdownMenuItem(
            value: _all,
            child: Text(l10n.transferAllWarehouses),
          ),
        ],
        onChanged: (id) => onChanged(id == null || id == _all ? null : id),
      ),
    );
  }
}

class _TransferCard extends StatelessWidget {
  const _TransferCard({required this.transfer, required this.onTap});

  final TransferSummary transfer;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final (label, tone) = transferStatusMeta(l10n, transfer.status);
    final done = transfer.quantity <= 0
        ? 0.0
        : ((transfer.quantity - transfer.inTransitQuantity) / transfer.quantity)
              .clamp(0, 1)
              .toDouble();
    return ListCard(
      key: Key('transfer-${transfer.id}'),
      onTap: onTap,
      leading: const IconBadge(icon: AppIcons.packageMoving, tone: Tone.info),
      title: transfer.number,
      subtitle: l10n.transferRoute(
        transfer.sourceWarehouseName ?? '—',
        transfer.destinationWarehouseName ?? '—',
      ),
      footer: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Wrap(
            spacing: AppSpacing.xs,
            runSpacing: AppSpacing.xxs,
            children: [
              StatusChip(label, tone),
              if (transfer.isOverdue)
                StatusChip(
                  l10n.transferOverdue,
                  Tone.danger,
                  icon: AppIcons.calendar,
                ),
            ],
          ),
          const SizedBox(height: AppSpacing.xs),
          PickProgressBar(value: done),
          const SizedBox(height: AppSpacing.xxs),
          Text(
            l10n.transferProgress(
              transfer.lineCount,
              formatQuantity(transfer.inTransitQuantity),
              formatQuantity(transfer.quantity),
            ),
            style: context.text.labelMedium?.copyWith(
              color: context.colors.textMuted,
            ),
          ),
        ],
      ),
    );
  }
}
