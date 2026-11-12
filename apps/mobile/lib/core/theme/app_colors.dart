import 'package:bayios_mobile/core/theme/app_palette.dart';
import 'package:flutter/material.dart';

/// A feedback / status colour family (`--bos-{tone}-fg|bg|border`).
@immutable
class AppTone {
  const AppTone({required this.fg, required this.bg, required this.border});

  final Color fg;
  final Color bg;
  final Color border;

  AppTone lerp(AppTone other, double t) => AppTone(
    fg: Color.lerp(fg, other.fg, t)!,
    bg: Color.lerp(bg, other.bg, t)!,
    border: Color.lerp(border, other.border, t)!,
  );

  @override
  bool operator ==(Object other) =>
      other is AppTone &&
      other.fg == fg &&
      other.bg == bg &&
      other.border == border;

  @override
  int get hashCode => Object.hash(fg, bg, border);
}

/// Which status family a badge, icon tile or banner uses. Resolved against
/// the active theme with `context.colors.tone(Tone.x)`, so light and dark
/// stay in one place.
enum Tone { neutral, info, success, warning, danger, purple, teal, brand, ai }

/// BayiOS semantic colours (light + dark), the Flutter mirror of the web
/// `--bos-*` tokens (apps/web/src/app/tokens.css). Screens read colours only
/// from here (`context.colors`) or from the Material `ColorScheme` built from
/// it — never from [AppPalette] or `Color(…)` literals.
///
/// apps/web/src/app/tokens.test.ts parses the [light] block below (one
/// `name: value,` per line) and checks the brand / text / surface / border
/// values against the web light theme. WCAG checks: test/core/ui/tokens_test.dart.
@immutable
class AppColors extends ThemeExtension<AppColors> {
  const AppColors({
    required this.brandPrimary,
    required this.brandPrimaryHover,
    required this.brandPrimarySubtle,
    required this.brandOnPrimary,
    required this.brandAccent,
    required this.brandAccentHover,
    required this.brandOnAccent,
    required this.brandPanel,
    required this.brandOnPanel,
    required this.brandOnPanelMuted,
    required this.textPrimary,
    required this.textSecondary,
    required this.textMuted,
    required this.textInverse,
    required this.textLink,
    required this.surfacePage,
    required this.surfaceCard,
    required this.surfaceRaised,
    required this.surfaceSunken,
    required this.overlay,
    required this.borderDefault,
    required this.borderStrong,
    required this.borderFocus,
    required this.dangerSolid,
    required this.dangerSolidHover,
    required this.successSolid,
    required this.onSolid,
    required this.chart1,
    required this.chart2,
    required this.chart3,
    required this.chart4,
    required this.chartTrack,
    required this.neutral,
    required this.info,
    required this.success,
    required this.warning,
    required this.danger,
    required this.purple,
    required this.teal,
    required this.brand,
    required this.ai,
  });

  // brand
  final Color brandPrimary;
  final Color brandPrimaryHover;
  final Color brandPrimarySubtle;
  final Color brandOnPrimary;

  /// Lighter kit green (primary-600): decorative fills (icon halos,
  /// gradients, charts) and dark text on it; never body text on white.
  final Color brandAccent;
  final Color brandAccentHover;
  final Color brandOnAccent;

  /// Deep green panel (hero cards) and the text on it.
  final Color brandPanel;
  final Color brandOnPanel;
  final Color brandOnPanelMuted;

  // text
  final Color textPrimary;
  final Color textSecondary;
  final Color textMuted;
  final Color textInverse;
  final Color textLink;

  // surface
  final Color surfacePage;
  final Color surfaceCard;
  final Color surfaceRaised;
  final Color surfaceSunken;

  /// Scrim behind sheets and dialogs (translucent).
  final Color overlay;

  // border
  final Color borderDefault;
  final Color borderStrong;
  final Color borderFocus;

  // solid danger (destructive buttons, error feedback block)
  final Color dangerSolid;
  final Color dangerSolidHover;

  /// Solid success block (scan accepted) and the text on solid blocks.
  final Color successSolid;
  final Color onSolid;

  // charts / progress
  final Color chart1;
  final Color chart2;
  final Color chart3;
  final Color chart4;
  final Color chartTrack;

  // status tones
  final AppTone neutral;
  final AppTone info;
  final AppTone success;
  final AppTone warning;
  final AppTone danger;
  final AppTone purple;
  final AppTone teal;
  final AppTone brand;
  final AppTone ai;

  static const light = AppColors(
    brandPrimary: AppPalette.primary700,
    brandPrimaryHover: AppPalette.primary800,
    brandPrimarySubtle: AppPalette.primary50,
    brandOnPrimary: AppPalette.white,
    brandAccent: AppPalette.primary600,
    brandAccentHover: AppPalette.primary500,
    brandOnAccent: AppPalette.gray900,
    brandPanel: AppPalette.primary900,
    brandOnPanel: AppPalette.white,
    brandOnPanelMuted: AppPalette.primary100,
    textPrimary: AppPalette.gray900,
    textSecondary: AppPalette.gray800,
    textMuted: AppPalette.gray700,
    textInverse: AppPalette.white,
    textLink: AppPalette.primary800,
    surfacePage: AppPalette.gray0,
    surfaceCard: AppPalette.white,
    surfaceRaised: AppPalette.white,
    surfaceSunken: AppPalette.gray50,
    overlay: Color(0x800C110F),
    borderDefault: AppPalette.gray200,
    borderStrong: AppPalette.gray600,
    borderFocus: AppPalette.primary700,
    dangerSolid: AppPalette.red700,
    dangerSolidHover: AppPalette.red800,
    successSolid: AppPalette.green800,
    onSolid: AppPalette.white,
    chart1: AppPalette.primary600,
    chart2: AppPalette.blue600,
    chart3: AppPalette.purple600,
    chart4: AppPalette.yellow600,
    chartTrack: AppPalette.primary100,
    neutral: AppTone(
      fg: AppPalette.gray800,
      bg: AppPalette.gray50,
      border: AppPalette.gray300,
    ),
    info: AppTone(
      fg: AppPalette.blue700,
      bg: AppPalette.blue50,
      border: AppPalette.blue300,
    ),
    success: AppTone(
      fg: AppPalette.green800,
      bg: AppPalette.green50,
      border: AppPalette.green300,
    ),
    warning: AppTone(
      fg: AppPalette.yellow900,
      bg: AppPalette.yellow50,
      border: AppPalette.yellow300,
    ),
    danger: AppTone(
      fg: AppPalette.red700,
      bg: AppPalette.red50,
      border: AppPalette.red300,
    ),
    purple: AppTone(
      fg: AppPalette.purple700,
      bg: AppPalette.purple50,
      border: AppPalette.purple300,
    ),
    teal: AppTone(
      fg: AppPalette.primary800,
      bg: AppPalette.primary50,
      border: AppPalette.primary300,
    ),
    brand: AppTone(
      fg: AppPalette.primary800,
      bg: AppPalette.primary100,
      border: AppPalette.primary400,
    ),
    ai: AppTone(
      fg: AppPalette.purple800,
      bg: AppPalette.purple100,
      border: AppPalette.purple400,
    ),
  );

  static const dark = AppColors(
    brandPrimary: AppPalette.primary500,
    brandPrimaryHover: AppPalette.primary400,
    brandPrimarySubtle: AppPalette.primary900,
    brandOnPrimary: AppPalette.gray900,
    brandAccent: AppPalette.primary500,
    brandAccentHover: AppPalette.primary400,
    brandOnAccent: AppPalette.gray900,
    brandPanel: AppPalette.primary900,
    brandOnPanel: AppPalette.white,
    brandOnPanelMuted: AppPalette.primary100,
    textPrimary: AppPalette.gray0,
    textSecondary: AppPalette.gray300,
    textMuted: AppPalette.gray500,
    textInverse: AppPalette.gray900,
    textLink: AppPalette.primary500,
    surfacePage: AppPalette.gray900,
    surfaceCard: Color(0xFF181D1B),
    surfaceRaised: AppPalette.gray800,
    surfaceSunken: Color(0xFF121715),
    overlay: Color(0x99000000),
    borderDefault: Color(0xFF2F3833),
    borderStrong: AppPalette.gray600,
    borderFocus: AppPalette.primary500,
    dangerSolid: AppPalette.red700,
    dangerSolidHover: AppPalette.red800,
    successSolid: AppPalette.green800,
    onSolid: AppPalette.white,
    chart1: AppPalette.primary500,
    chart2: AppPalette.blue500,
    chart3: AppPalette.purple500,
    chart4: AppPalette.yellow500,
    chartTrack: AppPalette.primary900,
    neutral: AppTone(
      fg: AppPalette.gray400,
      bg: AppPalette.gray800,
      border: AppPalette.gray700,
    ),
    info: AppTone(
      fg: AppPalette.blue300,
      bg: AppPalette.blue900,
      border: AppPalette.blue700,
    ),
    success: AppTone(
      fg: AppPalette.green300,
      bg: AppPalette.green900,
      border: AppPalette.green700,
    ),
    warning: AppTone(
      fg: AppPalette.yellow300,
      bg: AppPalette.yellow900,
      border: AppPalette.yellow700,
    ),
    danger: AppTone(
      fg: AppPalette.red300,
      bg: AppPalette.red900,
      border: AppPalette.red700,
    ),
    purple: AppTone(
      fg: AppPalette.purple300,
      bg: AppPalette.purple900,
      border: AppPalette.purple700,
    ),
    teal: AppTone(
      fg: AppPalette.primary300,
      bg: AppPalette.primary900,
      border: AppPalette.primary800,
    ),
    brand: AppTone(
      fg: AppPalette.primary400,
      bg: AppPalette.primary900,
      border: AppPalette.primary800,
    ),
    ai: AppTone(
      fg: AppPalette.purple300,
      bg: AppPalette.purple900,
      border: AppPalette.purple700,
    ),
  );

  /// The colour family of [tone].
  AppTone tone(Tone tone) => switch (tone) {
    Tone.neutral => neutral,
    Tone.info => info,
    Tone.success => success,
    Tone.warning => warning,
    Tone.danger => danger,
    Tone.purple => purple,
    Tone.teal => teal,
    Tone.brand => brand,
    Tone.ai => ai,
  };

  @override
  AppColors copyWith({
    Color? brandPrimary,
    Color? brandPrimaryHover,
    Color? brandPrimarySubtle,
    Color? brandOnPrimary,
    Color? brandAccent,
    Color? brandAccentHover,
    Color? brandOnAccent,
    Color? brandPanel,
    Color? brandOnPanel,
    Color? brandOnPanelMuted,
    Color? textPrimary,
    Color? textSecondary,
    Color? textMuted,
    Color? textInverse,
    Color? textLink,
    Color? surfacePage,
    Color? surfaceCard,
    Color? surfaceRaised,
    Color? surfaceSunken,
    Color? overlay,
    Color? borderDefault,
    Color? borderStrong,
    Color? borderFocus,
    Color? dangerSolid,
    Color? dangerSolidHover,
    Color? successSolid,
    Color? onSolid,
    Color? chart1,
    Color? chart2,
    Color? chart3,
    Color? chart4,
    Color? chartTrack,
    AppTone? neutral,
    AppTone? info,
    AppTone? success,
    AppTone? warning,
    AppTone? danger,
    AppTone? purple,
    AppTone? teal,
    AppTone? brand,
    AppTone? ai,
  }) => AppColors(
    brandPrimary: brandPrimary ?? this.brandPrimary,
    brandPrimaryHover: brandPrimaryHover ?? this.brandPrimaryHover,
    brandPrimarySubtle: brandPrimarySubtle ?? this.brandPrimarySubtle,
    brandOnPrimary: brandOnPrimary ?? this.brandOnPrimary,
    brandAccent: brandAccent ?? this.brandAccent,
    brandAccentHover: brandAccentHover ?? this.brandAccentHover,
    brandOnAccent: brandOnAccent ?? this.brandOnAccent,
    brandPanel: brandPanel ?? this.brandPanel,
    brandOnPanel: brandOnPanel ?? this.brandOnPanel,
    brandOnPanelMuted: brandOnPanelMuted ?? this.brandOnPanelMuted,
    textPrimary: textPrimary ?? this.textPrimary,
    textSecondary: textSecondary ?? this.textSecondary,
    textMuted: textMuted ?? this.textMuted,
    textInverse: textInverse ?? this.textInverse,
    textLink: textLink ?? this.textLink,
    surfacePage: surfacePage ?? this.surfacePage,
    surfaceCard: surfaceCard ?? this.surfaceCard,
    surfaceRaised: surfaceRaised ?? this.surfaceRaised,
    surfaceSunken: surfaceSunken ?? this.surfaceSunken,
    overlay: overlay ?? this.overlay,
    borderDefault: borderDefault ?? this.borderDefault,
    borderStrong: borderStrong ?? this.borderStrong,
    borderFocus: borderFocus ?? this.borderFocus,
    dangerSolid: dangerSolid ?? this.dangerSolid,
    dangerSolidHover: dangerSolidHover ?? this.dangerSolidHover,
    successSolid: successSolid ?? this.successSolid,
    onSolid: onSolid ?? this.onSolid,
    chart1: chart1 ?? this.chart1,
    chart2: chart2 ?? this.chart2,
    chart3: chart3 ?? this.chart3,
    chart4: chart4 ?? this.chart4,
    chartTrack: chartTrack ?? this.chartTrack,
    neutral: neutral ?? this.neutral,
    info: info ?? this.info,
    success: success ?? this.success,
    warning: warning ?? this.warning,
    danger: danger ?? this.danger,
    purple: purple ?? this.purple,
    teal: teal ?? this.teal,
    brand: brand ?? this.brand,
    ai: ai ?? this.ai,
  );

  @override
  AppColors lerp(covariant AppColors? other, double t) {
    if (other == null) return this;
    Color c(Color a, Color b) => Color.lerp(a, b, t)!;
    return AppColors(
      brandPrimary: c(brandPrimary, other.brandPrimary),
      brandPrimaryHover: c(brandPrimaryHover, other.brandPrimaryHover),
      brandPrimarySubtle: c(brandPrimarySubtle, other.brandPrimarySubtle),
      brandOnPrimary: c(brandOnPrimary, other.brandOnPrimary),
      brandAccent: c(brandAccent, other.brandAccent),
      brandAccentHover: c(brandAccentHover, other.brandAccentHover),
      brandOnAccent: c(brandOnAccent, other.brandOnAccent),
      brandPanel: c(brandPanel, other.brandPanel),
      brandOnPanel: c(brandOnPanel, other.brandOnPanel),
      brandOnPanelMuted: c(brandOnPanelMuted, other.brandOnPanelMuted),
      textPrimary: c(textPrimary, other.textPrimary),
      textSecondary: c(textSecondary, other.textSecondary),
      textMuted: c(textMuted, other.textMuted),
      textInverse: c(textInverse, other.textInverse),
      textLink: c(textLink, other.textLink),
      surfacePage: c(surfacePage, other.surfacePage),
      surfaceCard: c(surfaceCard, other.surfaceCard),
      surfaceRaised: c(surfaceRaised, other.surfaceRaised),
      surfaceSunken: c(surfaceSunken, other.surfaceSunken),
      overlay: c(overlay, other.overlay),
      borderDefault: c(borderDefault, other.borderDefault),
      borderStrong: c(borderStrong, other.borderStrong),
      borderFocus: c(borderFocus, other.borderFocus),
      dangerSolid: c(dangerSolid, other.dangerSolid),
      dangerSolidHover: c(dangerSolidHover, other.dangerSolidHover),
      successSolid: c(successSolid, other.successSolid),
      onSolid: c(onSolid, other.onSolid),
      chart1: c(chart1, other.chart1),
      chart2: c(chart2, other.chart2),
      chart3: c(chart3, other.chart3),
      chart4: c(chart4, other.chart4),
      chartTrack: c(chartTrack, other.chartTrack),
      neutral: neutral.lerp(other.neutral, t),
      info: info.lerp(other.info, t),
      success: success.lerp(other.success, t),
      warning: warning.lerp(other.warning, t),
      danger: danger.lerp(other.danger, t),
      purple: purple.lerp(other.purple, t),
      teal: teal.lerp(other.teal, t),
      brand: brand.lerp(other.brand, t),
      ai: ai.lerp(other.ai, t),
    );
  }
}

/// `context.colors.brandPrimary`, `context.text.titleMedium`.
extension AppThemeContext on BuildContext {
  /// The BayiOS semantic colours of the active theme (light / dark).
  AppColors get colors =>
      Theme.of(this).extension<AppColors>() ?? AppColors.light;

  /// The Inter Tight text theme built from the kit type scale.
  TextTheme get text => Theme.of(this).textTheme;
}
