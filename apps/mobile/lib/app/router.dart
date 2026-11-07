import 'package:bayios_mobile/app/feature.dart';
import 'package:bayios_mobile/app/features.dart';
import 'package:bayios_mobile/app/shell.dart';
import 'package:bayios_mobile/core/session/session_controller.dart';
import 'package:bayios_mobile/features/auth/sign_in_screen.dart';
import 'package:bayios_mobile/features/tenant/no_membership_screen.dart';
import 'package:bayios_mobile/features/tenant/session_loading_screen.dart';
import 'package:bayios_mobile/features/tenant/tenant_picker_screen.dart';
import 'package:flutter/foundation.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

/// Paths outside the feature shell (Turkish, like the web's `/giris`).
abstract final class AppRoutes {
  static const signIn = '/giris';
  static const loading = '/yukleniyor';
  static const tenantPicker = '/isletme-sec';
  static const noMembership = '/isletme-yok';

  static const Set<String> _gate = {
    signIn,
    loading,
    tenantPicker,
    noMembership,
  };
}

/// Where the session state sends [location] (null = stay). Pure, so the
/// guards are unit-tested without a widget tree.
String? redirectFor(
  SessionState session,
  String location,
  List<AppFeature> features,
) {
  final String target;
  switch (session) {
    case SessionSignedOut():
      target = AppRoutes.signIn;
    case SessionLoading() || SessionFailed():
      target = AppRoutes.loading;
    case SessionNoMembership():
      target = AppRoutes.noMembership;
    case SessionNeedsTenant():
      target = AppRoutes.tenantPicker;
    case final SessionReady ready:
      final visible = features.where((f) => f.isVisible(ready.access));
      final home = visible.isEmpty ? null : visible.first.path;
      if (home == null) {
        target = AppRoutes.loading;
      } else if (AppRoutes._gate.contains(location)) {
        target = home;
      } else {
        final owner = _featureFor(location, features);
        // Unknown path or a feature these permissions hide → home.
        target = owner != null && owner.isVisible(ready.access)
            ? location
            : home;
      }
  }
  return target == location ? null : target;
}

AppFeature? _featureFor(String location, List<AppFeature> features) {
  for (final f in features) {
    if (location == f.path || location.startsWith('${f.path}/')) return f;
  }
  return null;
}

final routerProvider = Provider<GoRouter>((ref) {
  final features = ref.watch(appFeaturesProvider);
  final session = ValueNotifier<SessionState>(
    ref.read(sessionControllerProvider),
  );
  ref.listen(sessionControllerProvider, (_, next) => session.value = next);

  final router = GoRouter(
    initialLocation: AppRoutes.loading,
    refreshListenable: session,
    redirect: (context, state) =>
        redirectFor(session.value, state.matchedLocation, features),
    routes: [
      GoRoute(
        path: AppRoutes.signIn,
        builder: (context, state) => const SignInScreen(),
      ),
      GoRoute(
        path: AppRoutes.loading,
        builder: (context, state) => const SessionLoadingScreen(),
      ),
      GoRoute(
        path: AppRoutes.tenantPicker,
        builder: (context, state) => const TenantPickerScreen(),
      ),
      GoRoute(
        path: AppRoutes.noMembership,
        builder: (context, state) => const NoMembershipScreen(),
      ),
      ShellRoute(
        builder: (context, state, child) =>
            AppShell(location: state.matchedLocation, child: child),
        routes: [
          for (final f in features)
            GoRoute(path: f.path, builder: f.builder, routes: f.routes),
        ],
      ),
    ],
  );
  ref.onDispose(() {
    router.dispose();
    session.dispose();
  });
  return router;
});
