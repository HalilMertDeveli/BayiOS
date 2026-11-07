import 'package:bayios_mobile/core/api/api_exception.dart';
import 'package:bayios_mobile/features/stock/stock_api.dart';
import 'package:bayios_mobile/features/stock/stock_models.dart';
import 'package:flutter/foundation.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

/// One balance row with its warehouse and bin resolved for display.
@immutable
class StockRow {
  const StockRow({required this.balance, this.warehouse, this.location});

  final StockBalance balance;
  final Warehouse? warehouse;
  final StorageLocation? location;
}

/// All balance rows of one variant.
@immutable
class StockGroup {
  const StockGroup({required this.variant, required this.rows});

  final VariantRef variant;
  final List<StockRow> rows;

  num get totalOnHand => rows.fold<num>(0, (s, r) => s + r.balance.onHand);
  num get totalAvailable =>
      rows.fold<num>(0, (s, r) => s + r.balance.available);
}

sealed class StockQueryState {
  const StockQueryState();
}

/// Nothing searched yet.
final class StockQueryIdle extends StockQueryState {
  const StockQueryIdle();
}

final class StockQueryLoading extends StockQueryState {
  const StockQueryLoading(this.term);

  final String term;
}

final class StockQueryResults extends StockQueryState {
  const StockQueryResults(this.term, this.groups, {required this.hasMore});

  final String term;
  final List<StockGroup> groups;

  /// More rows than one page: the screen asks for a narrower search.
  final bool hasMore;
}

/// No balance matched. [variant] is set when the code is a known product
/// that simply has no stock record yet.
final class StockQueryEmpty extends StockQueryState {
  const StockQueryEmpty(this.term, {this.variant});

  final String term;
  final VariantRef? variant;
}

final class StockQueryFailed extends StockQueryState {
  const StockQueryFailed(this.term, this.error);

  final String term;
  final Object error;
}

/// Stock query (WHT-10): balances by barcode / SKU / name, per warehouse and
/// bin, with on hand, reserved, available and the minimum-stock threshold.
class StockQueryController extends Notifier<StockQueryState> {
  int _generation = 0;
  List<Warehouse>? _warehouses;
  final _locations = <String, List<StorageLocation>>{};

  @override
  StockQueryState build() => const StockQueryIdle();

  StockApi get _api => ref.read(stockApiProvider);

  /// Runs a search; an empty term resets to idle. A newer search wins over
  /// a slower older one.
  Future<void> search(String raw) async {
    final term = raw.trim();
    final generation = ++_generation;
    if (term.isEmpty) {
      state = const StockQueryIdle();
      return;
    }
    state = StockQueryLoading(term);
    try {
      final next = await _load(term);
      if (generation == _generation && ref.mounted) state = next;
    } on Object catch (error) {
      if (generation == _generation && ref.mounted) {
        state = StockQueryFailed(term, error);
      }
    }
  }

  /// Same term again (pull-to-refresh, "Tekrar dene", after an adjustment).
  Future<void> refresh() async {
    final term = switch (state) {
      StockQueryLoading(:final term) ||
      StockQueryResults(:final term) ||
      StockQueryEmpty(:final term) ||
      StockQueryFailed(:final term) => term,
      StockQueryIdle() => '',
    };
    if (term.isNotEmpty) await search(term);
  }

  Future<StockQueryState> _load(String term) async {
    final page = await _api.balances(query: term);
    if (page.items.isEmpty) {
      return StockQueryEmpty(term, variant: await _knownVariant(term));
    }

    final ids = <String>[];
    for (final b in page.items) {
      if (!ids.contains(b.variantId)) ids.add(b.variantId);
    }
    final variants = {for (final v in await _api.variantsByIds(ids)) v.id: v};
    final warehouses = {for (final w in await _allWarehouses()) w.id: w};
    final binWarehouses = {
      for (final b in page.items)
        if (b.locationId != null) b.warehouseId,
    };
    final bins = <String, StorageLocation>{};
    for (final warehouseId in binWarehouses) {
      for (final l in await _binsOf(warehouseId)) {
        bins[l.id] = l;
      }
    }

    final groups = <StockGroup>[];
    for (final id in ids) {
      final rows = [
        for (final b in page.items)
          if (b.variantId == id)
            StockRow(
              balance: b,
              warehouse: warehouses[b.warehouseId],
              location: b.locationId == null ? null : bins[b.locationId],
            ),
      ]..sort(_byPlace);
      groups.add(
        StockGroup(
          // A variant the batch did not return (archived meanwhile) is still
          // shown with its id rather than hidden.
          variant: variants[id] ?? VariantRef(id: id, sku: id),
          rows: rows,
        ),
      );
    }
    return StockQueryResults(term, groups, hasMore: page.page.hasMore);
  }

  static int _byPlace(StockRow a, StockRow b) {
    final aDefault = a.warehouse?.isDefault ?? false;
    final bDefault = b.warehouse?.isDefault ?? false;
    if (aDefault != bDefault) return aDefault ? -1 : 1;
    final byWarehouse = (a.warehouse?.name ?? '').compareTo(
      b.warehouse?.name ?? '',
    );
    if (byWarehouse != 0) return byWarehouse;
    return (a.location?.code ?? '').compareTo(b.location?.code ?? '');
  }

  /// A scanned code that is a product without any balance row yet.
  Future<VariantRef?> _knownVariant(String term) async {
    try {
      return await _api.findVariantByCode(term);
    } on ApiException {
      return null;
    }
  }

  Future<List<Warehouse>> _allWarehouses() async =>
      _warehouses ??= await _api.warehouses();

  Future<List<StorageLocation>> _binsOf(String warehouseId) async {
    final cached = _locations[warehouseId];
    if (cached != null) return cached;
    final loaded = await _api.locations(warehouseId, includeInactive: true);
    _locations[warehouseId] = loaded;
    return loaded;
  }
}

final NotifierProvider<StockQueryController, StockQueryState>
stockQueryControllerProvider =
    NotifierProvider.autoDispose<StockQueryController, StockQueryState>(
      StockQueryController.new,
    );
