import 'dart:async';

import 'package:bayios_mobile/core/ui/tokens.dart';
import 'package:bayios_mobile/l10n/app_localizations.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:mobile_scanner/mobile_scanner.dart';

/// Reads one barcode. Returns the code, or null when the user cancelled or
/// chose manual entry (the caller then focuses its text field). Every screen
/// that scans also has that text field: hardware / keyboard-wedge scanners
/// type into it and press Enter, and it is the fallback when the camera is
/// missing or its permission is denied.
abstract interface class BarcodeScanner {
  Future<String?> scan(BuildContext context, {required String title});
}

/// Full-screen camera scanner (mobile_scanner). The plugin asks for the
/// camera permission when the preview starts; a denial is shown with the
/// manual-entry way out.
class CameraBarcodeScanner implements BarcodeScanner {
  const CameraBarcodeScanner();

  @override
  Future<String?> scan(BuildContext context, {required String title}) =>
      Navigator.of(context, rootNavigator: true).push<String>(
        MaterialPageRoute(
          fullscreenDialog: true,
          builder: (_) => ScannerScreen(title: title),
        ),
      );
}

/// Overridden in tests (and usable for a hardware-scanner integration).
final barcodeScannerProvider = Provider<BarcodeScanner>(
  (ref) => const CameraBarcodeScanner(),
);

class ScannerScreen extends StatefulWidget {
  const ScannerScreen({required this.title, super.key});

  final String title;

  @override
  State<ScannerScreen> createState() => _ScannerScreenState();
}

class _ScannerScreenState extends State<ScannerScreen> {
  final _controller = MobileScannerController(
    detectionSpeed: DetectionSpeed.noDuplicates,
  );
  bool _done = false;

  @override
  void dispose() {
    unawaited(_controller.dispose());
    super.dispose();
  }

  void _onDetect(BarcodeCapture capture) {
    if (_done) return;
    for (final barcode in capture.barcodes) {
      final value = barcode.rawValue?.trim();
      if (value != null && value.isNotEmpty) {
        _done = true;
        Navigator.of(context).pop(value);
        return;
      }
    }
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    return Scaffold(
      appBar: AppBar(
        title: Text(widget.title),
        actions: [
          IconButton(
            tooltip: l10n.stockScanTorch,
            icon: const Icon(Icons.flashlight_on_outlined),
            onPressed: _controller.toggleTorch,
          ),
        ],
      ),
      body: Column(
        children: [
          Expanded(
            child: MobileScanner(
              controller: _controller,
              onDetect: _onDetect,
              errorBuilder: (context, error) =>
                  ScannerErrorPanel(code: error.errorCode),
            ),
          ),
          SafeArea(
            top: false,
            child: Padding(
              padding: const EdgeInsets.all(BosSpace.s16),
              child: Column(
                mainAxisSize: MainAxisSize.min,
                children: [
                  Text(l10n.stockScanHint, textAlign: TextAlign.center),
                  const SizedBox(height: BosSpace.s12),
                  OutlinedButton.icon(
                    key: const Key('scanner-manual'),
                    onPressed: () => Navigator.of(context).pop(),
                    icon: const Icon(Icons.keyboard_outlined),
                    label: Text(l10n.stockScanManual),
                    style: OutlinedButton.styleFrom(
                      minimumSize: const Size.fromHeight(BosSize.primaryAction),
                    ),
                  ),
                ],
              ),
            ),
          ),
        ],
      ),
    );
  }
}

/// Why the camera cannot scan, and what to do instead.
class ScannerErrorPanel extends StatelessWidget {
  const ScannerErrorPanel({required this.code, super.key});

  final MobileScannerErrorCode code;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final message = switch (code) {
      MobileScannerErrorCode.permissionDenied => l10n.stockScanPermissionDenied,
      MobileScannerErrorCode.unsupported => l10n.stockScanUnsupported,
      _ => l10n.stockScanFailed,
    };
    return ColoredBox(
      color: BosColors.surfacePage,
      child: Center(
        child: Padding(
          padding: const EdgeInsets.all(BosSpace.s24),
          child: Column(
            mainAxisSize: MainAxisSize.min,
            children: [
              Icon(
                code == MobileScannerErrorCode.permissionDenied
                    ? Icons.no_photography_outlined
                    : Icons.videocam_off_outlined,
                size: 48,
                color: BosColors.danger.fg,
              ),
              const SizedBox(height: BosSpace.s16),
              Semantics(
                liveRegion: true,
                child: Text(
                  message,
                  key: const Key('scanner-error'),
                  textAlign: TextAlign.center,
                  style: Theme.of(context).textTheme.bodyLarge,
                ),
              ),
            ],
          ),
        ),
      ),
    );
  }
}
