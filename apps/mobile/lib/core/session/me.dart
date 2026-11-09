import 'package:bayios_mobile/core/api/contract.dart';

/// `GET /api/v1/me` → MeResponse.
class Me {
  const Me({
    required this.userId,
    required this.memberships,
    this.email,
    this.fullName,
    this.activeTenant,
  });

  factory Me.fromJson(Map<String, Object?> json) => Me(
    userId: json.requireString('userId'),
    email: json.optString('email'),
    fullName: json.optString('fullName'),
    memberships: json
        .objectList('memberships')
        .map(Membership.fromJson)
        .toList(),
    activeTenant: switch (json.optObject('activeTenant')) {
      final Map<String, Object?> t => ActiveTenant.fromJson(t),
      null => null,
    },
  );

  static const contract = ContractSchema('MeResponse', {
    'userId': 'string',
    'email': 'string?',
    'fullName': 'string?',
    'memberships': 'array',
    'activeTenant': 'object?',
  });

  final String userId;
  final String? email;
  final String? fullName;
  final List<Membership> memberships;

  /// Role and effective permissions of the tenant sent as `X-Tenant-Id`;
  /// null when no header was sent or the tenant is not usable.
  final ActiveTenant? activeTenant;
}

/// MembershipResponse — one tenant the user belongs to.
class Membership {
  const Membership({
    required this.tenantId,
    required this.tenantName,
    required this.tenantSlug,
    required this.role,
    required this.status,
    required this.tenantStatus,
  });

  factory Membership.fromJson(Map<String, Object?> json) => Membership(
    tenantId: json.requireString('tenantId'),
    tenantName: json.requireString('tenantName'),
    tenantSlug: json.requireString('tenantSlug'),
    role: json.requireString('role'),
    status: json.requireString('status'),
    tenantStatus: json.requireString('tenantStatus'),
  );

  static const contract = ContractSchema('MembershipResponse', {
    'tenantId': 'string',
    'tenantName': 'string',
    'tenantSlug': 'string',
    'role': 'string',
    'status': 'string',
    'tenantStatus': 'string',
  });

  final String tenantId;
  final String tenantName;
  final String tenantSlug;

  /// Role id (`warehouse_staff`, …) — labels only, never authorization.
  final String role;

  /// Membership status; only `active` memberships can be used.
  final String status;

  /// `active`, `suspended` or `closed` (#111).
  final String tenantStatus;

  bool get isActiveMembership => status == 'active';
  bool get isTenantUsable => tenantStatus == 'active';
}

/// ActiveTenantResponse.
class ActiveTenant {
  const ActiveTenant({
    required this.tenantId,
    required this.role,
    required this.permissions,
    this.entitlements,
  });

  factory ActiveTenant.fromJson(Map<String, Object?> json) => ActiveTenant(
    tenantId: json.requireString('tenantId'),
    role: json.requireString('role'),
    permissions: json
        .objectList('permissions')
        .map(PermissionGrant.fromJson)
        .toList(),
    entitlements: switch (json.optObject('entitlements')) {
      final Map<String, Object?> e => TenantEntitlements.fromJson(e),
      null => null,
    },
  );

  static const contract = ContractSchema('ActiveTenantResponse', {
    'tenantId': 'string',
    'role': 'string',
    'permissions': 'array',
    'entitlements': 'object?',
  });

  final String tenantId;
  final String role;
  final List<PermissionGrant> permissions;
  final TenantEntitlements? entitlements;
}

/// `all`, `conditional` or `own`; unknown values (newer API) are kept as
/// [unknown] and treated like a restricted grant.
enum PermissionScope { all, conditional, own, unknown }

/// PermissionGrantResponse — one effective permission.
class PermissionGrant {
  const PermissionGrant({
    required this.permission,
    required this.scope,
    this.condition,
  });

  factory PermissionGrant.fromJson(Map<String, Object?> json) =>
      PermissionGrant(
        permission: json.requireString('permission'),
        scope: switch (json.optString('scope')) {
          'all' => PermissionScope.all,
          'conditional' => PermissionScope.conditional,
          'own' => PermissionScope.own,
          _ => PermissionScope.unknown,
        },
        condition: json.optString('condition'),
      );

  static const contract = ContractSchema('PermissionGrantResponse', {
    'permission': 'string',
    'scope': 'string',
    'condition': 'string?',
  });

  final String permission;
  final PermissionScope scope;
  final String? condition;
}

/// TenantEntitlementsResponse (display only; the API enforces).
class TenantEntitlements {
  const TenantEntitlements({
    required this.plan,
    required this.status,
    required this.features,
  });

  factory TenantEntitlements.fromJson(Map<String, Object?> json) =>
      TenantEntitlements(
        plan: json.requireString('plan'),
        status: json.requireString('status'),
        features: json.stringList('features'),
      );

  static const contract = ContractSchema('TenantEntitlementsResponse', {
    'plan': 'string',
    'status': 'string',
    'features': 'array',
  });

  final String plan;
  final String status;
  final List<String> features;

  bool has(String feature) => features.contains(feature);
}
