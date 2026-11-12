import 'dart:async';

import 'package:bayios_mobile/core/theme/app_theme.dart';

/// Runs before every test file in test/. Widget tests render text in the
/// Flutter test font (solid glyphs), which the pixel-based
/// `textContrastGuideline` is built for; the bundled Inter Tight is only
/// loaded for the screenshot run (`--dart-define=BAYIOS_SCREENSHOTS=true`).
Future<void> testExecutable(FutureOr<void> Function() testMain) async {
  AppFonts.bundled = const bool.fromEnvironment('BAYIOS_SCREENSHOTS');
  await testMain();
}
