import 'package:bayios_mobile/core/session/me.dart';

/// Effective permissions of the active membership exactly as `GET /me`
/// returns them (`activeTenant.permissions`). The app keeps no
/// role → permission table: every "may I show this?" goes through here.
/// Visibility only — the API checks every permission again.
///
/// Permission ids: docs/product/roles-permissions.md §3 (e.g. `tasks.pick`,
/// `inventory.read`, `inventory.receive`).
class Access {
  Access(Iterable<PermissionGrant> grants)
    : _byPermission = {for (final g in grants) g.permission: g};

  static final none = Access(const []);

  final Map<String, PermissionGrant> _byPermission;

  /// Granted unconditionally (scope `all`).
  bool can(String permission) =>
      _byPermission[permission]?.scope == PermissionScope.all;

  /// Granted in any scope (`all`, `conditional`, `own`): show the screen and
  /// let the API filter or check records one by one (handle 403/404).
  bool canSome(String permission) => _byPermission.containsKey(permission);

  /// True when at least one of [permissions] is granted in any scope.
  bool canAny(Iterable<String> permissions) => permissions.any(canSome);

  PermissionScope? scope(String permission) => _byPermission[permission]?.scope;

  String? condition(String permission) => _byPermission[permission]?.condition;

  Iterable<String> get permissions => _byPermission.keys;
}
