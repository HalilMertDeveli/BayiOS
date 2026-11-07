import 'package:bayios_mobile/app/feature.dart';
import 'package:bayios_mobile/features/stock/adjustment_screen.dart';
import 'package:bayios_mobile/features/stock/receiving_screen.dart';
import 'package:bayios_mobile/features/stock/stock_api.dart';
import 'package:bayios_mobile/features/stock/stock_errors.dart';
import 'package:bayios_mobile/features/stock/stock_query_screen.dart';
import 'package:flutter/material.dart';
import 'package:go_router/go_router.dart';

/// Stok (#43) — stock query (WHT-10), goods receipt `/stok/mal-kabul`
/// (AF-05 / WHT-11, `inventory.receive`) and stock adjustment
/// `/stok/duzeltme` (INV-07, `inventory.adjust`). Every role with
/// `inventory.read` sees the tab; the actions only with their permission.
final stockFeature = AppFeature(
  id: 'stock',
  path: '/stok',
  label: (l10n) => l10n.stockNav,
  icon: Icons.inventory_2_outlined,
  selectedIcon: Icons.inventory_2,
  isVisible: (access) => access.canSome(StockPermissions.read),
  builder: (context, state) =>
      StockQueryScreen(initialQuery: state.uri.queryParameters['q']),
  routes: [
    GoRoute(
      path: 'mal-kabul',
      builder: (context, state) => const ReceivingScreen(),
    ),
    GoRoute(
      path: 'duzeltme',
      builder: (context, state) => AdjustmentScreen(
        target: state.extra is AdjustmentTarget
            ? state.extra! as AdjustmentTarget
            : null,
      ),
    ),
  ],
  endpoints: StockEndpoints.all,
  schemas: StockEndpoints.schemas,
);
