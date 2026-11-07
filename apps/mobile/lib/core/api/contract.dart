/// The fields a hand-written model reads from one schema of
/// packages/contracts/openapi.json. test/contract/contract_test.dart fails
/// when a field is missing, has another JSON type, or may be null / absent
/// while the model treats it as always present — i.e. on contract drift.
///
/// Field types: `string`, `integer`, `number`, `boolean`, `array`, `object`;
/// a trailing `?` means the model tolerates null or absence.
///
/// ```dart
/// static const contract = ContractSchema('MembershipResponse', {
///   'tenantId': 'string',
///   'tenantName': 'string',
/// });
/// ```
class ContractSchema {
  const ContractSchema(this.name, this.fields);

  /// Name under `components.schemas`.
  final String name;
  final Map<String, String> fields;
}

/// JSON helpers for tolerant model parsing.
extension JsonMapRead on Map<String, Object?> {
  String requireString(String key) {
    final value = this[key];
    if (value is String) return value;
    throw FormatException('Expected string "$key"', value);
  }

  String? optString(String key) {
    final value = this[key];
    return value is String ? value : null;
  }

  List<Map<String, Object?>> objectList(String key) {
    final value = this[key];
    if (value is! List<Object?>) return const [];
    return value.whereType<Map<String, Object?>>().toList();
  }

  List<String> stringList(String key) {
    final value = this[key];
    if (value is! List<Object?>) return const [];
    return value.whereType<String>().toList();
  }

  Map<String, Object?>? optObject(String key) {
    final value = this[key];
    return value is Map<String, Object?> ? value : null;
  }
}

/// Casts a decoded JSON body to an object or throws [FormatException].
Map<String, Object?> asJsonObject(Object? json) {
  if (json is Map<String, Object?>) return json;
  throw FormatException('Expected a JSON object', json);
}
