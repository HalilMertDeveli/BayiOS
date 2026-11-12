import 'package:bayios_mobile/core/api/contract.dart';
import 'package:bayios_mobile/core/api/endpoint.dart';
import 'package:bayios_mobile/core/session/access.dart';
import 'package:bayios_mobile/core/theme/app_icon.dart';
import 'package:bayios_mobile/l10n/app_localizations.dart';
import 'package:flutter/widgets.dart';
import 'package:go_router/go_router.dart';

bool _always(Access _) => true;

/// A top-level area of the signed-in app (Pano, Görevler, Sayım, Profil…).
///
/// A feature lives in `lib/features/<name>/` and exposes one [AppFeature]
/// value (`<name>_feature.dart`). Registering it is one line in
/// `lib/app/features.dart`; that gives it:
/// - a route at [path] inside the signed-in shell (plus nested [routes]),
/// - a bottom-navigation destination when [showInNavigation] is true and
///   [isVisible] allows it for the active tenant's permissions,
/// - a route guard: users whose permissions hide it are sent home,
/// - contract checks of its [endpoints] and [schemas] (contract_test.dart).
class AppFeature {
  const AppFeature({
    required this.id,
    required this.path,
    required this.label,
    required this.icon,
    required this.builder,
    this.selectedIcon,
    this.routes = const [],
    this.isVisible = _always,
    this.showInNavigation = true,
    this.endpoints = const [],
    this.schemas = const [],
  });

  /// Stable id (used for widget keys, e.g. `nav-dashboard`).
  final String id;

  /// Turkish, absolute path, e.g. `/gorevler`.
  final String path;
  final String Function(AppLocalizations l10n) label;
  final AppIconData icon;
  final AppIconData? selectedIcon;
  final Widget Function(BuildContext context, GoRouterState state) builder;

  /// Sub-routes, relative to [path] (e.g. `GoRoute(path: ':id', …)`).
  final List<RouteBase> routes;

  /// Visibility from the effective permissions of `GET /me` — never from
  /// role names. Visibility only: the API authorizes every call.
  final bool Function(Access access) isVisible;
  final bool showInNavigation;

  /// API operations this feature calls (contract-checked).
  final List<ApiEndpoint> endpoints;

  /// Response schemas its models read (contract-checked).
  final List<ContractSchema> schemas;
}
