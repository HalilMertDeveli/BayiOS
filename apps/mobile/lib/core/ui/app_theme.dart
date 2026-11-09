import 'package:bayios_mobile/core/ui/tokens.dart';
import 'package:flutter/material.dart';

/// Material 3 theme built from the BayiOS tokens. Every interactive control
/// gets a minimum size of 48 dp (BosSize.minTouchTarget); primary actions
/// are 56 dp. The component themes below are the single place for the
/// handheld look (app bars, cards, inputs, chips, tabs, bottom navigation),
/// so screens do not restyle controls one by one.
ThemeData buildAppTheme() {
  final scheme = ColorScheme.fromSeed(seedColor: BosColors.brandPrimary)
      .copyWith(
        primary: BosColors.brandPrimary,
        onPrimary: BosColors.brandOnPrimary,
        primaryContainer: BosColors.brandPrimarySubtle,
        onPrimaryContainer: BosColors.brandPrimaryHover,
        secondary: BosColors.textSecondary,
        onSecondary: BosColors.textInverse,
        secondaryContainer: BosPetrol.p100,
        onSecondaryContainer: BosPetrol.p900,
        tertiary: BosColors.brandAccent,
        onTertiary: BosColors.brandOnAccent,
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
        surfaceContainerHighest: BosColors.surfaceSunken,
        surfaceTint: Colors.transparent,
        outline: BosColors.borderStrong,
        outlineVariant: BosColors.borderDefault,
      );

  const minButton = Size(BosSize.minTouchTarget, BosSize.minTouchTarget);
  const buttonShape = RoundedRectangleBorder(
    borderRadius: BorderRadius.all(Radius.circular(BosRadius.xl)),
  );
  const buttonPadding = EdgeInsets.symmetric(
    horizontal: BosSpace.s20,
    vertical: BosSpace.s12,
  );
  const fieldRadius = BorderRadius.all(Radius.circular(BosRadius.xl));

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
    appBarTheme: AppBarTheme(
      backgroundColor: BosColors.surfaceCard,
      foregroundColor: BosColors.textPrimary,
      surfaceTintColor: Colors.transparent,
      elevation: 0,
      scrolledUnderElevation: 0,
      centerTitle: false,
      toolbarHeight: 64,
      titleTextStyle: BosType.h2.copyWith(
        color: BosColors.textPrimary,
        fontWeight: FontWeight.w700,
      ),
      // A hairline instead of a shadow: the bar stays calm and the page edge
      // stays visible in bright warehouse light.
      shape: const Border(bottom: BorderSide(color: BosColors.borderDefault)),
    ),
    tabBarTheme: TabBarThemeData(
      labelColor: BosColors.brandPrimary,
      unselectedLabelColor: BosColors.textSecondary,
      indicatorColor: BosColors.brandPrimary,
      indicatorSize: TabBarIndicatorSize.tab,
      indicator: const UnderlineTabIndicator(
        borderSide: BorderSide(color: BosColors.brandPrimary, width: 3),
        borderRadius: BorderRadius.vertical(top: Radius.circular(3)),
      ),
      dividerColor: BosColors.borderDefault,
      labelStyle: BosType.body.copyWith(fontWeight: FontWeight.w700),
      unselectedLabelStyle: BosType.body.copyWith(fontWeight: FontWeight.w600),
    ),
    filledButtonTheme: FilledButtonThemeData(
      style: FilledButton.styleFrom(
        minimumSize: minButton,
        padding: buttonPadding,
        shape: buttonShape,
        textStyle: textTheme.labelLarge?.copyWith(fontWeight: FontWeight.w700),
        disabledBackgroundColor: BosColors.surfaceSunken,
        disabledForegroundColor: BosColors.textMuted,
      ),
    ),
    outlinedButtonTheme: OutlinedButtonThemeData(
      style: OutlinedButton.styleFrom(
        minimumSize: minButton,
        padding: buttonPadding,
        shape: buttonShape,
        foregroundColor: BosColors.brandPrimary,
        backgroundColor: BosColors.surfaceCard,
        side: const BorderSide(color: BosColors.borderStrong),
        textStyle: textTheme.labelLarge,
      ),
    ),
    textButtonTheme: TextButtonThemeData(
      style: TextButton.styleFrom(
        minimumSize: minButton,
        padding: buttonPadding,
        shape: buttonShape,
        foregroundColor: BosColors.textLink,
        textStyle: textTheme.labelLarge,
      ),
    ),
    iconButtonTheme: IconButtonThemeData(
      style: IconButton.styleFrom(minimumSize: minButton),
    ),
    listTileTheme: ListTileThemeData(
      minVerticalPadding: BosSpace.s12,
      minTileHeight: BosSize.minTouchTarget + BosSpace.s8,
      contentPadding: const EdgeInsets.symmetric(horizontal: BosSpace.s16),
      iconColor: BosColors.brandPrimary,
      titleTextStyle: BosType.bodyLg.copyWith(
        color: BosColors.textPrimary,
        fontWeight: FontWeight.w600,
      ),
      subtitleTextStyle: BosType.body.copyWith(color: BosColors.textSecondary),
    ),
    navigationBarTheme: NavigationBarThemeData(
      height: 76,
      elevation: 0,
      backgroundColor: BosColors.surfaceCard,
      surfaceTintColor: Colors.transparent,
      indicatorColor: BosPetrol.p100,
      indicatorShape: const StadiumBorder(),
      labelBehavior: NavigationDestinationLabelBehavior.alwaysShow,
      iconTheme: WidgetStateProperty.resolveWith(
        (states) => IconThemeData(
          size: 26,
          color: states.contains(WidgetState.selected)
              ? BosColors.brandPrimaryHover
              : BosColors.textMuted,
        ),
      ),
      labelTextStyle: WidgetStateProperty.resolveWith(
        (states) => BosType.caption.copyWith(
          color: states.contains(WidgetState.selected)
              ? BosColors.brandPrimaryHover
              : BosColors.textSecondary,
          fontWeight: states.contains(WidgetState.selected)
              ? FontWeight.w700
              : FontWeight.w500,
        ),
      ),
    ),
    cardTheme: const CardThemeData(
      color: BosColors.surfaceCard,
      surfaceTintColor: Colors.transparent,
      elevation: 0,
      margin: EdgeInsets.zero,
      shape: RoundedRectangleBorder(
        borderRadius: BorderRadius.all(Radius.circular(BosRadius.xl)),
        side: BorderSide(color: BosColors.borderDefault),
      ),
    ),
    chipTheme: ChipThemeData(
      backgroundColor: BosColors.surfaceSunken,
      selectedColor: BosColors.brandPrimarySubtle,
      side: const BorderSide(color: BosColors.borderDefault),
      labelStyle: BosType.body.copyWith(
        color: BosColors.textPrimary,
        fontWeight: FontWeight.w600,
      ),
      padding: const EdgeInsets.symmetric(
        horizontal: BosSpace.s8,
        vertical: BosSpace.s8,
      ),
      shape: const StadiumBorder(),
    ),
    segmentedButtonTheme: SegmentedButtonThemeData(
      style: SegmentedButton.styleFrom(
        minimumSize: const Size(BosSize.minTouchTarget, BosSize.primaryAction),
        selectedBackgroundColor: BosColors.brandPrimarySubtle,
        selectedForegroundColor: BosColors.brandPrimaryHover,
        side: const BorderSide(color: BosColors.borderStrong),
        textStyle: textTheme.labelLarge,
      ),
    ),
    dividerTheme: const DividerThemeData(
      color: BosColors.borderDefault,
      thickness: 1,
      space: 1,
    ),
    progressIndicatorTheme: const ProgressIndicatorThemeData(
      color: BosColors.brandPrimary,
      linearTrackColor: BosColors.surfaceSunken,
      circularTrackColor: BosColors.brandPrimarySubtle,
    ),
    dialogTheme: DialogThemeData(
      backgroundColor: BosColors.surfaceCard,
      surfaceTintColor: Colors.transparent,
      shape: const RoundedRectangleBorder(
        borderRadius: BorderRadius.all(Radius.circular(BosRadius.xl * 2)),
      ),
      titleTextStyle: BosType.h2.copyWith(
        color: BosColors.textPrimary,
        fontWeight: FontWeight.w700,
      ),
      contentTextStyle: BosType.bodyLg.copyWith(color: BosColors.textSecondary),
    ),
    bottomSheetTheme: const BottomSheetThemeData(
      backgroundColor: BosColors.surfaceCard,
      surfaceTintColor: Colors.transparent,
      shape: RoundedRectangleBorder(
        borderRadius: BorderRadius.vertical(
          top: Radius.circular(BosRadius.xl * 2),
        ),
      ),
    ),
    inputDecorationTheme: InputDecorationTheme(
      filled: true,
      fillColor: BosColors.surfaceCard,
      // 16 + 24 line + 16 = 56 dp tall fields.
      contentPadding: const EdgeInsets.symmetric(
        horizontal: BosSpace.s16,
        vertical: BosSpace.s16,
      ),
      prefixIconColor: BosColors.textMuted,
      suffixIconColor: BosColors.textMuted,
      floatingLabelStyle: BosType.body.copyWith(
        color: BosColors.brandPrimary,
        fontWeight: FontWeight.w600,
      ),
      helperStyle: BosType.caption.copyWith(color: BosColors.textMuted),
      errorStyle: BosType.caption.copyWith(
        color: BosColors.danger.fg,
        fontWeight: FontWeight.w600,
      ),
      border: const OutlineInputBorder(
        borderRadius: fieldRadius,
        borderSide: BorderSide(color: BosColors.borderStrong),
      ),
      enabledBorder: const OutlineInputBorder(
        borderRadius: fieldRadius,
        borderSide: BorderSide(color: BosColors.borderStrong),
      ),
      disabledBorder: const OutlineInputBorder(
        borderRadius: fieldRadius,
        borderSide: BorderSide(color: BosColors.borderDefault),
      ),
      focusedBorder: const OutlineInputBorder(
        borderRadius: fieldRadius,
        borderSide: BorderSide(color: BosColors.borderFocus, width: 2),
      ),
      errorBorder: OutlineInputBorder(
        borderRadius: fieldRadius,
        borderSide: BorderSide(color: BosColors.danger.fg),
      ),
      focusedErrorBorder: OutlineInputBorder(
        borderRadius: fieldRadius,
        borderSide: BorderSide(color: BosColors.danger.fg, width: 2),
      ),
    ),
    snackBarTheme: SnackBarThemeData(
      behavior: SnackBarBehavior.floating,
      backgroundColor: BosColors.textPrimary,
      contentTextStyle: BosType.body.copyWith(color: BosColors.textInverse),
      actionTextColor: BosPetrol.p200,
      shape: const RoundedRectangleBorder(
        borderRadius: BorderRadius.all(Radius.circular(BosRadius.xl)),
      ),
    ),
  );
}
