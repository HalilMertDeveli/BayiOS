import 'dart:convert';
import 'dart:io';
import 'dart:ui' as ui;

import 'package:flutter/rendering.dart';
import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:google_fonts/google_fonts.dart';

/// Screenshots are a design tool, not a check: they only run with
/// `--dart-define=BAYIOS_SCREENSHOTS=true` (see screens_screenshot_test.dart)
/// so the normal `flutter test` never writes files.
const screenshotsEnabled = bool.fromEnvironment('BAYIOS_SCREENSHOTS');

/// Where the PNGs go (repo `docs/design/screenshots/mobile`), overridable
/// with `--dart-define=BAYIOS_SCREENSHOT_DIR=…`.
const _dirOverride = String.fromEnvironment('BAYIOS_SCREENSHOT_DIR');

String get screenshotDir => _dirOverride.isNotEmpty
    ? _dirOverride
    : '${Directory.current.path}/../../docs/design/screenshots/mobile';

/// Phone viewport: 375×812 logical px at 2× (750×1624 PNG).
const phoneSize = Size(375, 812);
const phonePixelRatio = 2.0;

void usePhoneViewport(WidgetTester tester) {
  tester.view
    ..physicalSize = phoneSize * phonePixelRatio
    ..devicePixelRatio = phonePixelRatio;
  addTearDown(tester.view.reset);
}

/// flutter_test renders every font as boxes ("Ahem") unless the real fonts
/// are loaded: Material Icons from the app's font manifest. Inter Tight is
/// loaded by google_fonts from the bundled assets (see [settle]).
Future<void> loadManifestFonts() async {
  final manifest = jsonDecode(
    await rootBundle.loadString('FontManifest.json'),
  ) as List<dynamic>;
  for (final entry in manifest.cast<Map<String, dynamic>>()) {
    final family = entry['family'] as String;
    final loader = FontLoader(family.split('/').last);
    for (final font
        in (entry['fonts'] as List<dynamic>).cast<Map<String, dynamic>>()) {
      loader.addFont(rootBundle.load(font['asset'] as String));
    }
    await loader.load();
  }
}

/// Lets the asynchronous work of a frame finish for real (font files, SVG
/// decoding) and re-renders, a few times.
Future<void> settle(WidgetTester tester) async {
  for (var i = 0; i < 4; i++) {
    await tester.runAsync(() async {
      await GoogleFonts.pendingFonts();
      await Future<void>.delayed(const Duration(milliseconds: 60));
    });
    await tester.pump(const Duration(milliseconds: 400));
  }
}

/// Writes the [boundary] subtree as `<dir>/<name>.png`.
Future<void> capture(WidgetTester tester, Key boundary, String name) async {
  await settle(tester);
  final render =
      tester.renderObject(find.byKey(boundary)) as RenderRepaintBoundary;
  await tester.runAsync(() async {
    final image = await render.toImage(pixelRatio: phonePixelRatio);
    final bytes = await image.toByteData(format: ui.ImageByteFormat.png);
    image.dispose();
    final file = File('$screenshotDir/$name.png');
    await file.parent.create(recursive: true);
    await file.writeAsBytes(bytes!.buffer.asUint8List());
  });
}
