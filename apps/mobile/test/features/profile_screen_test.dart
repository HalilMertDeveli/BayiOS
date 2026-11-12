import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import '../helpers/pump.dart';
import 'picking/fake_picking_server.dart';
import 'picking/picking_app.dart';

void main() {
  testWidgets('profile: initials avatar, actions and the UI kit credit', (
    tester,
  ) async {
    await pumpPickingApp(tester, server: FakePickingServer());
    await tester.tap(find.byKey(const Key('nav-profile')));
    await tester.pumpAndSettle();

    expect(find.text('Ayşe Depocu'), findsOneWidget);
    // Real users get initials, never a photo.
    expect(find.text('AD'), findsWidgets);
    expect(find.byKey(const Key('profile-sign-out')), findsOneWidget);
    await expectAccessibleTargets(tester);

    final credit = find.byKey(const Key('profile-about-credit'));
    await tester.scrollUntilVisible(credit, 200);
    expect(find.text(l10n.profileAboutSection), findsOneWidget);
    expect(
      tester.widget<Text>(credit).data,
      'UI tasarımı: Lukáš Marek, '
      "'AI Inventory Management Web App UI Kit' (Figma Community), "
      'CC BY 4.0. Değiştirilerek kullanılmıştır.',
    );
  });
}
