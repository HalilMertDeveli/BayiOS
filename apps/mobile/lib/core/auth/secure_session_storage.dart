import 'package:flutter_secure_storage/flutter_secure_storage.dart';
import 'package:supabase_flutter/supabase_flutter.dart';

/// Persists the Supabase session (access + refresh token) in the platform
/// keystore — Android Keystore-backed encryption, iOS Keychain — instead of
/// supabase_flutter's default SharedPreferences (threat-model §3.2 S).
class SecureSessionStorage extends LocalStorage {
  SecureSessionStorage({FlutterSecureStorage? storage, String? key})
    : _storage = storage ?? defaultStorage,
      _key = key ?? 'bayios.supabase.session';

  static const defaultStorage = FlutterSecureStorage(
    iOptions: IOSOptions(
      accessibility: KeychainAccessibility.first_unlock_this_device,
    ),
  );

  final FlutterSecureStorage _storage;
  final String _key;

  @override
  Future<void> initialize() async {}

  @override
  Future<bool> hasAccessToken() => _storage.containsKey(key: _key);

  @override
  Future<String?> accessToken() => _storage.read(key: _key);

  @override
  Future<void> persistSession(String persistSessionString) =>
      _storage.write(key: _key, value: persistSessionString);

  @override
  Future<void> removePersistedSession() => _storage.delete(key: _key);
}

/// PKCE verifier storage in the keystore too (unused by password sign-in,
/// but keeps every auth artefact out of SharedPreferences).
class SecureGotrueAsyncStorage extends GotrueAsyncStorage {
  const SecureGotrueAsyncStorage([
    this._storage = SecureSessionStorage.defaultStorage,
  ]);

  final FlutterSecureStorage _storage;

  @override
  Future<String?> getItem({required String key}) => _storage.read(key: key);

  @override
  Future<void> removeItem({required String key}) => _storage.delete(key: key);

  @override
  Future<void> setItem({required String key, required String value}) =>
      _storage.write(key: key, value: value);
}
