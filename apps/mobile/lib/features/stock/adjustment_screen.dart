import 'package:bayios_mobile/core/api/api_exception.dart';
import 'package:bayios_mobile/core/api/idempotency.dart';
import 'package:bayios_mobile/core/ui/tokens.dart';
import 'package:bayios_mobile/features/stock/quantity.dart';
import 'package:bayios_mobile/features/stock/stock_api.dart';
import 'package:bayios_mobile/features/stock/stock_errors.dart';
import 'package:bayios_mobile/features/stock/stock_models.dart';
import 'package:bayios_mobile/features/stock/stock_widgets.dart';
import 'package:bayios_mobile/l10n/app_localizations.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

const adjustmentReasonMaxLength = 500;

/// The balance row an adjustment starts from (passed as route `extra`).
@immutable
class AdjustmentTarget {
  const AdjustmentTarget({
    required this.variant,
    required this.balance,
    this.warehouse,
    this.location,
  });

  final VariantRef variant;
  final StockBalance balance;
  final Warehouse? warehouse;
  final StorageLocation? location;
}

/// Stok düzeltme (INV-07, `inventory.adjust`): one ledger movement of a
/// chosen type with a mandatory reason. Opened from a balance row of the
/// stock query; pops `true` after a successful save.
class AdjustmentScreen extends ConsumerStatefulWidget {
  const AdjustmentScreen({required this.target, super.key});

  final AdjustmentTarget? target;

  @override
  ConsumerState<AdjustmentScreen> createState() => _AdjustmentScreenState();
}

class _AdjustmentScreenState extends ConsumerState<AdjustmentScreen> {
  final _quantity = TextEditingController();
  final _reason = TextEditingController();
  AdjustmentType? _type;
  String? _typeError;
  String? _quantityError;
  String? _reasonError;
  Object? _error;
  bool _pending = false;
  bool _sending = false;
  IdempotentAction<StockMovement>? _action;

  @override
  void dispose() {
    _quantity.dispose();
    _reason.dispose();
    super.dispose();
  }

  bool _validate(AppLocalizations l10n) {
    final q = parseQuantity(_quantity.text);
    final reason = _reason.text.trim();
    setState(() {
      _typeError = _type == null ? l10n.stockAdjustTypeRequired : null;
      _quantityError = q.error == null
          ? null
          : quantityErrorMessage(l10n, q.error!);
      _reasonError = reason.isEmpty ? l10n.stockReasonRequired : null;
    });
    return _typeError == null && _quantityError == null && _reasonError == null;
  }

  Map<String, Object?> _body(AdjustmentTarget target, {required bool confirm}) {
    return {
      'variantId': target.variant.id,
      'warehouseId': target.balance.warehouseId,
      'locationId': target.balance.locationId,
      'type': _type!.wire,
      'quantity': parseQuantity(_quantity.text).value,
      'reason': _reason.text.trim(),
      'confirmNegativeStock': confirm,
    };
  }

  /// Save; after an unknown outcome the same action (same key) is resent.
  Future<void> _save(AdjustmentTarget target, {bool confirm = false}) async {
    final l10n = AppLocalizations.of(context);
    if (_sending) return;
    if (!_pending && !_validate(l10n)) return;
    if (!_pending || _action == null || confirm) {
      final body = _body(target, confirm: confirm);
      final api = ref.read(stockApiProvider);
      _action = IdempotentAction((key) => api.createAdjustment(body, key));
    }
    final action = _action!;
    setState(() {
      _sending = true;
      _error = null;
    });
    try {
      final movement = await withBusyRetry(
        action.run,
        wait: ref.read(stockDelayProvider),
      );
      if (!mounted) return;
      _action = null;
      ScaffoldMessenger.of(context).showSnackBar(
        SnackBar(
          content: Text(
            l10n.stockAdjustSaved(formatQuantity(movement.balanceAfter)),
          ),
        ),
      );
      Navigator.of(context).pop(true);
    } on Object catch (error) {
      if (!mounted) return;
      final pending = error is! ApiException || error.isTransient;
      if (!pending) _action = null;
      setState(() {
        _sending = false;
        _pending = pending;
        _error = error;
      });
      if (!pending && needsNegativeStockConfirmation(error)) {
        await _confirmNegative(target);
      }
    }
  }

  Future<void> _confirmNegative(AdjustmentTarget target) async {
    final l10n = AppLocalizations.of(context);
    final confirmed = await showDialog<bool>(
      context: context,
      builder: (context) => AlertDialog(
        title: Text(l10n.stockAdjustNegativeTitle),
        content: Text(l10n.stockAdjustNegativeBody),
        actions: [
          TextButton(
            key: const Key('adjust-negative-cancel'),
            onPressed: () => Navigator.of(context).pop(false),
            child: Text(l10n.stockAdjustNegativeCancel),
          ),
          FilledButton(
            key: const Key('adjust-negative-confirm'),
            onPressed: () => Navigator.of(context).pop(true),
            child: Text(l10n.stockAdjustNegativeConfirm),
          ),
        ],
      ),
    );
    if ((confirmed ?? false) && mounted) await _save(target, confirm: true);
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final access = ref.watch(stockAccessProvider);
    final target = widget.target;
    final Widget body;
    if (!access.can(StockPermissions.adjust)) {
      body = StockMessagePanel(
        key: const Key('adjust-no-permission'),
        icon: Icons.lock_outline,
        message: l10n.stockAdjustForbidden,
      );
    } else if (target == null) {
      body = StockMessagePanel(
        key: const Key('adjust-no-target'),
        icon: Icons.manage_search,
        message: l10n.stockAdjustNoTarget,
      );
    } else {
      body = _form(context, l10n, target);
    }
    return Scaffold(
      appBar: AppBar(title: Text(l10n.stockAdjustTitle)),
      body: body,
    );
  }

  Widget _form(
    BuildContext context,
    AppLocalizations l10n,
    AdjustmentTarget target,
  ) {
    final locked = _sending || _pending;
    final error = _error;
    final b = target.balance;
    return ListView(
      padding: const EdgeInsets.all(BosSpace.s16),
      children: [
        Card(
          child: Padding(
            padding: const EdgeInsets.all(BosSpace.s16),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                VariantHeader(variant: target.variant),
                const SizedBox(height: BosSpace.s8),
                Text(placeLabel(target.warehouse, target.location)),
                Text(
                  l10n.stockAdjustCurrent(
                    formatQuantity(b.onHand),
                    formatQuantity(b.reserved),
                    formatQuantity(b.available),
                  ),
                ),
              ],
            ),
          ),
        ),
        const SizedBox(height: BosSpace.s16),
        if (error != null && !needsNegativeStockConfirmation(error)) ...[
          StockNotice(
            key: const Key('adjust-error'),
            tone: BosColors.danger,
            icon: Icons.error_outline,
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(
                  stockErrorMessage(
                    l10n,
                    error,
                    forbidden: l10n.stockAdjustForbidden,
                  ),
                ),
                if (_pending) ...[
                  const SizedBox(height: BosSpace.s4),
                  Text(l10n.stockAdjustPendingHint),
                ],
              ],
            ),
          ),
          const SizedBox(height: BosSpace.s16),
        ],
        Text(l10n.stockAdjustTypeLabel, style: BosType.h3),
        RadioGroup<AdjustmentType>(
          groupValue: _type,
          onChanged: (value) {
            if (locked) return;
            setState(() {
              _type = value;
              _typeError = null;
            });
          },
          child: Column(
            children: [
              for (final type in AdjustmentType.values)
                RadioListTile<AdjustmentType>(
                  key: Key('adjust-type-${type.wire}'),
                  value: type,
                  enabled: !locked,
                  title: Text(_typeLabel(l10n, type)),
                ),
            ],
          ),
        ),
        if (_typeError != null)
          Text(_typeError!, style: TextStyle(color: BosColors.danger.fg)),
        const SizedBox(height: BosSpace.s12),
        TextField(
          key: const Key('adjust-quantity'),
          controller: _quantity,
          enabled: !locked,
          keyboardType: const TextInputType.numberWithOptions(decimal: true),
          decoration: InputDecoration(
            labelText: l10n.stockAdjustQuantityLabel,
            errorText: _quantityError,
          ),
        ),
        const SizedBox(height: BosSpace.s12),
        TextField(
          key: const Key('adjust-reason'),
          controller: _reason,
          enabled: !locked,
          maxLength: adjustmentReasonMaxLength,
          minLines: 2,
          maxLines: 4,
          decoration: InputDecoration(
            labelText: l10n.stockAdjustReasonLabel,
            helperText: l10n.stockAdjustReasonHint,
            errorText: _reasonError,
          ),
        ),
        const SizedBox(height: BosSpace.s16),
        FilledButton.icon(
          key: const Key('adjust-submit'),
          onPressed: _sending ? null : () => _save(target),
          icon: Icon(_pending ? Icons.refresh : Icons.save_outlined),
          label: Text(
            _sending
                ? l10n.stockAdjustSaving
                : _pending
                ? l10n.resend
                : l10n.stockAdjustSubmit,
          ),
          style: FilledButton.styleFrom(
            minimumSize: const Size.fromHeight(BosSize.primaryAction),
          ),
        ),
        if (_pending)
          TextButton(
            key: const Key('adjust-discard'),
            onPressed: () => setState(() {
              _pending = false;
              _action = null;
              _error = null;
            }),
            child: Text(l10n.receivingDiscard),
          ),
      ],
    );
  }

  static String _typeLabel(AppLocalizations l10n, AdjustmentType type) =>
      switch (type) {
        AdjustmentType.adjustmentIn => l10n.stockAdjustTypeIn,
        AdjustmentType.adjustmentOut => l10n.stockAdjustTypeOut,
        AdjustmentType.damageOut => l10n.stockAdjustTypeDamage,
        AdjustmentType.returnIn => l10n.stockAdjustTypeReturn,
      };
}
