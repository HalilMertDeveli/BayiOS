import 'package:bayios_mobile/app/feature.dart';
import 'package:bayios_mobile/app/features.dart';
import 'package:bayios_mobile/core/session/access.dart';
import 'package:bayios_mobile/core/session/session_controller.dart';
import 'package:bayios_mobile/core/ui/components.dart';
import 'package:bayios_mobile/l10n/app_localizations.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

/// Features shown to the active tenant's permissions (navigation order).
List<AppFeature> visibleFeatures(List<AppFeature> features, Access access) => [
  for (final f in features)
    if (f.showInNavigation && f.isVisible(access)) f,
];

/// Signed-in shell: the feature screen plus a bottom navigation bar (thumb
/// zone) built from the registered features the permissions allow.
class AppShell extends ConsumerWidget {
  const AppShell({required this.location, required this.child, super.key});

  final String location;
  final Widget child;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final session = ref.watch(sessionControllerProvider);
    final access = session is SessionReady ? session.access : Access.none;
    final items = visibleFeatures(ref.watch(appFeaturesProvider), access);
    final l10n = AppLocalizations.of(context);
    var index = items.indexWhere(
      (f) => location == f.path || location.startsWith('${f.path}/'),
    );
    if (index < 0) index = 0;

    return Scaffold(
      body: child,
      bottomNavigationBar: items.length < 2
          ? null
          : AppBottomNav(
              selectedIndex: index,
              onSelected: (i) => context.go(items[i].path),
              items: [
                for (final f in items)
                  AppNavItem(
                    key: Key('nav-${f.id}'),
                    icon: f.icon,
                    selectedIcon: f.selectedIcon,
                    label: f.label(l10n),
                  ),
              ],
            ),
    );
  }
}
