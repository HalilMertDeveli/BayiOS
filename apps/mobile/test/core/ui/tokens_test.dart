import 'package:bayios_mobile/core/theme/app_colors.dart';
import 'package:bayios_mobile/core/theme/app_palette.dart';
import 'package:bayios_mobile/core/theme/app_spacing.dart';
import 'package:bayios_mobile/core/theme/app_theme.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

/// WCAG 2.x contrast ratio between two opaque colours.
double contrast(Color a, Color b) {
  final la = a.computeLuminance();
  final lb = b.computeLuminance();
  final hi = la > lb ? la : lb;
  final lo = la > lb ? lb : la;
  return (hi + 0.05) / (lo + 0.05);
}

void main() {
  const themes = {'light': AppColors.light, 'dark': AppColors.dark};

  for (final MapEntry(key: name, value: c) in themes.entries) {
    group('$name theme meets WCAG 2.2 AA (#222)', () {
      final surfaces = {
        'page': c.surfacePage,
        'card': c.surfaceCard,
        'sunken': c.surfaceSunken,
      };

      test('text on the primary button and its hover', () {
        expect(
          contrast(c.brandOnPrimary, c.brandPrimary),
          greaterThanOrEqualTo(4.5),
        );
        expect(
          contrast(c.brandOnPrimary, c.brandPrimaryHover),
          greaterThanOrEqualTo(4.5),
        );
      });

      test('dark text on the accent green', () {
        expect(
          contrast(c.brandOnAccent, c.brandAccent),
          greaterThanOrEqualTo(4.5),
        );
      });

      test('text on the brand panel', () {
        expect(contrast(c.brandOnPanel, c.brandPanel), greaterThanOrEqualTo(7));
        expect(
          contrast(c.brandOnPanelMuted, c.brandPanel),
          greaterThanOrEqualTo(4.5),
        );
      });

      test('text on solid feedback blocks', () {
        expect(contrast(c.onSolid, c.dangerSolid), greaterThanOrEqualTo(4.5));
        expect(contrast(c.onSolid, c.successSolid), greaterThanOrEqualTo(4.5));
      });

      for (final MapEntry(key: surface, value: bg) in surfaces.entries) {
        test('text, link and focus on $surface', () {
          expect(contrast(c.textPrimary, bg), greaterThanOrEqualTo(7));
          expect(contrast(c.textSecondary, bg), greaterThanOrEqualTo(4.5));
          expect(contrast(c.textMuted, bg), greaterThanOrEqualTo(4.5));
          expect(contrast(c.textLink, bg), greaterThanOrEqualTo(4.5));
          expect(contrast(c.brand.fg, bg), greaterThanOrEqualTo(4.5));
          // Non-text UI (focus ring, primary control edge): 3:1.
          expect(contrast(c.borderFocus, bg), greaterThanOrEqualTo(3));
          expect(contrast(c.brandPrimary, bg), greaterThanOrEqualTo(3));
          expect(contrast(c.borderStrong, bg), greaterThanOrEqualTo(3));
        });
      }

      test('every status tone keeps 4.5:1 on its own background', () {
        for (final tone in Tone.values) {
          final t = c.tone(tone);
          expect(
            contrast(t.fg, t.bg),
            greaterThanOrEqualTo(4.5),
            reason: '$name ${tone.name}',
          );
        }
      });
    });
  }

  test('light theme = the web --bos-* light values (kit palette)', () {
    const c = AppColors.light;
    expect(c.brandPrimary, AppPalette.primary700);
    expect(c.brandPrimary, const Color(0xFF1F825E));
    expect(c.textPrimary, AppPalette.gray900);
    expect(c.surfacePage, AppPalette.gray0);
    expect(c.surfaceCard, AppPalette.white);
    expect(c.borderDefault, AppPalette.gray200);
  });

  test('dark theme: near-black page, white-5 % cards', () {
    expect(AppColors.dark.surfacePage, const Color(0xFF0C110F));
    expect(AppColors.dark.surfaceCard, const Color(0xFF181D1B));
  });

  for (final (name, theme) in [
    ('light', buildAppTheme()),
    ('dark', buildAppDarkTheme()),
  ]) {
    test('$name Material scheme and extension come from AppColors', () {
      final c = theme.extension<AppColors>()!;
      final scheme = theme.colorScheme;
      expect(scheme.primary, c.brandPrimary);
      expect(scheme.onPrimary, c.brandOnPrimary);
      expect(scheme.error, c.danger.fg);
      expect(scheme.error, isNot(scheme.primary));
      expect(theme.scaffoldBackgroundColor, c.surfacePage);
      expect(theme.cardTheme.color, c.surfaceCard);
    });

    test('$name text theme uses Inter Tight and the kit scale', () {
      final previous = AppFonts.bundled;
      AppFonts.bundled = true;
      addTearDown(() => AppFonts.bundled = previous);
      final text =
          (name == 'light' ? buildAppTheme() : buildAppDarkTheme()).textTheme;
      expect(text.bodyMedium!.fontFamily, startsWith('InterTight'));
      expect(text.headlineMedium!.fontSize, AppTypeScale.headingH5Size);
      expect(text.headlineMedium!.fontWeight, FontWeight.w700);
      expect(text.labelLarge!.fontSize, AppTypeScale.buttonSize);
      expect(text.bodyMedium!.fontSize, AppTypeScale.mediumRegularSize);
    });
  }

  test('controls keep the 48 dp minimum touch target', () {
    expect(AppSize.minTouchTarget, 48);
    final theme = buildAppTheme();
    final filled = theme.filledButtonTheme.style!.minimumSize!.resolve({})!;
    expect(filled.height, greaterThanOrEqualTo(AppSize.minTouchTarget));
    final outlined = theme.outlinedButtonTheme.style!.minimumSize!.resolve({})!;
    expect(outlined.height, greaterThanOrEqualTo(AppSize.minTouchTarget));
  });

  test('spacing and radius come from the kit scales', () {
    expect(
      [
        AppSpacing.xxs,
        AppSpacing.xs,
        AppSpacing.sm,
        AppSpacing.md,
        AppSpacing.lg,
        AppSpacing.xl,
      ],
      [4, 8, 12, 16, 24, 32],
    );
    expect([AppRadius.sm, AppRadius.md, AppRadius.lg], [8, 12, 16]);
  });
}
