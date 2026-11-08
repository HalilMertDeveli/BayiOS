import 'package:bayios_mobile/core/api/api_client.dart';
import 'package:bayios_mobile/core/api/contract.dart';
import 'package:bayios_mobile/core/api/endpoint.dart';
import 'package:bayios_mobile/core/push/device_api.dart';
import 'package:bayios_mobile/core/session/me.dart';

/// Endpoints the core uses (checked against the contract).
abstract final class CoreEndpoints {
  /// Profile + memberships; optional `X-Tenant-Id` selects whose effective
  /// permissions come back in `activeTenant`.
  static const me = ApiEndpoint(
    HttpMethod.get,
    '/api/v1/me',
    tenant: TenantScope.optional,
  );

  /// Anonymous reachability probe for the offline banner.
  static const meta = ApiEndpoint(
    HttpMethod.get,
    '/api/v1/meta',
    tenant: TenantScope.none,
    authenticated: false,
  );

  static const List<ApiEndpoint> all = [me, meta, ...DeviceEndpoints.all];

  static const List<ContractSchema> schemas = [
    Me.contract,
    Membership.contract,
    ActiveTenant.contract,
    PermissionGrant.contract,
    TenantEntitlements.contract,
    ...DeviceEndpoints.schemas,
  ];
}

class IdentityApi {
  const IdentityApi(this._api);

  final ApiClient _api;

  Future<Me> getMe({String? tenantId}) async {
    final response = await _api.send(
      CoreEndpoints.me,
      tenantId: tenantId,
      decode: (json) => Me.fromJson(asJsonObject(json)),
    );
    return response.data;
  }

  /// True when the API answered (any status).
  Future<void> ping() async {
    await _api.send<void>(CoreEndpoints.meta, decode: (_) {});
  }
}
