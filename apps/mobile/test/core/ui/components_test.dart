import 'package:bayios_mobile/core/theme/app_colors.dart';
import 'package:bayios_mobile/core/theme/app_icon.dart';
import 'package:bayios_mobile/core/ui/components.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import '../../helpers/pump.dart';

Finder findIcon(AppIconData icon) =>
    find.byWidgetPredicate((w) => w is AppIcon && w.icon == icon);

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
      expect(findIcon(AppIcons.check), findsOneWidget);
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
      expect(findIcon(AppIcons.minus4), findsOneWidget);
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
      expect(findIcon(AppIcons.check), findsNWidgets(3));
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
      expect(background(tester), AppColors.light.dangerSolid);
      final title = tester.widget<Text>(find.text('Yanlış ürün'));
      expect(title.style?.color, AppColors.light.onSolid);
      expect(findIcon(AppIcons.closeRemove), findsOneWidget);
      await expectAccessibleTargets(tester);
    });

    testWidgets('success: solid green block', (tester) async {
      await pumpBody(
        tester,
        const FeedbackBanner(tone: FeedbackTone.success, title: 'Okutuldu'),
      );
      await tester.pumpAndSettle();
      expect(background(tester), AppColors.light.successSolid);
      expect(findIcon(AppIcons.checkCircle1), findsOneWidget);
      await expectAccessibleTargets(tester);
    });

    testWidgets('info stays tinted (not alarming)', (tester) async {
      await pumpBody(
        tester,
        const FeedbackBanner(tone: FeedbackTone.info, title: 'Hazır'),
      );
      await tester.pumpAndSettle();
      expect(background(tester), AppColors.light.info.bg);
    });
  });

  testWidgets('EmptyState: title, hint and action', (tester) async {
    await pumpBody(
      tester,
      EmptyState(
        icon: AppIcons.packageOpen,
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

  group('initials (same rule as the web)', () {
    test('first and last word, Turkish upper case', () {
      expect(initials('Ayşe Yılmaz'), 'AY');
      expect(initials('ilker ışık'), 'İI');
      expect(initials('Örnek Müdür (sentetik)'), 'ÖM');
      expect(initials('depo@ornek.test'), 'D');
      expect(initials('Mehmet'), 'M');
    });

    testWidgets('AppAvatar shows initials and is decorative', (tester) async {
      final handle = tester.ensureSemantics();
      await pumpBody(tester, const AppAvatar(name: 'Ayşe Depocu'));
      expect(find.text('AD'), findsOneWidget);
      expect(find.bySemanticsLabel('AD'), findsNothing);
      handle.dispose();
    });
  });

  testWidgets('StatusChip: label, tone colours and optional icon', (
    tester,
  ) async {
    await pumpBody(
      tester,
      const StatusChip('Acil', Tone.danger, icon: AppIcons.bolt),
    );
    expect(find.text('Acil'), findsOneWidget);
    final text = tester.widget<Text>(find.text('Acil'));
    expect(text.style?.color, AppColors.light.danger.fg);
    expect(findIcon(AppIcons.bolt), findsOneWidget);
  });
}
