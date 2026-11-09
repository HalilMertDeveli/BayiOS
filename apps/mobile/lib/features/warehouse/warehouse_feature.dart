import 'package:bayios_mobile/app/feature.dart';
import 'package:bayios_mobile/core/ui/tokens.dart';
import 'package:bayios_mobile/features/stock/stock_errors.dart';
import 'package:bayios_mobile/features/warehouse/count_list_screen.dart';
import 'package:bayios_mobile/features/warehouse/count_screen.dart';
import 'package:bayios_mobile/features/warehouse/relocation_screen.dart';
import 'package:bayios_mobile/features/warehouse/transfer_list_screen.dart';
import 'package:bayios_mobile/features/warehouse/transfer_receive_screen.dart';
import 'package:bayios_mobile/features/warehouse/warehouse_api.dart';
import 'package:bayios_mobile/features/warehouse/warehouse_errors.dart';
import 'package:bayios_mobile/l10n/app_localizations.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

/// Depo (#168) — handheld warehouse operations: receive incoming transfers
/// (`inventory.transfer.receive`), enter stock counts
/// (`inventory.count.perform`) and move stock between shelves
/// ([WarehousePermissions.relocate]). Each item shows with its own
/// permission; the tab shows when any of them is granted.
final warehouseFeature = AppFeature(
  id: 'warehouse',
  path: '/depo',
  label: (l10n) => l10n.warehouseNav,
  icon: Icons.warehouse_outlined,
  selectedIcon: Icons.warehouse,
  isVisible: (access) => access.canAny(WarehousePermissions.all),
  builder: (context, state) => const WarehouseHubScreen(),
  routes: [
    GoRoute(
      path: 'transferler',
      builder: (context, state) => const TransferListScreen(),
      routes: [
        GoRoute(
          path: ':id',
          builder: (context, state) =>
              TransferReceiveScreen(transferId: state.pathParameters['id']!),
        ),
      ],
    ),
    GoRoute(
      path: 'sayimlar',
      builder: (context, state) => const CountListScreen(),
      routes: [
        GoRoute(
          path: ':id',
          builder: (context, state) =>
              CountScreen(countId: state.pathParameters['id']!),
        ),
      ],
    ),
    GoRoute(
      path: 'raf-tasima',
      builder: (context, state) => const RelocationScreen(),
    ),
  ],
  endpoints: WarehouseEndpoints.all,
  schemas: WarehouseEndpoints.schemas,
);

/// /depo — the three handheld operations the permissions allow.
class WarehouseHubScreen extends ConsumerWidget {
  const WarehouseHubScreen({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final l10n = AppLocalizations.of(context);
    final access = ref.watch(stockAccessProvider);
    final items = [
      if (access.canSome(WarehousePermissions.receiveTransfer))
        (
          'warehouse-transfers',
          Icons.local_shipping_outlined,
          l10n.transferListTitle,
          l10n.warehouseHubTransfersHint,
          '/depo/transferler',
        ),
      if (access.canSome(WarehousePermissions.performCount))
        (
          'warehouse-counts',
          Icons.fact_check_outlined,
          l10n.countListTitle,
          l10n.warehouseHubCountsHint,
          '/depo/sayimlar',
        ),
      if (access.canSome(WarehousePermissions.relocate))
        (
          'warehouse-relocation',
          Icons.swap_vert,
          l10n.relocationTitle,
          l10n.warehouseHubRelocationHint,
          '/depo/raf-tasima',
        ),
    ];
    return Scaffold(
      appBar: AppBar(title: Text(l10n.warehouseTitle)),
      body: ListView(
        padding: const EdgeInsets.all(BosSpace.s16),
        children: [
          for (final (key, icon, title, hint, path) in items) ...[
            Card(
              key: Key(key),
              clipBehavior: Clip.antiAlias,
              child: InkWell(
                onTap: () => context.go(path),
                child: ConstrainedBox(
                  constraints: const BoxConstraints(minHeight: 88),
                  child: Padding(
                    padding: const EdgeInsets.all(BosSpace.s16),
                    child: Row(
                      children: [
                        Icon(icon, size: 36, color: BosColors.brandPrimary),
                        const SizedBox(width: BosSpace.s16),
                        Expanded(
                          child: Column(
                            crossAxisAlignment: CrossAxisAlignment.start,
                            children: [
                              Text(
                                title,
                                style: Theme.of(context).textTheme.titleMedium,
                              ),
                              Text(
                                hint,
                                style: const TextStyle(
                                  color: BosColors.textMuted,
                                ),
                              ),
                            ],
                          ),
                        ),
                        const Icon(Icons.chevron_right),
                      ],
                    ),
                  ),
                ),
              ),
            ),
            const SizedBox(height: BosSpace.s8),
          ],
        ],
      ),
    );
  }
}
