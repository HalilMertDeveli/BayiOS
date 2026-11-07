import 'package:flutter_secure_storage/flutter_secure_storage.dart';

/// Remembers the selected tenant per user. The selection is only what is
/// sent as `X-Tenant-Id`; it grants nothing (the API checks the membership on
/// every request, ADR 0002). Keyed by user so a shared device never carries
/// one user's selection over to another.
abstract interface class ActiveTenantStore {
  Future<String?> read(String userId);
  Future<void> write(String userId, String tenantId);
  Future<void> clear(String userId);
}

class SecureActiveTenantStore implements ActiveTenantStore {
  const SecureActiveTenantStore([this._storage = const FlutterSecureStorage()]);

  final FlutterSecureStorage _storage;

  static String _key(String userId) => 'bayios.activeTenant.$userId';

  @override
  Future<String?> read(String userId) async {
    try {
      return await _storage.read(key: _key(userId));
    } on Object {
      return null; // unreadable keystore entry: pick again
    }
  }

  @override
  Future<void> write(String userId, String tenantId) async {
    try {
      await _storage.write(key: _key(userId), value: tenantId);
    } on Object {
      // Selection lasts for this run only.
    }
  }

  @override
  Future<void> clear(String userId) async {
    try {
      await _storage.delete(key: _key(userId));
    } on Object {
      // ignore
    }
  }
}

/// In-memory store (tests, previews).
class MemoryActiveTenantStore implements ActiveTenantStore {
  final Map<String, String> values = {};

  @override
  Future<String?> read(String userId) async => values[userId];

  @override
  Future<void> write(String userId, String tenantId) async =>
      values[userId] = tenantId;

  @override
  Future<void> clear(String userId) async => values.remove(userId);
}
