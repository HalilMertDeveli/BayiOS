import 'package:bayios_mobile/core/ui/tokens.dart';
import 'package:bayios_mobile/l10n/app_localizations.dart';
import 'package:flutter/material.dart';

/// The BayiOS mark: a petrol tile with the warehouse glyph and the copper
/// underline (copper is decorative only, docs/design/brand-colors.md).
class BrandMark extends StatelessWidget {
  const BrandMark({super.key, this.size = 64, this.inverted = false});

  final double size;

  /// White tile on a petrol background (sign-in header).
  final bool inverted;

  @override
  Widget build(BuildContext context) {
    final tile = inverted ? BosColors.surfaceCard : BosColors.brandPrimary;
    final glyph = inverted ? BosColors.brandPrimary : BosColors.brandOnPrimary;
    return ExcludeSemantics(
      child: SizedBox.square(
        dimension: size,
        child: DecoratedBox(
          decoration: BoxDecoration(
            color: tile,
            borderRadius: BorderRadius.circular(size * 0.26),
          ),
          child: Stack(
            alignment: Alignment.center,
            children: [
              Icon(Icons.warehouse_rounded, size: size * 0.56, color: glyph),
              Positioned(
                left: size * 0.26,
                right: size * 0.26,
                bottom: size * 0.12,
                child: Container(
                  height: size * 0.07,
                  decoration: BoxDecoration(
                    color: BosColors.brandAccent,
                    borderRadius: BorderRadius.circular(size),
                  ),
                ),
              ),
            ],
          ),
        ),
      ),
    );
  }
}

/// Icon on a tinted rounded square (quick actions, empty states, info rows).
class IconBadge extends StatelessWidget {
  const IconBadge({
    required this.icon,
    super.key,
    this.tone = BosColors.brand,
    this.size = 48,
  });

  final IconData icon;
  final BosTone tone;
  final double size;

  @override
  Widget build(BuildContext context) {
    return SizedBox.square(
      dimension: size,
      child: DecoratedBox(
        decoration: BoxDecoration(
          color: tone.bg,
          borderRadius: BorderRadius.circular(size * 0.3),
        ),
        child: Icon(icon, size: size * 0.54, color: tone.fg),
      ),
    );
  }
}

/// Section title of a scrolling page ("Görevlerim", "Hızlı erişim").
class SectionHeader extends StatelessWidget {
  const SectionHeader(this.title, {super.key, this.trailing});

  final String title;
  final Widget? trailing;

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.only(top: BosSpace.s8, bottom: BosSpace.s8),
      child: Row(
        children: [
          Expanded(
            child: Semantics(
              header: true,
              child: Text(
                title,
                style: BosType.h3.copyWith(
                  color: BosColors.textPrimary,
                  fontWeight: FontWeight.w700,
                ),
              ),
            ),
          ),
          ?trailing,
        ],
      ),
    );
  }
}

/// The one empty / informational state of the app: a tinted icon, a title
/// and what to do next; optionally an action.
class EmptyState extends StatelessWidget {
  const EmptyState({
    required this.icon,
    required this.title,
    super.key,
    this.body,
    this.tone = BosColors.brand,
    this.action,
    this.compact = false,
  });

  final IconData icon;
  final String title;
  final String? body;
  final BosTone tone;
  final Widget? action;

  /// Less vertical padding (inside a form, under a search field).
  final bool compact;

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: EdgeInsets.symmetric(
        horizontal: BosSpace.s24,
        vertical: compact ? BosSpace.s24 : BosSpace.s48,
      ),
      child: Column(
        mainAxisSize: MainAxisSize.min,
        children: [
          ExcludeSemantics(
            child: IconBadge(icon: icon, tone: tone, size: compact ? 56 : 72),
          ),
          const SizedBox(height: BosSpace.s16),
          Semantics(
            liveRegion: true,
            child: Text(
              title,
              textAlign: TextAlign.center,
              style: BosType.h3.copyWith(color: BosColors.textPrimary),
            ),
          ),
          if (body != null) ...[
            const SizedBox(height: BosSpace.s8),
            Text(
              body!,
              textAlign: TextAlign.center,
              style: BosType.body.copyWith(color: BosColors.textSecondary),
            ),
          ],
          if (action != null) ...[
            const SizedBox(height: BosSpace.s24),
            action!,
          ],
        ],
      ),
    );
  }
}

/// One step of a scan flow ("Raf okut", "Ürün okut", "Adet").
@immutable
class ScanStep {
  const ScanStep(this.label, {this.detail, this.skipped = false});

  final String label;

  /// The value once known (shelf code, SKU) or what is expected.
  final String? detail;

  /// Not needed for this line (no shelf tracking).
  final bool skipped;
}

/// The big "1 Raf okut → 2 Ürün okut → 3 Adet" indicator of the scan
/// screens: the current step is filled petrol, finished steps show a green
/// check, the rest are outlined. Screen readers hear "Adım 2 / 3: Ürün okut".
class ScanStepIndicator extends StatelessWidget {
  const ScanStepIndicator({
    required this.steps,
    required this.current,
    super.key,
  });

  final List<ScanStep> steps;

  /// Index of the current step; `steps.length` = all done.
  final int current;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final active = current.clamp(0, steps.length - 1);
    final semantics = current >= steps.length
        ? l10n.scanStepAllDone
        : l10n.scanStepSemantics(active + 1, steps.length, steps[active].label);
    final children = <Widget>[];
    for (var i = 0; i < steps.length; i++) {
      if (i > 0) {
        children.add(
          Expanded(
            child: Padding(
              padding: const EdgeInsets.only(top: 19),
              child: Container(
                height: 3,
                margin: const EdgeInsets.symmetric(horizontal: BosSpace.s4),
                decoration: BoxDecoration(
                  color: i <= current
                      ? BosColors.success.fg
                      : BosColors.borderDefault,
                  borderRadius: BorderRadius.circular(2),
                ),
              ),
            ),
          ),
        );
      }
      children.add(_StepDot(index: i, step: steps[i], current: current));
    }
    return Semantics(
      container: true,
      liveRegion: true,
      label: semantics,
      child: ExcludeSemantics(
        child: Row(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: children,
        ),
      ),
    );
  }
}

class _StepDot extends StatelessWidget {
  const _StepDot({
    required this.index,
    required this.step,
    required this.current,
  });

  final int index;
  final ScanStep step;
  final int current;

  @override
  Widget build(BuildContext context) {
    final done = index < current || step.skipped;
    final isCurrent = index == current && !step.skipped;
    final Color fill;
    final Color fg;
    final Color border;
    if (isCurrent) {
      fill = BosColors.brandPrimary;
      fg = BosColors.brandOnPrimary;
      border = BosColors.brandPrimary;
    } else if (done) {
      fill = BosColors.success.fg;
      fg = BosColors.textInverse;
      border = BosColors.success.fg;
    } else {
      fill = BosColors.surfaceCard;
      fg = BosColors.textMuted;
      border = BosColors.borderStrong;
    }
    return SizedBox(
      width: 84,
      child: Column(
        children: [
          AnimatedContainer(
            duration: BosMotion.base,
            width: 40,
            height: 40,
            decoration: BoxDecoration(
              color: fill,
              shape: BoxShape.circle,
              border: Border.all(color: border, width: 2),
              boxShadow: isCurrent
                  ? const [BoxShadow(color: BosPetrol.p200, spreadRadius: 4)]
                  : null,
            ),
            alignment: Alignment.center,
            child: done && !isCurrent
                ? Icon(
                    step.skipped ? Icons.remove : Icons.check,
                    color: fg,
                    size: 24,
                  )
                : Text(
                    '${index + 1}',
                    style: BosType.h3.copyWith(
                      color: fg,
                      fontWeight: FontWeight.w700,
                    ),
                  ),
          ),
          const SizedBox(height: BosSpace.s4),
          Text(
            step.label,
            textAlign: TextAlign.center,
            maxLines: 2,
            style: BosType.caption.copyWith(
              color: isCurrent
                  ? BosColors.brandPrimary
                  : BosColors.textSecondary,
              fontWeight: isCurrent ? FontWeight.w700 : FontWeight.w500,
            ),
          ),
          if (step.detail case final detail? when !step.skipped)
            Text(
              detail,
              textAlign: TextAlign.center,
              maxLines: 1,
              overflow: TextOverflow.ellipsis,
              style: BosType.caption.copyWith(
                fontFamily: 'monospace',
                color: BosColors.textMuted,
              ),
            ),
        ],
      ),
    );
  }
}

/// Tone of a scan result box.
enum FeedbackTone { success, info, warning, error }

/// The result of a scan / write, impossible to miss: success and error are
/// solid colour blocks with a big icon (readable at arm's length, in bright
/// light and with sunglasses); info and warning stay tinted. Each new
/// message pops in once (scale) so a repeated result is still noticed.
class FeedbackBanner extends StatelessWidget {
  const FeedbackBanner({
    required this.tone,
    required this.title,
    super.key,
    this.text,
    this.trailing,
  });

  final FeedbackTone tone;
  final String title;
  final String? text;
  final Widget? trailing;

  @override
  Widget build(BuildContext context) {
    final (BosTone palette, IconData icon) = switch (tone) {
      FeedbackTone.success => (BosColors.success, Icons.check_circle),
      FeedbackTone.info => (BosColors.info, Icons.info_outline),
      FeedbackTone.warning => (BosColors.warning, Icons.warning_amber_rounded),
      FeedbackTone.error => (BosColors.danger, Icons.cancel),
    };
    final solid = tone == FeedbackTone.success || tone == FeedbackTone.error;
    final bg = solid
        ? (tone == FeedbackTone.error ? BosColors.dangerSolid : palette.fg)
        : palette.bg;
    final fg = solid ? BosColors.textInverse : palette.fg;
    return TweenAnimationBuilder<double>(
      // A new message (tone + title) restarts the pop.
      key: ValueKey('$tone|$title|$text'),
      tween: Tween(begin: solid ? 0.94 : 1, end: 1),
      duration: BosMotion.slow,
      curve: Curves.easeOutBack,
      builder: (context, scale, child) =>
          Transform.scale(scale: scale, child: child),
      child: Semantics(
        liveRegion: true,
        container: true,
        child: DecoratedBox(
          decoration: BoxDecoration(
            color: bg,
            border: Border.all(color: solid ? bg : palette.border),
            borderRadius: BorderRadius.circular(BosRadius.xl),
          ),
          child: Padding(
            padding: const EdgeInsets.all(BosSpace.s12),
            child: Row(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Icon(icon, color: fg, size: solid ? 36 : 28),
                const SizedBox(width: BosSpace.s12),
                Expanded(
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Text(
                        title,
                        style: (solid ? BosType.h3 : BosType.bodyLg).copyWith(
                          color: fg,
                          fontWeight: FontWeight.w700,
                        ),
                      ),
                      if (text case final text?)
                        Text(text, style: BosType.body.copyWith(color: fg)),
                    ],
                  ),
                ),
                ?trailing,
              ],
            ),
          ),
        ),
      ),
    );
  }
}
