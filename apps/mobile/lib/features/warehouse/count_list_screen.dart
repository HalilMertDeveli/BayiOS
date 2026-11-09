import 'dart:async';

import 'package:bayios_mobile/core/ui/tokens.dart';
import 'package:bayios_mobile/core/ui/widgets.dart';
import 'package:bayios_mobile/features/picking/tasks_widgets.dart';
import 'package:bayios_mobile/features/stock/stock_errors.dart';
import 'package:bayios_mobile/features/warehouse/warehouse_api.dart';
import 'package:bayios_mobile/features/warehouse/warehouse_errors.dart';
import 'package:bayios_mobile/features/warehouse/warehouse_models.dart';
import 'package:bayios_mobile/features/warehouse/warehouse_widgets.dart';
import 'package:bayios_mobile/l10n/app_localizations.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

String countPath(String id) => '/depo/sayimlar/${Uri.encodeComponent(id)}';

String countScopeLabel(AppLocalizations l10n, String scope) => switch (scope) {
  'full' => l10n.countScopeFull,
  'locations' => l10n.countScopeLocations,
  'variants' => l10n.countScopeVariants,
  _ => scope,
};

/// Açık sayımlar (started on the web by a manager).
class CountListScreen extends ConsumerStatefulWidget {
  const CountListScreen({super.key});

  @override
  ConsumerState<CountListScreen> createState() => _CountListState();
}

class _CountListState extends ConsumerState<CountListScreen> {
  bool _loading = true;
  Object? _error;
  final _items = <CountSummary>[];
  String? _cursor;
  bool _loadingMore = false;
  Object? _moreError;

  @override
  void initState() {
    super.initState();
    if (ref
        .read(stockAccessProvider)
        .canSome(WarehousePermissions.performCount)) {
      unawaited(_load());
    }
  }

  Future<void> _load() async {
    setState(() {
      _loading = true;
      _error = null;
    });
    try {
      final page = await ref
          .read(warehouseApiProvider)
          .counts(statuses: const [CountStatus.open]);
      if (!mounted) return;
      setState(() {
        _items
          ..clear()
          ..addAll(page.items);
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

  Future<void> _loadMore() async {
    final cursor = _cursor;
    if (cursor == null || _loadingMore) return;
    setState(() {
      _loadingMore = true;
      _moreError = null;
    });
    try {
      final page = await ref
          .read(warehouseApiProvider)
          .counts(statuses: const [CountStatus.open], cursor: cursor);
      if (!mounted) return;
      setState(() {
        _items.addAll(page.items);
        _cursor = page.page.hasMore ? page.page.nextCursor : null;
      });
    } on Object catch (error) {
      if (mounted) setState(() => _moreError = error);
    } finally {
      if (mounted) setState(() => _loadingMore = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final access = ref.watch(stockAccessProvider);
    if (!access.canSome(WarehousePermissions.performCount)) {
      return WarehouseForbidden(
        title: l10n.countListTitle,
        message: l10n.countForbidden,
      );
    }
    final Widget body;
    if (_loading && _items.isEmpty) {
      body = const LoadingView();
    } else if (_error != null) {
      body = ListView(
        physics: const AlwaysScrollableScrollPhysics(),
        children: [
          const SizedBox(height: BosSpace.s48),
          ErrorView(error: _error!, onRetry: _load),
        ],
      );
    } else {
      body = ListView(
        key: const Key('count-list'),
        physics: const AlwaysScrollableScrollPhysics(),
        padding: const EdgeInsets.all(BosSpace.s16),
        children: [
          if (_items.isEmpty)
            TasksMessage(
              key: const Key('count-empty'),
              icon: Icons.fact_check_outlined,
              title: l10n.countEmpty,
              body: l10n.countEmptyHint,
            )
          else
            for (final c in _items) ...[
              _CountCard(count: c, onTap: () => context.go(countPath(c.id))),
              const SizedBox(height: BosSpace.s8),
            ],
          if (_moreError != null)
            ErrorMessageBox(documentErrorMessage(l10n, _moreError!)),
          if (_cursor != null) ...[
            const SizedBox(height: BosSpace.s8),
            OutlinedButton(
              key: const Key('count-load-more'),
              onPressed: _loadingMore ? null : _loadMore,
              style: OutlinedButton.styleFrom(
                minimumSize: const Size.fromHeight(BosSize.minTouchTarget),
              ),
              child: Text(_loadingMore ? l10n.loading : l10n.tasksLoadMore),
            ),
          ],
        ],
      );
    }
    return Scaffold(
      appBar: AppBar(title: Text(l10n.countListTitle)),
      body: RefreshIndicator(onRefresh: _load, child: body),
    );
  }
}

class _CountCard extends StatelessWidget {
  const _CountCard({required this.count, required this.onTap});

  final CountSummary count;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final theme = Theme.of(context);
    return Card(
      key: Key('count-${count.id}'),
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
                        count.number,
                        style: BosType.mono.copyWith(fontSize: 17),
                      ),
                    ),
                    if (count.isBlind)
                      ToneBadge(l10n.countBlind, BosColors.purple),
                  ],
                ),
                const SizedBox(height: BosSpace.s4),
                Text(
                  '${count.warehouseName ?? '—'} · '
                  '${countScopeLabel(l10n, count.scope)}',
                  style: theme.textTheme.bodyLarge,
                ),
                Text(
                  l10n.countProgress(count.countedLineCount, count.lineCount),
                  style: theme.textTheme.bodyMedium?.copyWith(
                    color: BosColors.textMuted,
                  ),
                ),
              ],
            ),
          ),
        ),
      ),
    );
  }
}
