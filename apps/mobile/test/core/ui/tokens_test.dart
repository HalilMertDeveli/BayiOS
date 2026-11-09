import 'package:bayios_mobile/core/ui/app_theme.dart';
import 'package:bayios_mobile/core/ui/tokens.dart';
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
  const surfaces = {
    'page': BosColors.surfacePage,
    'card': BosColors.surfaceCard,
    'sunken': BosColors.surfaceSunken,
  };

  group('brand palette (#61) meets WCAG 2.2 AA', () {
    test('white text on the petrol primary and its hover', () {
      expect(
        contrast(BosColors.brandOnPrimary, BosColors.brandPrimary),
        greaterThanOrEqualTo(4.5),
      );
      expect(
        contrast(BosColors.brandOnPrimary, BosColors.brandPrimaryHover),
        greaterThanOrEqualTo(4.5),
      );
    });

    test('dark text on the copper accent', () {
      expect(
        contrast(BosColors.brandOnAccent, BosColors.brandAccent),
        greaterThanOrEqualTo(4.5),
      );
    });

    for (final entry in surfaces.entries) {
      test('text, link and focus on ${entry.key}', () {
        expect(
          contrast(BosColors.textLink, entry.value),
          greaterThanOrEqualTo(4.5),
        );
        expect(
          contrast(BosColors.brand.fg, entry.value),
          greaterThanOrEqualTo(4.5),
        );
        expect(
          contrast(BosColors.borderFocus, entry.value),
          greaterThanOrEqualTo(3),
        );
        expect(
          contrast(BosColors.brandPrimary, entry.value),
          greaterThanOrEqualTo(3),
        );
      });
    }

    test('every status tone keeps 4.5:1 on its own background', () {
      for (final tone in [
        BosColors.neutral,
        BosColors.info,
        BosColors.success,
        BosColors.warning,
        BosColors.danger,
        BosColors.purple,
        BosColors.teal,
        BosColors.brand,
      ]) {
        expect(contrast(tone.fg, tone.bg), greaterThanOrEqualTo(4.5));
      }
    });

    test('the Material scheme uses the petrol primary', () {
      final scheme = buildAppTheme().colorScheme;
      expect(scheme.primary, BosColors.brandPrimary);
      expect(scheme.primary, const Color(0xFF0C5468));
      expect(scheme.onPrimary, BosColors.brandOnPrimary);
      expect(scheme.error, BosColors.danger.fg);
      expect(scheme.error, isNot(scheme.primary));
    });
  });
}
