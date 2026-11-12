import 'package:bayios_mobile/app/app.dart';
import 'package:bayios_mobile/core/theme/app_theme.dart';
import 'package:bayios_mobile/l10n/app_localizations.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_riverpod/misc.dart';
import 'package:flutter_test/flutter_test.dart';

final AppLocalizations l10n = lookupAppLocalizations(appLocale);

/// Pumps [child] inside the app theme and Turkish localizations.
Future<void> pumpScreen(
  WidgetTester tester,
  Widget child, {
  List<Override> overrides = const [],
}) async {
  await tester.pumpWidget(
    ProviderScope(
      retry: (_, _) => null,
      overrides: overrides,
      child: MaterialApp(
        theme: buildAppTheme(),
        locale: appLocale,
        supportedLocales: AppLocalizations.supportedLocales,
        localizationsDelegates: AppLocalizations.localizationsDelegates,
        home: child,
      ),
    ),
  );
}

/// Pumps the whole app (router + guards).
Future<void> pumpApp(
  WidgetTester tester, {
  List<Override> overrides = const [],
}) async {
  await tester.pumpWidget(
    ProviderScope(
      retry: (_, _) => null,
      overrides: overrides,
      child: const BayiOsApp(),
    ),
  );
}

/// Every tappable control is at least 48×48 dp (Android guideline) and
/// labelled for screen readers.
Future<void> expectAccessibleTargets(WidgetTester tester) async {
  await expectLater(tester, meetsGuideline(androidTapTargetGuideline));
  await expectLater(tester, meetsGuideline(labeledTapTargetGuideline));
  await expectLater(tester, meetsGuideline(textContrastGuideline));
}
