import 'package:bayios_mobile/core/ui/components.dart';
import 'package:bayios_mobile/core/ui/tokens.dart';
import 'package:bayios_mobile/features/stock/quantity.dart';
import 'package:bayios_mobile/features/stock/stock_errors.dart';
import 'package:bayios_mobile/features/stock/stock_widgets.dart';
import 'package:bayios_mobile/features/warehouse/document_scan_controller.dart';
import 'package:bayios_mobile/features/warehouse/warehouse_errors.dart';
import 'package:bayios_mobile/l10n/app_localizations.dart';
import 'package:flutter/material.dart';

/// Deep link without the permission: say so instead of a broken form.
class WarehouseForbidden extends StatelessWidget {
  const WarehouseForbidden({
    required this.title,
    required this.message,
    super.key,
  });

  final String title;
  final String message;

  @override
  Widget build(BuildContext context) => Scaffold(
    appBar: AppBar(title: Text(title)),
    body: StockMessagePanel(
      key: const Key('warehouse-no-permission'),
      icon: Icons.lock_outline,
      message: message,
    ),
  );
}

/// Big − / quantity / + row (one-handed: the buttons are 56 dp).
class QuantityStepper extends StatefulWidget {
  const QuantityStepper({
    required this.keyPrefix,
    required this.text,
    required this.label,
    required this.onChanged,
    required this.onStep,
    super.key,
    this.error,
    this.enabled = true,
  });

  final String keyPrefix;
  final String text;
  final String label;
  final String? error;
  final bool enabled;
  final ValueChanged<String> onChanged;
  final ValueChanged<int> onStep;

  @override
  State<QuantityStepper> createState() => _QuantityStepperState();
}

class _QuantityStepperState extends State<QuantityStepper> {
  late final _controller = TextEditingController(text: widget.text);

  @override
  void didUpdateWidget(QuantityStepper oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (widget.text != _controller.text) {
      _controller.value = TextEditingValue(
        text: widget.text,
        selection: TextSelection.collapsed(offset: widget.text.length),
      );
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
    final style = IconButton.styleFrom(
      minimumSize: const Size.square(BosSize.primaryAction),
    );
    return Row(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        IconButton.outlined(
          key: Key('${widget.keyPrefix}-minus'),
          tooltip: l10n.receivingQuantityMinus,
          onPressed: widget.enabled ? () => widget.onStep(-1) : null,
          style: style,
          icon: const Icon(Icons.remove),
        ),
        const SizedBox(width: BosSpace.s8),
        Expanded(
          child: TextField(
            key: Key('${widget.keyPrefix}-quantity'),
            controller: _controller,
            enabled: widget.enabled,
            textAlign: TextAlign.center,
            style: BosType.h2,
            keyboardType: const TextInputType.numberWithOptions(decimal: true),
            decoration: InputDecoration(
              labelText: widget.label,
              errorText: widget.error,
              errorMaxLines: 3,
            ),
            onChanged: widget.onChanged,
          ),
        ),
        const SizedBox(width: BosSpace.s8),
        IconButton.outlined(
          key: Key('${widget.keyPrefix}-plus'),
          tooltip: l10n.receivingQuantityPlus,
          onPressed: widget.enabled ? () => widget.onStep(1) : null,
          style: style,
          icon: const Icon(Icons.add),
        ),
      ],
    );
  }
}

/// The scanned shelf (sticky across product scans) with a way to clear it,
/// and the field to scan / type another one.
class ShelfScanField extends StatelessWidget {
  const ShelfScanField({
    required this.keyPrefix,
    required this.shelf,
    required this.onScanned,
    required this.onClear,
    super.key,
    this.enabled = true,
    this.required = false,
    this.missing = false,
    this.hint,
  });

  final String keyPrefix;
  final String? shelf;
  final ValueChanged<String> onScanned;
  final VoidCallback onClear;
  final bool enabled;

  /// The warehouse tracks shelves: a product scan needs one.
  final bool required;

  /// A product was scanned without a shelf.
  final bool missing;
  final String? hint;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final theme = Theme.of(context);
    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        if (shelf != null)
          Row(
            children: [
              const Icon(Icons.shelves, color: BosColors.brandPrimary),
              const SizedBox(width: BosSpace.s8),
              Expanded(
                child: Semantics(
                  liveRegion: true,
                  child: Text(
                    l10n.docShelfCurrent(shelf!),
                    key: Key('$keyPrefix-shelf-current'),
                    style: theme.textTheme.titleMedium,
                  ),
                ),
              ),
              IconButton(
                key: Key('$keyPrefix-shelf-clear'),
                tooltip: l10n.docShelfClear,
                onPressed: enabled ? onClear : null,
                icon: const Icon(Icons.close),
              ),
            ],
          ),
        CodeEntryField(
          fieldKey: Key('$keyPrefix-shelf-field'),
          scanKey: Key('$keyPrefix-shelf-scan'),
          label: shelf == null ? l10n.docShelfLabel : l10n.docShelfChange,
          hint: hint ?? (required ? l10n.docShelfRequiredHint : null),
          scanTitle: l10n.docShelfScanTitle,
          enabled: enabled,
          onSubmit: onScanned,
        ),
        if (missing) ...[
          const SizedBox(height: BosSpace.s8),
          StockNotice(
            key: Key('$keyPrefix-shelf-missing'),
            tone: BosColors.danger,
            icon: Icons.error_outline,
            child: Text(l10n.docShelfMissing),
          ),
        ],
      ],
    );
  }
}

/// Result of the last scan (accepted / refused / unknown / stale), with
/// "Tekrar gönder" for an unknown outcome.
class EntryFeedbackView extends StatelessWidget {
  const EntryFeedbackView({
    required this.keyPrefix,
    required this.feedback,
    required this.acceptedLine,
    required this.onResend,
    required this.onDiscard,
    super.key,
    this.sending = false,
    this.forbidden,
  });

  final String keyPrefix;
  final EntryFeedback feedback;

  /// "FLT-001 +2 · toplam 5" for one changed line.
  final String Function(ChangedLine line) acceptedLine;
  final VoidCallback onResend;
  final VoidCallback onDiscard;
  final bool sending;
  final String? forbidden;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    String message(Object error) =>
        documentErrorMessage(l10n, error, forbidden: forbidden);
    switch (feedback) {
      // Accepted / refused scans are solid colour blocks (FeedbackBanner):
      // readable at arm's length, together with the haptic + sound alert.
      case EntryAccepted(:final changes, :final replayed):
        return FeedbackBanner(
          key: Key('$keyPrefix-accepted'),
          tone: FeedbackTone.success,
          title: replayed ? l10n.docEntryReplayed : l10n.docEntryAccepted,
          text: changes.isEmpty
              ? null
              : [for (final line in changes) acceptedLine(line)].join('\n'),
        );
      case EntryRejected(:final error, :final code):
        return KeyedSubtree(
          key: Key('$keyPrefix-rejected'),
          child: FeedbackBanner(
            key: Key('$keyPrefix-rejected-message'),
            tone: FeedbackTone.error,
            title: code == null
                ? l10n.docEntryRejected
                : l10n.docEntryRejectedCode(code),
            text: message(error),
          ),
        );
      case EntryStale(:final error):
        return StockNotice(
          key: Key('$keyPrefix-stale'),
          tone: BosColors.warning,
          icon: Icons.sync_problem,
          child: Text(message(error)),
        );
      case EntryPending(:final error):
        return StockNotice(
          key: Key('$keyPrefix-pending'),
          tone: BosColors.danger,
          icon: Icons.cloud_off_outlined,
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Text(
                l10n.docEntryUnknown,
                style: const TextStyle(fontWeight: FontWeight.w700),
              ),
              Text(message(error)),
              Text(l10n.docEntryPendingHint),
              const SizedBox(height: BosSpace.s8),
              Wrap(
                spacing: BosSpace.s8,
                runSpacing: BosSpace.s8,
                children: [
                  FilledButton.icon(
                    key: Key('$keyPrefix-resend'),
                    onPressed: sending ? null : onResend,
                    icon: const Icon(Icons.refresh),
                    label: Text(l10n.resend),
                    style: FilledButton.styleFrom(
                      backgroundColor: BosColors.dangerSolid,
                      minimumSize: const Size(0, BosSize.primaryAction),
                    ),
                  ),
                  TextButton(
                    key: Key('$keyPrefix-discard'),
                    onPressed: sending ? null : onDiscard,
                    style: TextButton.styleFrom(
                      foregroundColor: BosColors.danger.fg,
                    ),
                    child: Text(l10n.docEntryDiscard),
                  ),
                ],
              ),
            ],
          ),
        );
    }
  }
}

/// "1 Raf okut → 2 Adet → 3 Ürün okut" of the document scan screens
/// (transfer receipt, count), in the order of the form below it: the shelf
/// is sticky, the quantity defaults to 1 and the product scan writes.
class DocumentScanSteps extends StatelessWidget {
  const DocumentScanSteps({
    required this.shelf,
    required this.shelfRequired,
    required this.quantityText,
    super.key,
  });

  final String? shelf;

  /// The warehouse tracks shelves (or is not known yet).
  final bool shelfRequired;
  final String quantityText;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final needsShelf = shelfRequired && shelf == null;
    return Card(
      child: Padding(
        padding: const EdgeInsets.symmetric(
          horizontal: BosSpace.s8,
          vertical: BosSpace.s12,
        ),
        child: ScanStepIndicator(
          current: needsShelf ? 0 : 2,
          steps: [
            ScanStep(
              l10n.scanStepShelf,
              detail: shelf,
              skipped: !shelfRequired && shelf == null,
            ),
            ScanStep(
              l10n.scanStepQuantity,
              detail: quantityText.isEmpty ? null : '×$quantityText',
            ),
            ScanStep(l10n.scanStepProduct),
          ],
        ),
      ),
    );
  }
}

/// Quantity error text of the scan screens.
String? quantityErrorText(AppLocalizations l10n, QuantityError? error) =>
    error == null ? null : quantityErrorMessage(l10n, error);

/// "+2 birim" / "−1 birim".
String signedQuantity(num value) =>
    value > 0 ? '+${formatQuantity(value)}' : formatQuantity(value);
