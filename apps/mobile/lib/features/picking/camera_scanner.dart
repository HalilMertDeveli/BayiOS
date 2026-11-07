import 'dart:async';

import 'package:bayios_mobile/core/ui/tokens.dart';
import 'package:bayios_mobile/l10n/app_localizations.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:mobile_scanner/mobile_scanner.dart';

/// What the camera screen returned.
sealed class ScanOutcome {
  const ScanOutcome();
}

/// A barcode / QR code was read.
final class ScannedCode extends ScanOutcome {
  const ScannedCode(this.code);

  final String code;
}

/// No camera, no permission or the user chose "Kodu elle gir": the pick
/// screen focuses its manual entry field.
final class ManualEntryRequested extends ScanOutcome {
  const ManualEntryRequested();
}

/// Opens the scanner and resolves with the outcome (null = closed).
/// [shelf] only changes the hint text.
typedef CodeScanner = Future<ScanOutcome?> Function(
  BuildContext context, {
  required bool shelf,
});

Future<ScanOutcome?> _openCameraScanner(
  BuildContext context, {
  required bool shelf,
}) => Navigator.of(context).push<ScanOutcome>(
  MaterialPageRoute(
    fullscreenDialog: true,
    builder: (_) => CameraScanScreen(shelf: shelf),
  ),
);

/// The camera in production; tests (and the emulator smoke, which has no
/// real barcode in front of its virtual camera) use the manual entry path or
/// override this provider.
final codeScannerProvider = Provider<CodeScanner>((ref) => _openCameraScanner);

/// Full-screen camera (mobile_scanner, ML Kit on Android / Vision on iOS).
/// The plugin asks for the camera permission on first start; a denial or a
/// device without a camera shows [ScannerErrorView] with the manual entry
/// fallback.
class CameraScanScreen extends StatefulWidget {
  const CameraScanScreen({required this.shelf, super.key});

  final bool shelf;

  @override
  State<CameraScanScreen> createState() => _CameraScanScreenState();
}

class _CameraScanScreenState extends State<CameraScanScreen> {
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
        Navigator.of(context).pop(ScannedCode(value));
        return;
      }
    }
  }

  void _manual() {
    if (_done) return;
    _done = true;
    Navigator.of(context).pop(const ManualEntryRequested());
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    return Scaffold(
      backgroundColor: Colors.black,
      appBar: AppBar(
        title: Text(l10n.pickingCameraTitle),
        leading: IconButton(
          tooltip: l10n.pickingCameraClose,
          icon: const Icon(Icons.close),
          onPressed: () => Navigator.of(context).pop(),
        ),
        actions: [
          IconButton(
            tooltip: l10n.pickingCameraTorch,
            icon: const Icon(Icons.flashlight_on_outlined),
            onPressed: () => unawaited(_controller.toggleTorch()),
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
                  ScannerErrorView(error: error, onManual: _manual),
              overlayBuilder: (context, constraints) => IgnorePointer(
                child: Center(
                  child: Container(
                    width: constraints.maxWidth * 0.8,
                    height: constraints.maxWidth * 0.5,
                    decoration: BoxDecoration(
                      border: Border.all(color: Colors.white, width: 3),
                      borderRadius: BorderRadius.circular(BosRadius.xl),
                    ),
                  ),
                ),
              ),
            ),
          ),
          ColoredBox(
            color: BosColors.surfaceCard,
            child: SafeArea(
              top: false,
              child: Padding(
                padding: const EdgeInsets.all(BosSpace.s16),
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.stretch,
                  children: [
                    Text(
                      widget.shelf
                          ? l10n.pickingCameraHintShelf
                          : l10n.pickingCameraHintItem,
                      textAlign: TextAlign.center,
                      style: BosType.bodyLg,
                    ),
                    const SizedBox(height: BosSpace.s12),
                    OutlinedButton.icon(
                      key: const Key('camera-manual'),
                      onPressed: _manual,
                      icon: const Icon(Icons.keyboard_outlined),
                      label: Text(l10n.pickingCameraManual),
                      style: OutlinedButton.styleFrom(
                        minimumSize: const Size.fromHeight(
                          BosSize.primaryAction,
                        ),
                      ),
                    ),
                  ],
                ),
              ),
            ),
          ),
        ],
      ),
    );
  }
}

/// Camera unavailable: why, plus the manual entry fallback.
class ScannerErrorView extends StatelessWidget {
  const ScannerErrorView({
    required this.error,
    required this.onManual,
    super.key,
  });

  final MobileScannerException error;
  final VoidCallback onManual;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final (icon, message) = switch (error.errorCode) {
      MobileScannerErrorCode.permissionDenied => (
        Icons.no_photography_outlined,
        l10n.pickingCameraPermissionDenied,
      ),
      MobileScannerErrorCode.unsupported => (
        Icons.no_photography_outlined,
        l10n.pickingCameraUnsupported,
      ),
      _ => (Icons.error_outline, l10n.pickingCameraError),
    };
    return ColoredBox(
      color: BosColors.surfaceCard,
      child: Center(
        child: Padding(
          padding: const EdgeInsets.all(BosSpace.s24),
          child: Column(
            mainAxisSize: MainAxisSize.min,
            children: [
              Icon(icon, size: 48, color: BosColors.danger.fg),
              const SizedBox(height: BosSpace.s16),
              Semantics(
                liveRegion: true,
                child: Text(
                  message,
                  key: const Key('camera-error'),
                  textAlign: TextAlign.center,
                  style: BosType.bodyLg.copyWith(color: BosColors.textPrimary),
                ),
              ),
              const SizedBox(height: BosSpace.s24),
              FilledButton.icon(
                key: const Key('camera-error-manual'),
                onPressed: onManual,
                icon: const Icon(Icons.keyboard_outlined),
                label: Text(l10n.pickingCameraManual),
                style: FilledButton.styleFrom(
                  minimumSize: const Size(220, BosSize.primaryAction),
                ),
              ),
            ],
          ),
        ),
      ),
    );
  }
}
