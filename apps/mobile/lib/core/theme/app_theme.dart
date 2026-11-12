import 'package:bayios_mobile/core/theme/app_colors.dart';
import 'package:bayios_mobile/core/theme/app_palette.dart';
import 'package:bayios_mobile/core/theme/app_spacing.dart';
import 'package:flutter/material.dart';
import 'package:google_fonts/google_fonts.dart';

/// Font switch. Inter Tight (bundled, google_fonts) in the app; widget tests
/// turn it off (test/flutter_test_config.dart) so text renders in the test
/// font and the pixel-based contrast guideline stays meaningful; the
/// screenshot run keeps it on.
abstract final class AppFonts {
  static bool bundled = true;
}

/// Light theme (kit "Light").
ThemeData buildAppTheme() => buildTheme(AppColors.light, Brightness.light);

/// Dark theme (kit "Dark": near-black page, white-5 % cards).
ThemeData buildAppDarkTheme() => buildTheme(AppColors.dark, Brightness.dark);

TextStyle _scale(double size, double line, FontWeight weight) => TextStyle(
  fontSize: size,
  height: line / size,
  fontWeight: weight,
  letterSpacing: 0,
);

/// The kit type scale on the Material text roles (Inter Tight):
/// - headline*: page titles (H5 24/34 bold, H4 32/42 for the big numbers)
/// - title*: app bar (Large 18/24), card titles (Medium 16/20), small titles
/// - body*: Large 18/24, Medium 16/20 (default text), Small 14/18
/// - label*: buttons 14/20 medium, X-Small 12/16 (badges, captions)
TextTheme _textTheme(AppColors c) {
  final base = TextTheme(
    displaySmall: _scale(
      AppTypeScale.headingH4Size,
      AppTypeScale.headingH4Line,
      AppTypeScale.headingH4Weight,
    ),
    headlineMedium: _scale(
      AppTypeScale.headingH5Size,
      AppTypeScale.headingH5Line,
      AppTypeScale.headingH5Weight,
    ),
    headlineSmall: _scale(
      AppTypeScale.largeBoldSize,
      AppTypeScale.largeBoldLine,
      AppTypeScale.largeBoldWeight,
    ),
    titleLarge: _scale(
      AppTypeScale.largeSemiBoldSize,
      AppTypeScale.largeSemiBoldLine,
      AppTypeScale.largeSemiBoldWeight,
    ),
    titleMedium: _scale(
      AppTypeScale.mediumSemiBoldSize,
      AppTypeScale.mediumSemiBoldLine,
      AppTypeScale.mediumSemiBoldWeight,
    ),
    titleSmall: _scale(
      AppTypeScale.smallSemiBoldSize,
      AppTypeScale.smallSemiBoldLine,
      AppTypeScale.smallSemiBoldWeight,
    ),
    bodyLarge: _scale(
      AppTypeScale.largeRegularSize,
      AppTypeScale.largeRegularLine,
      AppTypeScale.largeRegularWeight,
    ),
    bodyMedium: _scale(
      AppTypeScale.mediumRegularSize,
      AppTypeScale.mediumRegularLine,
      AppTypeScale.mediumRegularWeight,
    ),
    bodySmall: _scale(
      AppTypeScale.smallRegularSize,
      AppTypeScale.smallRegularLine,
      AppTypeScale.smallRegularWeight,
    ),
    labelLarge: _scale(
      AppTypeScale.buttonSize,
      AppTypeScale.buttonLine,
      AppTypeScale.buttonWeight,
    ),
    labelMedium: _scale(
      AppTypeScale.xsmallMediumSize,
      AppTypeScale.xsmallMediumLine,
      AppTypeScale.xsmallMediumWeight,
    ),
    labelSmall: _scale(
      AppTypeScale.xsmallRegularSize,
      AppTypeScale.xsmallRegularLine,
      AppTypeScale.xsmallRegularWeight,
    ),
  ).apply(bodyColor: c.textPrimary, displayColor: c.textPrimary);
  return AppFonts.bundled ? GoogleFonts.interTightTextTheme(base) : base;
}

/// Style helpers on top of the theme text styles.
extension AppTextStyleX on TextStyle {
  /// The same style at another weight, re-resolved to the matching Inter
  /// Tight file (a plain `copyWith(fontWeight:)` would fake-bold the
  /// regular face).
  TextStyle weight(FontWeight weight) => AppFonts.bundled
      ? GoogleFonts.interTight(textStyle: copyWith(fontWeight: weight))
      : copyWith(fontWeight: weight);

  TextStyle get medium => weight(FontWeight.w500);
  TextStyle get semiBold => weight(FontWeight.w600);
  TextStyle get bold => weight(FontWeight.w700);

  /// Codes and quantities: equal-width digits so columns line up.
  TextStyle get tabular =>
      copyWith(fontFeatures: const [FontFeature.tabularFigures()]);
}

/// Inherit-only emphasis: keeps the surrounding size and colour (inside a
/// notice, a banner) and switches to the matching Inter Tight weight.
abstract final class AppTextStyles {
  static TextStyle get strong =>
      const TextStyle(fontWeight: FontWeight.w700).bold;
  static TextStyle get semiStrong =>
      const TextStyle(fontWeight: FontWeight.w600).semiBold;
}

/// Material 3 theme from the BayiOS colours. Every interactive control gets
/// a minimum size of 48 dp ([AppSize.minTouchTarget]); the component themes
/// below are the single place for the kit look (flat white bars, bordered
/// cards and inputs, radius 8 controls), so screens do not restyle controls
/// one by one.
ThemeData buildTheme(AppColors c, Brightness brightness) {
  // Inter Tight ships with the app (assets/google_fonts, OFL): never fetch
  // fonts at runtime (offline handhelds, tests).
  GoogleFonts.config.allowRuntimeFetching = false;
  final dark = brightness == Brightness.dark;

  final scheme =
      ColorScheme.fromSeed(
        seedColor: c.brandPrimary,
        brightness: brightness,
      ).copyWith(
        primary: c.brandPrimary,
        onPrimary: c.brandOnPrimary,
        primaryContainer: c.brand.bg,
        onPrimaryContainer: c.brand.fg,
        secondary: c.textSecondary,
        onSecondary: c.textInverse,
        secondaryContainer: c.brandPrimarySubtle,
        onSecondaryContainer: c.brand.fg,
        tertiary: c.brandAccent,
        onTertiary: c.brandOnAccent,
        error: c.danger.fg,
        onError: c.textInverse,
        errorContainer: c.danger.bg,
        onErrorContainer: c.danger.fg,
        surface: c.surfaceCard,
        onSurface: c.textPrimary,
        onSurfaceVariant: c.textMuted,
        surfaceContainerLowest: c.surfaceCard,
        surfaceContainerLow: c.surfacePage,
        surfaceContainer: c.surfacePage,
        surfaceContainerHigh: c.surfaceSunken,
        surfaceContainerHighest: c.surfaceSunken,
        surfaceTint: Colors.transparent,
        outline: c.borderStrong,
        outlineVariant: c.borderDefault,
        scrim: c.overlay,
        shadow: AppPalette.black,
      );

  final text = _textTheme(c);
  const minButton = Size(AppSize.minTouchTarget, AppSize.minTouchTarget);
  const buttonShape = RoundedRectangleBorder(borderRadius: AppRadius.smAll);
  const buttonPadding = EdgeInsets.symmetric(
    horizontal: AppSpacing.md,
    vertical: AppSpacing.sm,
  );
  final buttonText = text.labelLarge;

  OutlineInputBorder field(Color color, [double width = 1]) =>
      OutlineInputBorder(
        borderRadius: AppRadius.smAll,
        borderSide: BorderSide(color: color, width: width),
      );

  // Kit primary button: brand green deepening towards the bottom edge and a
  // soft lift (flat on disabled / pressed). The lightest stop is the brand
  // green itself, so white text keeps 4.5:1 everywhere on the button.
  Widget raisedPrimary(
    BuildContext context,
    Set<WidgetState> states,
    Widget? child,
  ) {
    final disabled = states.contains(WidgetState.disabled);
    return DecoratedBox(
      decoration: BoxDecoration(
        borderRadius: AppRadius.smAll,
        gradient: disabled
            ? null
            : LinearGradient(
                begin: Alignment.topCenter,
                end: Alignment.bottomCenter,
                colors: [
                  c.brandPrimary,
                  if (dark) c.brandPrimary else c.brandPrimaryHover,
                ],
              ),
        boxShadow: disabled || states.contains(WidgetState.pressed)
            ? AppShadows.none
            : AppShadows.hairline,
      ),
      child: child,
    );
  }

  return ThemeData(
    useMaterial3: true,
    brightness: brightness,
    colorScheme: scheme,
    extensions: [c],
    scaffoldBackgroundColor: c.surfacePage,
    canvasColor: c.surfacePage,
    textTheme: text,
    primaryTextTheme: text,
    fontFamily: text.bodyMedium?.fontFamily,
    materialTapTargetSize: MaterialTapTargetSize.padded,
    visualDensity: VisualDensity.standard,
    iconTheme: IconThemeData(color: c.textSecondary, size: AppSize.iconMd),
    appBarTheme: AppBarTheme(
      backgroundColor: c.surfaceCard,
      foregroundColor: c.textPrimary,
      surfaceTintColor: Colors.transparent,
      elevation: 0,
      scrolledUnderElevation: 0,
      centerTitle: false,
      toolbarHeight: AppSize.topBarHeight,
      titleSpacing: AppSpacing.md,
      iconTheme: IconThemeData(color: c.textPrimary, size: AppSize.icon),
      actionsIconTheme: IconThemeData(color: c.textPrimary, size: AppSize.icon),
      titleTextStyle: text.titleLarge?.copyWith(color: c.textPrimary),
      // A hairline instead of a shadow (kit header).
      shape: Border(bottom: BorderSide(color: c.borderDefault)),
    ),
    tabBarTheme: TabBarThemeData(
      labelColor: c.brandPrimary,
      unselectedLabelColor: c.textMuted,
      indicatorColor: c.brandPrimary,
      indicatorSize: TabBarIndicatorSize.tab,
      indicator: UnderlineTabIndicator(
        borderSide: BorderSide(color: c.brandPrimary, width: 2),
        borderRadius: const BorderRadius.vertical(top: Radius.circular(2)),
      ),
      dividerColor: c.borderDefault,
      labelStyle: text.bodySmall?.semiBold,
      unselectedLabelStyle: text.bodySmall?.medium,
    ),
    filledButtonTheme: FilledButtonThemeData(
      style: FilledButton.styleFrom(
        minimumSize: minButton,
        padding: buttonPadding,
        shape: buttonShape,
        backgroundColor: c.brandPrimary,
        foregroundColor: c.brandOnPrimary,
        textStyle: buttonText,
        iconSize: AppSize.icon,
        elevation: 0,
        disabledBackgroundColor: c.surfaceSunken,
        disabledForegroundColor: c.textMuted,
      ).copyWith(backgroundBuilder: raisedPrimary),
    ),
    outlinedButtonTheme: OutlinedButtonThemeData(
      style: OutlinedButton.styleFrom(
        minimumSize: minButton,
        padding: buttonPadding,
        shape: buttonShape,
        foregroundColor: c.textPrimary,
        backgroundColor: c.surfaceCard,
        iconColor: c.textSecondary,
        iconSize: AppSize.icon,
        side: BorderSide(color: c.borderDefault),
        textStyle: buttonText,
        disabledForegroundColor: c.textMuted,
      ),
    ),
    textButtonTheme: TextButtonThemeData(
      style: TextButton.styleFrom(
        minimumSize: minButton,
        padding: buttonPadding,
        shape: buttonShape,
        foregroundColor: c.textLink,
        iconSize: AppSize.icon,
        textStyle: buttonText,
      ),
    ),
    iconButtonTheme: IconButtonThemeData(
      style: IconButton.styleFrom(
        minimumSize: minButton,
        foregroundColor: c.textPrimary,
        iconSize: AppSize.icon,
      ),
    ),
    listTileTheme: ListTileThemeData(
      minVerticalPadding: AppSpacing.sm,
      minTileHeight: AppSize.primaryAction,
      contentPadding: const EdgeInsets.symmetric(horizontal: AppSpacing.md),
      iconColor: c.textSecondary,
      titleTextStyle: text.bodyMedium?.medium.copyWith(color: c.textPrimary),
      subtitleTextStyle: text.bodySmall?.copyWith(color: c.textMuted),
      shape: const RoundedRectangleBorder(borderRadius: AppRadius.smAll),
    ),
    navigationBarTheme: NavigationBarThemeData(
      height: 72,
      elevation: 0,
      backgroundColor: c.surfaceCard,
      surfaceTintColor: Colors.transparent,
      indicatorColor: c.brandPrimarySubtle,
      indicatorShape: const RoundedRectangleBorder(
        borderRadius: AppRadius.smAll,
      ),
      labelBehavior: NavigationDestinationLabelBehavior.alwaysShow,
      iconTheme: WidgetStateProperty.resolveWith(
        (states) => IconThemeData(
          size: AppSize.iconMd,
          color: states.contains(WidgetState.selected)
              ? c.brandPrimary
              : c.textMuted,
        ),
      ),
      labelTextStyle: WidgetStateProperty.resolveWith(
        (states) => states.contains(WidgetState.selected)
            ? text.labelMedium!.semiBold.copyWith(color: c.brandPrimary)
            : text.labelMedium!.copyWith(color: c.textMuted),
      ),
    ),
    cardTheme: CardThemeData(
      color: c.surfaceCard,
      surfaceTintColor: Colors.transparent,
      elevation: 0,
      margin: EdgeInsets.zero,
      shape: RoundedRectangleBorder(
        borderRadius: AppRadius.lgAll,
        side: BorderSide(color: c.borderDefault),
      ),
    ),
    chipTheme: ChipThemeData(
      backgroundColor: c.surfaceCard,
      selectedColor: c.brandPrimarySubtle,
      side: BorderSide(color: c.borderDefault),
      labelStyle: text.bodySmall?.medium.copyWith(color: c.textPrimary),
      padding: const EdgeInsets.symmetric(
        horizontal: AppSpacing.xs,
        vertical: AppSpacing.xs,
      ),
      shape: const RoundedRectangleBorder(borderRadius: AppRadius.smAll),
    ),
    segmentedButtonTheme: SegmentedButtonThemeData(
      style: SegmentedButton.styleFrom(
        minimumSize: const Size(AppSize.minTouchTarget, AppSize.minTouchTarget),
        backgroundColor: c.surfaceSunken,
        foregroundColor: c.textMuted,
        selectedBackgroundColor: c.surfaceCard,
        selectedForegroundColor: c.textPrimary,
        side: BorderSide(color: c.borderDefault),
        textStyle: buttonText,
        shape: buttonShape,
      ),
    ),
    checkboxTheme: CheckboxThemeData(
      shape: RoundedRectangleBorder(
        borderRadius: BorderRadius.circular(AppRadiusScale.xxs),
      ),
      side: BorderSide(color: c.borderStrong, width: 1.5),
      fillColor: WidgetStateProperty.resolveWith(
        (states) =>
            states.contains(WidgetState.selected) ? c.brandAccent : null,
      ),
      checkColor: WidgetStatePropertyAll(c.brandOnPrimary),
    ),
    radioTheme: RadioThemeData(
      fillColor: WidgetStateProperty.resolveWith(
        (states) => states.contains(WidgetState.selected)
            ? c.brandAccent
            : c.borderStrong,
      ),
    ),
    switchTheme: SwitchThemeData(
      thumbColor: WidgetStateProperty.resolveWith(
        (states) => states.contains(WidgetState.selected)
            ? c.brandOnPrimary
            : c.surfaceCard,
      ),
      trackColor: WidgetStateProperty.resolveWith(
        (states) => states.contains(WidgetState.selected)
            ? c.brandPrimary
            : c.borderDefault,
      ),
      trackOutlineColor: WidgetStatePropertyAll(c.borderDefault),
    ),
    dropdownMenuTheme: DropdownMenuThemeData(
      textStyle: text.bodyMedium?.copyWith(color: c.textPrimary),
      menuStyle: MenuStyle(
        backgroundColor: WidgetStatePropertyAll(c.surfaceRaised),
        surfaceTintColor: const WidgetStatePropertyAll(Colors.transparent),
        side: WidgetStatePropertyAll(BorderSide(color: c.borderDefault)),
        shape: const WidgetStatePropertyAll(
          RoundedRectangleBorder(borderRadius: AppRadius.mdAll),
        ),
      ),
    ),
    popupMenuTheme: PopupMenuThemeData(
      color: c.surfaceRaised,
      surfaceTintColor: Colors.transparent,
      shape: RoundedRectangleBorder(
        borderRadius: AppRadius.mdAll,
        side: BorderSide(color: c.borderDefault),
      ),
      textStyle: text.bodyMedium?.copyWith(color: c.textPrimary),
    ),
    dividerTheme: DividerThemeData(
      color: c.borderDefault,
      thickness: 1,
      space: 1,
    ),
    progressIndicatorTheme: ProgressIndicatorThemeData(
      color: c.brandAccent,
      linearTrackColor: c.chartTrack,
      circularTrackColor: c.chartTrack,
    ),
    dialogTheme: DialogThemeData(
      backgroundColor: c.surfaceCard,
      surfaceTintColor: Colors.transparent,
      barrierColor: c.overlay,
      shape: RoundedRectangleBorder(
        borderRadius: AppRadius.lgAll,
        side: BorderSide(color: c.borderDefault),
      ),
      titleTextStyle: text.titleLarge?.copyWith(color: c.textPrimary),
      contentTextStyle: text.bodyMedium?.copyWith(color: c.textSecondary),
    ),
    bottomSheetTheme: BottomSheetThemeData(
      backgroundColor: c.surfaceCard,
      surfaceTintColor: Colors.transparent,
      modalBarrierColor: c.overlay,
      shape: const RoundedRectangleBorder(
        borderRadius: BorderRadius.vertical(top: Radius.circular(AppRadius.lg)),
      ),
    ),
    inputDecorationTheme: InputDecorationTheme(
      filled: true,
      fillColor: c.surfaceCard,
      // 14 + 20 line + 14 = 48 dp tall fields (kit 40 + touch margin).
      contentPadding: const EdgeInsets.symmetric(
        horizontal: AppSpacing.sm,
        vertical: AppSpacingScale.sm + AppRadiusScale.xxxs,
      ),
      hintStyle: text.bodyMedium?.copyWith(color: c.textMuted),
      labelStyle: text.bodyMedium?.copyWith(color: c.textMuted),
      prefixIconColor: c.textMuted,
      suffixIconColor: c.textMuted,
      prefixIconConstraints: const BoxConstraints(
        minWidth: AppSize.minTouchTarget - AppSpacing.xxs,
        minHeight: AppSize.minTouchTarget,
      ),
      floatingLabelStyle: text.bodyMedium?.medium.copyWith(
        color: c.brandPrimary,
      ),
      helperStyle: text.bodySmall?.copyWith(color: c.textMuted),
      errorStyle: text.bodySmall?.medium.copyWith(color: c.danger.fg),
      border: field(c.borderDefault),
      enabledBorder: field(c.borderDefault),
      disabledBorder: field(c.borderDefault),
      focusedBorder: field(c.borderFocus, 1.5),
      errorBorder: field(c.danger.fg),
      focusedErrorBorder: field(c.danger.fg, 1.5),
    ),
    textSelectionTheme: TextSelectionThemeData(
      cursorColor: c.brandPrimary,
      selectionColor: c.brand.bg,
      selectionHandleColor: c.brandPrimary,
    ),
    snackBarTheme: SnackBarThemeData(
      behavior: SnackBarBehavior.floating,
      backgroundColor: c.textPrimary,
      contentTextStyle: text.bodyMedium?.copyWith(color: c.textInverse),
      actionTextColor: c.brandAccentHover,
      shape: const RoundedRectangleBorder(borderRadius: AppRadius.mdAll),
    ),
    tooltipTheme: TooltipThemeData(
      decoration: BoxDecoration(
        color: c.textPrimary,
        borderRadius: AppRadius.smAll,
      ),
      textStyle: text.labelMedium?.copyWith(color: c.textInverse),
    ),
  );
}
