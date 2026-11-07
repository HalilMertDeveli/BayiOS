import 'package:bayios_mobile/core/ui/tokens.dart';
import 'package:bayios_mobile/core/ui/widgets.dart';
import 'package:bayios_mobile/features/picking/pick_controller.dart';
import 'package:bayios_mobile/features/picking/pick_session.dart';
import 'package:bayios_mobile/features/picking/picking_models.dart';
import 'package:bayios_mobile/l10n/app_localizations.dart';
import 'package:flutter/material.dart';

Future<void> showShortageDialog(
  BuildContext context, {
  required PickingTaskLine line,
  required PickController controller,
}) => showDialog<void>(
  context: context,
  barrierDismissible: false,
  builder: (_) => ShortageDialog(line: line, controller: controller),
);

/// Shortage for one line: reason (required) and quantity (defaults to what is
/// still open, never more). "Eksik bildir" again after a lost answer resends
/// the same action (same key) — see [PickController.reportShortage].
class ShortageDialog extends StatefulWidget {
  const ShortageDialog({
    required this.line,
    required this.controller,
    super.key,
  });

  final PickingTaskLine line;
  final PickController controller;

  @override
  State<ShortageDialog> createState() => _ShortageDialogState();
}

class _ShortageDialogState extends State<ShortageDialog> {
  String? _reason;
  late final _quantity = TextEditingController(
    text: formatQuantity(widget.line.quantityRemaining),
  );
  String? _reasonError;
  String? _quantityError;
  String? _error;
  bool _busy = false;

  @override
  void dispose() {
    _quantity.dispose();
    super.dispose();
  }

  Future<void> _submit() async {
    final l10n = AppLocalizations.of(context);
    final open = widget.line.quantityRemaining;
    final parsed = parseQuantity(_quantity.text, max: open);
    setState(() {
      _reasonError = _reason == null
          ? l10n.pickingShortageReasonRequired
          : null;
      _quantityError = switch (parsed) {
        QuantityInvalid(:final error) => quantityErrorText(l10n, error, open),
        QuantityOk() => null,
      };
      _error = null;
    });
    if (_reason == null || parsed is! QuantityOk) return;
    setState(() => _busy = true);
    final message = await widget.controller.reportShortage(
      widget.line,
      _reason!,
      parsed.value,
    );
    if (!mounted) return;
    if (message == null) {
      Navigator.of(context).pop();
      return;
    }
    setState(() {
      _busy = false;
      _error = message;
    });
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final line = widget.line;
    final reasons = {
      ShortageReason.notFound: l10n.pickingShortageReasonNotFound,
      ShortageReason.damaged: l10n.pickingShortageReasonDamaged,
      ShortageReason.insufficient: l10n.pickingShortageReasonInsufficient,
    };
    return AlertDialog(
      title: Text(l10n.pickingShortageTitle(line.sku)),
      scrollable: true,
      content: Column(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Text(l10n.pickingShortageBody),
          const SizedBox(height: BosSpace.s16),
          Text(
            l10n.pickingShortageReasonLabel,
            style: BosType.body.copyWith(fontWeight: FontWeight.w600),
          ),
          RadioGroup<String>(
            groupValue: _reason,
            onChanged: (value) => setState(() {
              _reason = value;
              _reasonError = null;
            }),
            child: Column(
              children: [
                for (final entry in reasons.entries)
                  RadioListTile<String>(
                    key: Key('shortage-reason-${entry.key}'),
                    value: entry.key,
                    title: Text(entry.value),
                    contentPadding: EdgeInsets.zero,
                  ),
              ],
            ),
          ),
          if (_reasonError != null)
            Text(
              _reasonError!,
              key: const Key('shortage-reason-error'),
              style: BosType.caption.copyWith(color: BosColors.danger.fg),
            ),
          const SizedBox(height: BosSpace.s12),
          TextField(
            key: const Key('shortage-quantity'),
            controller: _quantity,
            keyboardType: const TextInputType.numberWithOptions(decimal: true),
            decoration: InputDecoration(
              labelText: l10n.pickingShortageQuantityLabel(line.unit),
              helperText: l10n.pickingShortageOpenHint(
                formatQuantity(line.quantityRemaining),
              ),
              errorText: _quantityError,
              errorMaxLines: 2,
            ),
          ),
          if (_error != null) ...[
            const SizedBox(height: BosSpace.s12),
            ErrorMessageBox(_error!),
          ],
        ],
      ),
      actions: [
        TextButton(
          key: const Key('shortage-cancel'),
          onPressed: _busy ? null : () => Navigator.of(context).pop(),
          child: Text(l10n.pickingCancel),
        ),
        FilledButton(
          key: const Key('shortage-submit'),
          onPressed: _busy ? null : _submit,
          style: FilledButton.styleFrom(
            backgroundColor: BosColors.dangerSolid,
            foregroundColor: BosColors.textInverse,
          ),
          child: Text(_busy ? l10n.loading : l10n.pickingShortageSubmit),
        ),
      ],
    );
  }
}
