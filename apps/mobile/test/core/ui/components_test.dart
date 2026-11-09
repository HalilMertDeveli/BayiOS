import 'package:bayios_mobile/core/ui/components.dart';
import 'package:bayios_mobile/core/ui/tokens.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import '../../helpers/pump.dart';

void main() {
  const steps = [
    ScanStep('Raf okut', detail: 'A-01-01'),
    ScanStep('Ürün okut', detail: 'FLT-001'),
    ScanStep('Adet'),
  ];

  Future<void> pumpBody(WidgetTester tester, Widget child) => pumpScreen(
    tester,
    Scaffold(
      body: Padding(padding: const EdgeInsets.all(16), child: child),
    ),
  );

  group('ScanStepIndicator', () {
    testWidgets('announces the current step as "Adım n / N: label"', (
      tester,
    ) async {
      final handle = tester.ensureSemantics();
      await pumpBody(
        tester,
        const ScanStepIndicator(key: Key('steps'), steps: steps, current: 1),
      );
      expect(
        tester.getSemantics(find.byKey(const Key('steps'))),
        matchesSemantics(
          label: l10n.scanStepSemantics(2, 3, 'Ürün okut'),
          isLiveRegion: true,
        ),
      );
      // Every label stays visible; the current number is shown, the done
      // step shows a check instead of its number.
      expect(find.text('Raf okut'), findsOneWidget);
      expect(find.text('Ürün okut'), findsOneWidget);
      expect(find.text('Adet'), findsOneWidget);
      expect(find.text('1'), findsNothing);
      expect(find.byIcon(Icons.check), findsOneWidget);
      expect(find.text('2'), findsOneWidget);
      expect(find.text('3'), findsOneWidget);
      await expectAccessibleTargets(tester);
      handle.dispose();
    });

    testWidgets('a skipped step (no shelf tracking) is marked, not numbered', (
      tester,
    ) async {
      await pumpBody(
        tester,
        const ScanStepIndicator(
          steps: [ScanStep('Raf okut', skipped: true), ScanStep('Ürün okut')],
          current: 1,
        ),
      );
      expect(find.byIcon(Icons.remove), findsOneWidget);
      expect(find.text('1'), findsNothing);
    });

    testWidgets('past the last step: "all done"', (tester) async {
      final handle = tester.ensureSemantics();
      await pumpBody(
        tester,
        const ScanStepIndicator(key: Key('steps'), steps: steps, current: 3),
      );
      expect(
        tester.getSemantics(find.byKey(const Key('steps'))).label,
        l10n.scanStepAllDone,
      );
      expect(find.byIcon(Icons.check), findsNWidgets(3));
      handle.dispose();
    });
  });

  group('FeedbackBanner', () {
    Color background(WidgetTester tester) {
      final box = tester.widget<DecoratedBox>(
        find
            .descendant(
              of: find.byType(FeedbackBanner),
              matching: find.byType(DecoratedBox),
            )
            .first,
      );
      return (box.decoration as BoxDecoration).color!;
    }

    testWidgets('error: solid danger block with white text', (tester) async {
      await pumpBody(
        tester,
        const FeedbackBanner(
          tone: FeedbackTone.error,
          title: 'Yanlış ürün',
          text: 'Bu satırın ürünü değil.',
        ),
      );
      await tester.pumpAndSettle();
      expect(background(tester), BosColors.dangerSolid);
      final title = tester.widget<Text>(find.text('Yanlış ürün'));
      expect(title.style?.color, BosColors.textInverse);
      expect(find.byIcon(Icons.cancel), findsOneWidget);
      await expectAccessibleTargets(tester);
    });

    testWidgets('success: solid green block', (tester) async {
      await pumpBody(
        tester,
        const FeedbackBanner(tone: FeedbackTone.success, title: 'Okutuldu'),
      );
      await tester.pumpAndSettle();
      expect(background(tester), BosColors.success.fg);
      expect(find.byIcon(Icons.check_circle), findsOneWidget);
      await expectAccessibleTargets(tester);
    });

    testWidgets('info stays tinted (not alarming)', (tester) async {
      await pumpBody(
        tester,
        const FeedbackBanner(tone: FeedbackTone.info, title: 'Hazır'),
      );
      await tester.pumpAndSettle();
      expect(background(tester), BosColors.info.bg);
    });
  });

  testWidgets('EmptyState: title, hint and action', (tester) async {
    await pumpBody(
      tester,
      EmptyState(
        icon: Icons.inbox_outlined,
        title: 'Görev yok',
        body: 'Yeni görevler burada görünür.',
        action: FilledButton(onPressed: () {}, child: const Text('Yenile')),
      ),
    );
    expect(find.text('Görev yok'), findsOneWidget);
    expect(find.text('Yeni görevler burada görünür.'), findsOneWidget);
    expect(find.text('Yenile'), findsOneWidget);
    await expectAccessibleTargets(tester);
  });

  testWidgets('BrandMark is decorative (hidden from screen readers)', (
    tester,
  ) async {
    final handle = tester.ensureSemantics();
    await pumpBody(tester, const BrandMark());
    expect(find.bySemanticsLabel(RegExp('.+')), findsNothing);
    handle.dispose();
  });
}
