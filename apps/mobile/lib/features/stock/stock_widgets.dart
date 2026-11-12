import 'package:bayios_mobile/core/theme/app_colors.dart';
import 'package:bayios_mobile/core/theme/app_icon.dart';
import 'package:bayios_mobile/core/theme/app_spacing.dart';
import 'package:bayios_mobile/core/theme/app_theme.dart';
import 'package:bayios_mobile/core/ui/components.dart';
import 'package:bayios_mobile/core/ui/widgets.dart';
import 'package:bayios_mobile/features/stock/scanner.dart';
import 'package:bayios_mobile/features/stock/stock_models.dart';
import 'package:bayios_mobile/l10n/app_localizations.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

/// Text field for a barcode / SKU plus a big camera button. Enter (also
/// sent by keyboard-wedge scanners) or a camera read calls [onSubmit]; the
/// field is the manual fallback when the camera is unavailable.
class CodeEntryField extends ConsumerStatefulWidget {
  const CodeEntryField({
    required this.fieldKey,
    required this.scanKey,
    required this.label,
    required this.scanTitle,
    required this.onSubmit,
    super.key,
    this.enabled = true,
    this.busy = false,
    this.clearOnSubmit = true,
    this.initialValue,
    this.hint,
  });

  final Key fieldKey;
  final Key scanKey;
  final String label;
  final String scanTitle;
  final String? hint;
  final String? initialValue;
  final bool enabled;
  final bool busy;
  final bool clearOnSubmit;
  final ValueChanged<String> onSubmit;

  @override
  ConsumerState<CodeEntryField> createState() => _CodeEntryFieldState();
}

class _CodeEntryFieldState extends ConsumerState<CodeEntryField> {
  late final _controller = TextEditingController(text: widget.initialValue);
  final _focus = FocusNode();

  @override
  void didUpdateWidget(CodeEntryField oldWidget) {
    super.didUpdateWidget(oldWidget);
    final value = widget.initialValue;
    if (value != null && value != oldWidget.initialValue) {
      _controller.text = value;
    }
  }

  @override
  void dispose() {
    _controller.dispose();
    _focus.dispose();
    super.dispose();
  }

  void _submit(String value) {
    final code = value.trim();
    // While a lookup runs the field stays enabled (and focused, so a
    // keyboard-wedge scanner's next read is not lost); submits wait.
    if (code.isEmpty || widget.busy || !widget.enabled) return;
    if (widget.clearOnSubmit) {
      _controller.clear();
    } else {
      _controller.text = code;
    }
    widget.onSubmit(code);
  }

  Future<void> _scan() async {
    final code = await ref
        .read(barcodeScannerProvider)
        .scan(context, title: widget.scanTitle);
    if (!mounted) return;
    if (code == null || code.trim().isEmpty) {
      // Cancelled or "Elle gir": continue in the text field.
      _focus.requestFocus();
      return;
    }
    _submit(code);
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final enabled = widget.enabled;
    final c = context.colors;
    // Compact scan control: the label sits on the field border (not above
    // it) so the scan screens keep shelf, quantity, product and the result
    // on one screen.
    return Row(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Expanded(
          child: TextField(
            key: widget.fieldKey,
            controller: _controller,
            focusNode: _focus,
            enabled: enabled,
            autocorrect: false,
            enableSuggestions: false,
            textInputAction: TextInputAction.search,
            decoration: InputDecoration(
              labelText: widget.label,
              floatingLabelBehavior: FloatingLabelBehavior.always,
              hintText: l10n.stockCodeHint,
              helperText: widget.hint,
              helperMaxLines: 3,
              prefixIcon: const AppIcon(AppIcons.search),
              suffixIcon: widget.busy
                  ? const Padding(
                      padding: EdgeInsets.all(AppSpacing.sm),
                      child: SizedBox.square(
                        dimension: AppSize.icon,
                        child: CircularProgressIndicator(strokeWidth: 2),
                      ),
                    )
                  : IconButton(
                      tooltip: l10n.stockCodeSubmit,
                      icon: const AppIcon(AppIcons.arrowRight2),
                      onPressed: enabled
                          ? () => _submit(_controller.text)
                          : null,
                    ),
            ),
            onSubmitted: _submit,
            // Keep focus after Enter (default unfocuses): the next scan of a
            // keyboard-wedge scanner types straight into the field.
            onEditingComplete: () {},
          ),
        ),
        const SizedBox(width: AppSpacing.xs),
        IconButton(
          key: widget.scanKey,
          tooltip: widget.scanTitle,
          onPressed: enabled && !widget.busy ? _scan : null,
          style: IconButton.styleFrom(
            fixedSize: const Size.square(AppSize.minTouchTarget),
            backgroundColor: c.brandPrimary,
            foregroundColor: c.brandOnPrimary,
            disabledBackgroundColor: c.surfaceSunken,
            disabledForegroundColor: c.textMuted,
            shape: const RoundedRectangleBorder(borderRadius: AppRadius.smAll),
          ),
          icon: const AppIcon(AppIcons.scan, size: AppSize.iconMd),
        ),
      ],
    );
  }
}

// Message with an icon and an optional "Tekrar dene" — the stock screens'
/// version of `ErrorView` (with inventory-specific wording).
class StockMessagePanel extends StatelessWidget {
  const StockMessagePanel({
    required this.message,
    required this.icon,
    super.key,
    this.tone,
    this.onRetry,
    this.retryLabel,
    this.error,
  });

  final String message;
  final AppIconData icon;
  final Tone? tone;
  final VoidCallback? onRetry;
  final String? retryLabel;

  /// The failure shown; a connection error re-runs [onRetry] by itself when
  /// the API is reachable again ([RefetchOnReconnect]).
  final Object? error;

  @override
  Widget build(BuildContext context) {
    return RefetchOnReconnect(
      error: error,
      onReconnect: onRetry,
      child: _content(context),
    );
  }

  Widget _content(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    return Padding(
      padding: const EdgeInsets.symmetric(vertical: AppSpacing.lg),
      child: Column(
        children: [
          ExcludeSemantics(
            child: IconBadge(
              icon: icon,
              tone: tone ?? Tone.brand,
              size: AppSize.primaryAction,
              round: true,
            ),
          ),
          const SizedBox(height: AppSpacing.md),
          Padding(
            padding: const EdgeInsets.symmetric(horizontal: AppSpacing.md),
            child: Semantics(
              liveRegion: true,
              child: Text(
                message,
                textAlign: TextAlign.center,
                style: context.text.titleMedium?.copyWith(
                  color: context.colors.textPrimary,
                ),
              ),
            ),
          ),
          if (onRetry != null) ...[
            const SizedBox(height: AppSpacing.md),
            FilledButton.icon(
              key: const Key('stock-retry'),
              onPressed: onRetry,
              icon: const AppIcon(AppIcons.refresh),
              label: Text(retryLabel ?? l10n.retry),
              style: FilledButton.styleFrom(
                minimumSize: const Size(200, AppSize.primaryAction),
              ),
            ),
          ],
        ],
      ),
    );
  }
}

/// Coloured notice box (success / warning / danger) for inline feedback.
class StockNotice extends StatelessWidget {
  const StockNotice({
    required this.tone,
    required this.icon,
    required this.child,
    super.key,
  });

  final Tone tone;
  final AppIconData icon;
  final Widget child;

  @override
  Widget build(BuildContext context) {
    final t = context.colors.tone(tone);
    return Semantics(
      liveRegion: true,
      container: true,
      child: DecoratedBox(
        decoration: BoxDecoration(
          color: t.bg,
          border: Border.all(color: t.border),
          borderRadius: AppRadius.mdAll,
        ),
        child: Padding(
          padding: const EdgeInsets.all(AppSpacing.sm),
          child: Row(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              AppIcon(icon, color: t.fg, size: AppSize.icon),
              const SizedBox(width: AppSpacing.xs),
              Expanded(
                child: DefaultTextStyle.merge(
                  style: context.text.bodySmall?.copyWith(color: t.fg),
                  child: child,
                ),
              ),
            ],
          ),
        ),
      ),
    );
  }
}

/// SKU (tabular figures) plus the product / variant name and barcode.
class VariantHeader extends StatelessWidget {
  const VariantHeader({required this.variant, super.key});

  final VariantRef variant;

  @override
  Widget build(BuildContext context) {
    final details = [
      if (variant.title != null) variant.title!,
      if (variant.barcode != null) variant.barcode!,
    ];
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Text(
          variant.sku,
          style: context.text.titleMedium?.tabular.copyWith(
            color: context.colors.textPrimary,
          ),
        ),
        if (details.isNotEmpty)
          Text(
            details.join(' · '),
            style: context.text.bodyMedium?.copyWith(
              color: context.colors.textMuted,
            ),
          ),
      ],
    );
  }
}

/// "Depo adı · Raf kodu".
String placeLabel(Warehouse? warehouse, StorageLocation? location) {
  final name = warehouse?.name ?? '—';
  return location == null ? name : '$name · ${location.code}';
}
