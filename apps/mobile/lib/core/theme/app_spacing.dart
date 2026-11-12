import 'package:bayios_mobile/core/theme/app_palette.dart';
import 'package:flutter/painting.dart';

/// Layout spacing (kit `Padding/*`, 4 px base). Screens use only these.
abstract final class AppSpacing {
  static const double xxs = AppSpacingScale.xxs; // 4
  static const double xs = AppSpacingScale.xs; // 8
  static const double sm = AppSpacingScale.sm; // 12
  static const double md = AppSpacingScale.md; // 16
  static const double lg = AppSpacingScale.xl; // 24
  static const double xl = AppSpacingScale.xxl; // 32

  /// Large empty-state / hero padding.
  static const double xxl = AppSpacingScale.x6xl; // 48
}

/// Corner radii (kit `Radius/*`).
abstract final class AppRadius {
  /// Chips, badges, small tiles.
  static const double xs = AppRadiusScale.xs; // 6

  /// Buttons, inputs, dropdowns, icon buttons.
  static const double sm = AppRadiusScale.s; // 8

  /// Inner panels, list rows, banners.
  static const double md = AppRadiusScale.l; // 12

  /// Cards, sheets, dialogs.
  static const double lg = AppRadiusScale.xl; // 16

  /// Pills and avatars.
  static const double full = AppRadiusScale.max;

  static const BorderRadius smAll = BorderRadius.all(Radius.circular(sm));
  static const BorderRadius mdAll = BorderRadius.all(Radius.circular(md));
  static const BorderRadius lgAll = BorderRadius.all(Radius.circular(lg));
}

/// The kit is flat: a hairline under controls and a soft card lift, no
/// heavy elevation. Shadow colour = kit gray-900 at low opacity.
abstract final class AppShadows {
  static const List<BoxShadow> hairline = [
    BoxShadow(
      color: Color(0x0A0C110F), // gray-900 @ 4 %
      offset: Offset(0, 1),
      blurRadius: 2,
    ),
  ];

  static const List<BoxShadow> card = [
    BoxShadow(
      color: Color(0x0F0C110F), // gray-900 @ 6 %
      offset: Offset(0, 4),
      blurRadius: 16,
    ),
  ];

  static const List<BoxShadow> none = [];
}

/// Control sizes. Mobile minimum touch target is 48 dp (WCAG 2.5.8,
/// Android); primary actions of the handheld flows are taller (gloves).
abstract final class AppSize {
  static const double minTouchTarget = AppSpacingScale.x6xl; // 48
  static const double primaryAction = AppSpacingScale.x7xl; // 56
  static const double iconSm = AppSpacingScale.md; // 16
  static const double icon = AppSpacingScale.lg; // 20
  static const double iconMd = AppSpacingScale.xl; // 24
  static const double iconLg = AppSpacingScale.xxl; // 32
  static const double topBarHeight = AppSpacingScale.x8xl; // 64
}

/// Animation durations.
abstract final class AppMotion {
  static const fast = Duration(milliseconds: 120);
  static const base = Duration(milliseconds: 200);
  static const slow = Duration(milliseconds: 300);
}
