import 'package:bayios_mobile/core/api/api_client.dart';
import 'package:bayios_mobile/core/api/api_exception.dart';
import 'package:bayios_mobile/core/api/contract.dart';
import 'package:bayios_mobile/core/api/endpoint.dart';
import 'package:bayios_mobile/core/api/idempotency.dart';

/// Push registrations of the signed-in user in a tenant (`/api/v1/devices`,
/// #44). Contract-checked through `CoreEndpoints`.
abstract final class DeviceEndpoints {
  static const register = ApiEndpoint(
    HttpMethod.post,
    '/api/v1/devices',
    idempotent: true,
  );

  static const unregister = ApiEndpoint(
    HttpMethod.delete,
    '/api/v1/devices/{id}',
  );

  static const List<ApiEndpoint> all = [register, unregister];

  static const List<ContractSchema> schemas = [DeviceRegistration.contract];
}

/// `DeviceResponse`: the API never returns the token itself.
class DeviceRegistration {
  const DeviceRegistration({required this.id, required this.platform});

  factory DeviceRegistration.fromJson(Map<String, Object?> json) =>
      DeviceRegistration(
        id: json.requireString('id'),
        platform: json.requireString('platform'),
      );

  static const contract = ContractSchema('DeviceResponse', {
    'id': 'string',
    'platform': 'string',
  });

  final String id;
  final String platform;
}

class DeviceApi {
  const DeviceApi(this._api);

  final ApiClient _api;

  /// Registers (or refreshes) [token] in [tenantId] (the business the session
  /// confirmed — not whatever is active when a retry finally goes out).
  Future<DeviceRegistration> register({
    required String token,
    required String platform,
    required String appVersion,
    required String locale,
    required String tenantId,
    required IdempotencyKey key,
  }) async {
    final response = await _api.send(
      DeviceEndpoints.register,
      idempotencyKey: key,
      tenantId: tenantId,
      body: {
        'token': token,
        'platform': platform,
        'appVersion': appVersion,
        'locale': locale,
      },
      decode: (json) => DeviceRegistration.fromJson(asJsonObject(json)),
    );
    return response.data;
  }

  /// Removes the registration [id] of [tenantId] (also a tenant that is no
  /// longer active). An already removed registration (404) counts as done.
  Future<void> unregister(String id, {required String tenantId}) async {
    try {
      await _api.send<void>(
        DeviceEndpoints.unregister,
        pathParams: {'id': id},
        tenantId: tenantId,
        decode: (_) {},
      );
    } on ApiException catch (error) {
      if (error.kind == ApiErrorKind.notFound) return;
      rethrow;
    }
  }
}
