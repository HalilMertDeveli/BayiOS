import 'package:bayios_mobile/core/ui/tokens.dart';
import 'package:flutter/material.dart';

/// Material 3 theme built from the BayiOS tokens. Every interactive control
/// gets a minimum size of 48 dp (BosSize.minTouchTarget).
ThemeData buildAppTheme() {
  final scheme = ColorScheme.fromSeed(seedColor: BosColors.brandPrimary)
      .copyWith(
        primary: BosColors.brandPrimary,
        onPrimary: BosColors.brandOnPrimary,
        primaryContainer: BosColors.brandPrimarySubtle,
        onPrimaryContainer: BosColors.brandPrimaryHover,
        secondary: BosColors.textSecondary,
        onSecondary: BosColors.textInverse,
        error: BosColors.danger.fg,
        onError: BosColors.textInverse,
        errorContainer: BosColors.danger.bg,
        onErrorContainer: BosColors.danger.fg,
        surface: BosColors.surfaceCard,
        onSurface: BosColors.textPrimary,
        onSurfaceVariant: BosColors.textMuted,
        surfaceContainerLowest: BosColors.surfaceCard,
        surfaceContainerLow: BosColors.surfacePage,
        surfaceContainer: BosColors.surfacePage,
        surfaceContainerHigh: BosColors.surfaceSunken,
        outline: BosColors.borderStrong,
        outlineVariant: BosColors.borderDefault,
      );

  const minButton = Size(BosSize.minTouchTarget, BosSize.minTouchTarget);
  const buttonShape = RoundedRectangleBorder(
    borderRadius: BorderRadius.all(Radius.circular(BosRadius.lg)),
  );
  const buttonPadding = EdgeInsets.symmetric(
    horizontal: BosSpace.s20,
    vertical: BosSpace.s12,
  );

  final textTheme =
      TextTheme(
        displaySmall: BosType.display,
        headlineMedium: BosType.h1,
        headlineSmall: BosType.h2,
        titleLarge: BosType.h2,
        titleMedium: BosType.h3,
        titleSmall: BosType.body.copyWith(fontWeight: FontWeight.w600),
        bodyLarge: BosType.bodyLg,
        bodyMedium: BosType.body,
        bodySmall: BosType.caption,
        labelLarge: BosType.body.copyWith(fontWeight: FontWeight.w600),
        labelMedium: BosType.caption.copyWith(fontWeight: FontWeight.w600),
        labelSmall: BosType.caption,
      ).apply(
        bodyColor: BosColors.textPrimary,
        displayColor: BosColors.textPrimary,
      );

  return ThemeData(
    useMaterial3: true,
    colorScheme: scheme,
    scaffoldBackgroundColor: BosColors.surfacePage,
    textTheme: textTheme,
    materialTapTargetSize: MaterialTapTargetSize.padded,
    visualDensity: VisualDensity.standard,
    appBarTheme: const AppBarTheme(
      backgroundColor: BosColors.surfaceCard,
      foregroundColor: BosColors.textPrimary,
      elevation: 0,
      scrolledUnderElevation: 1,
      centerTitle: false,
    ),
    filledButtonTheme: FilledButtonThemeData(
      style: FilledButton.styleFrom(
        minimumSize: minButton,
        padding: buttonPadding,
        shape: buttonShape,
        textStyle: textTheme.labelLarge,
      ),
    ),
    outlinedButtonTheme: OutlinedButtonThemeData(
      style: OutlinedButton.styleFrom(
        minimumSize: minButton,
        padding: buttonPadding,
        shape: buttonShape,
        side: const BorderSide(color: BosColors.borderStrong),
        textStyle: textTheme.labelLarge,
      ),
    ),
    textButtonTheme: TextButtonThemeData(
      style: TextButton.styleFrom(
        minimumSize: minButton,
        padding: buttonPadding,
        shape: buttonShape,
        textStyle: textTheme.labelLarge,
      ),
    ),
    iconButtonTheme: IconButtonThemeData(
      style: IconButton.styleFrom(minimumSize: minButton),
    ),
    listTileTheme: const ListTileThemeData(
      minVerticalPadding: BosSpace.s12,
      minTileHeight: BosSize.minTouchTarget + BosSpace.s8,
      contentPadding: EdgeInsets.symmetric(horizontal: BosSpace.s16),
    ),
    navigationBarTheme: const NavigationBarThemeData(
      height: 72,
      backgroundColor: BosColors.surfaceCard,
      indicatorColor: BosColors.brandPrimarySubtle,
    ),
    cardTheme: const CardThemeData(
      color: BosColors.surfaceCard,
      elevation: 0,
      margin: EdgeInsets.zero,
      shape: RoundedRectangleBorder(
        borderRadius: BorderRadius.all(Radius.circular(BosRadius.xl)),
        side: BorderSide(color: BosColors.borderDefault),
      ),
    ),
    inputDecorationTheme: const InputDecorationTheme(
      filled: true,
      fillColor: BosColors.surfaceCard,
      // 16 + 24 line + 16 = 56 dp tall fields.
      contentPadding: EdgeInsets.symmetric(
        horizontal: BosSpace.s16,
        vertical: BosSpace.s16,
      ),
      border: OutlineInputBorder(
        borderRadius: BorderRadius.all(Radius.circular(BosRadius.lg)),
        borderSide: BorderSide(color: BosColors.borderStrong),
      ),
      enabledBorder: OutlineInputBorder(
        borderRadius: BorderRadius.all(Radius.circular(BosRadius.lg)),
        borderSide: BorderSide(color: BosColors.borderStrong),
      ),
      focusedBorder: OutlineInputBorder(
        borderRadius: BorderRadius.all(Radius.circular(BosRadius.lg)),
        borderSide: BorderSide(color: BosColors.borderFocus, width: 2),
      ),
    ),
    snackBarTheme: const SnackBarThemeData(behavior: SnackBarBehavior.floating),
  );
}
