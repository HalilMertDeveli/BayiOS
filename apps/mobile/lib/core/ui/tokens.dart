import 'package:flutter/material.dart';

/// BayiOS design tokens for Flutter, mirrored from the web
/// (apps/web/src/app/globals.css, `--bos-*`, light theme) and
/// docs/design/design-system-plan.md §2. Keep the values in sync with the web
/// file; the dark theme is P2 (design plan D-03).
abstract final class BosColors {
  // color.brand
  static const brandPrimary = Color(0xFF0F4C75);
  static const brandPrimaryHover = Color(0xFF0B3A5B);
  static const brandPrimarySubtle = Color(0xFFE6F0F7);
  static const brandOnPrimary = Color(0xFFFFFFFF);

  // color.text
  static const textPrimary = Color(0xFF111827);
  static const textSecondary = Color(0xFF374151);
  static const textMuted = Color(0xFF5B6472);
  static const textInverse = Color(0xFFFFFFFF);
  static const textLink = Color(0xFF0F5C8F);

  // color.surface
  static const surfacePage = Color(0xFFF6F7F9);
  static const surfaceCard = Color(0xFFFFFFFF);
  static const surfaceRaised = Color(0xFFFFFFFF);
  static const surfaceSunken = Color(0xFFEEF0F3);

  // color.border
  static const borderDefault = Color(0xFFDFE3E8);
  static const borderStrong = Color(0xFF7B8494);
  static const borderFocus = Color(0xFF2563EB);

  // color.{tone}.{fg,bg,border}
  static const neutral = BosTone(
    fg: Color(0xFF374151),
    bg: Color(0xFFEEF0F3),
    border: Color(0xFFC5CBD3),
  );
  static const info = BosTone(
    fg: Color(0xFF1D4ED8),
    bg: Color(0xFFE8EFFD),
    border: Color(0xFF9DB7F2),
  );
  static const success = BosTone(
    fg: Color(0xFF166534),
    bg: Color(0xFFE8F5EC),
    border: Color(0xFF86C79A),
  );
  static const warning = BosTone(
    fg: Color(0xFF8A4B00),
    bg: Color(0xFFFFF4E0),
    border: Color(0xFFF0B45A),
  );
  static const danger = BosTone(
    fg: Color(0xFFB42318),
    bg: Color(0xFFFDECEA),
    border: Color(0xFFF1A29B),
  );
  static const purple = BosTone(
    fg: Color(0xFF6B21A8),
    bg: Color(0xFFF3E8FD),
    border: Color(0xFFC9A3EE),
  );
  static const teal = BosTone(
    fg: Color(0xFF0F6B6B),
    bg: Color(0xFFE2F4F3),
    border: Color(0xFF8CCFCB),
  );
  static const brand = BosTone(
    fg: Color(0xFF0F4C75),
    bg: Color(0xFFE6F0F7),
    border: Color(0xFF93B8D3),
  );
  static const dangerSolid = Color(0xFFB42318);
}

/// A feedback / status colour family (`color.{tone}.fg|bg|border`).
@immutable
class BosTone {
  const BosTone({required this.fg, required this.bg, required this.border});

  final Color fg;
  final Color bg;
  final Color border;
}

/// `space.*` (4 px base).
abstract final class BosSpace {
  static const double s2 = 2;
  static const double s4 = 4;
  static const double s8 = 8;
  static const double s12 = 12;
  static const double s16 = 16;
  static const double s20 = 20;
  static const double s24 = 24;
  static const double s32 = 32;
  static const double s40 = 40;
  static const double s48 = 48;
  static const double s64 = 64;
}

/// `radius.*`.
abstract final class BosRadius {
  static const double sm = 4;
  static const double md = 6;
  static const double lg = 8;
  static const double xl = 12;
}

/// `size.control.*` — mobile minimum is 48 dp (design plan §1.6, WCAG 2.5.8).
abstract final class BosSize {
  /// Minimum touch target on mobile.
  static const double minTouchTarget = 48;

  /// Primary actions in the thumb zone (warehouse use, gloves).
  static const double primaryAction = 56;
}

/// `motion.*`.
abstract final class BosMotion {
  static const fast = Duration(milliseconds: 120);
  static const base = Duration(milliseconds: 200);
  static const slow = Duration(milliseconds: 300);
}

/// `type.*` with the mobile scale (web values + 2 px, design plan §2.3).
abstract final class BosType {
  static const display = TextStyle(
    fontSize: 32,
    height: 40 / 32,
    fontWeight: FontWeight.w600,
  );
  static const h1 = TextStyle(
    fontSize: 26,
    height: 34 / 26,
    fontWeight: FontWeight.w600,
  );
  static const h2 = TextStyle(
    fontSize: 22,
    height: 30 / 22,
    fontWeight: FontWeight.w600,
  );
  static const h3 = TextStyle(
    fontSize: 18,
    height: 26 / 18,
    fontWeight: FontWeight.w600,
  );
  static const body = TextStyle(
    fontSize: 16,
    height: 24 / 16,
    fontWeight: FontWeight.w400,
  );
  static const bodyLg = TextStyle(
    fontSize: 18,
    height: 26 / 18,
    fontWeight: FontWeight.w400,
  );
  static const caption = TextStyle(
    fontSize: 14,
    height: 18 / 14,
    fontWeight: FontWeight.w400,
  );
  static const mono = TextStyle(
    fontSize: 15,
    height: 22 / 15,
    fontWeight: FontWeight.w500,
    fontFamily: 'monospace',
  );

  /// Big quantity on the picking screen (`32/40, 700`).
  static const quantity = TextStyle(
    fontSize: 32,
    height: 40 / 32,
    fontWeight: FontWeight.w700,
    fontFeatures: [FontFeature.tabularFigures()],
  );
}
