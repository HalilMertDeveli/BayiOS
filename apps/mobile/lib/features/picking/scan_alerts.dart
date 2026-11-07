import 'dart:async';

import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

/// Feedback a worker notices without looking at the screen. Uses only the
/// platform's haptic/system-sound channels (no extra plugin or permission:
/// `performHapticFeedback` needs no VIBRATE permission). Devices with haptics
/// or sounds turned off simply stay silent; the screen always shows the
/// message as well.
abstract interface class ScanAlerts {
  /// Accepted scan / shelf.
  void accepted();

  /// Wrong product, wrong shelf, unknown code, over-pick: the line was not
  /// counted.
  void rejected();
}

class DeviceScanAlerts implements ScanAlerts {
  const DeviceScanAlerts();

  @override
  void accepted() {
    unawaited(HapticFeedback.selectionClick());
    unawaited(SystemSound.play(SystemSoundType.click));
  }

  @override
  void rejected() {
    unawaited(_doubleBuzz());
    unawaited(SystemSound.play(SystemSoundType.alert));
  }

  static Future<void> _doubleBuzz() async {
    await HapticFeedback.heavyImpact();
    await Future<void>.delayed(const Duration(milliseconds: 120));
    await HapticFeedback.vibrate();
  }
}

/// Overridden in tests with a recorder.
final scanAlertsProvider = Provider<ScanAlerts>(
  (ref) => const DeviceScanAlerts(),
);
