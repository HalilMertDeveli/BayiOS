import 'package:flutter/foundation.dart';
import 'package:uuid/uuid.dart';

/// Value of the `Idempotency-Key` header (api-strategy §7): one key per user
/// action, reused when the same action is retried, so a request that reached
/// the API before the connection dropped is not applied twice.
@immutable
class IdempotencyKey {
  const IdempotencyKey(this.value);

  /// A fresh random key (UUID v4) for a new user action.
  factory IdempotencyKey.generate() => IdempotencyKey(_uuid.v4());

  static const _uuid = Uuid();

  final String value;

  @override
  bool operator ==(Object other) =>
      other is IdempotencyKey && other.value == value;

  @override
  int get hashCode => value.hashCode;

  @override
  String toString() => 'IdempotencyKey($value)';
}

/// One user action that writes (e.g. "Toplamayı başlat"). Create it when the
/// user taps, call [run] to send and call [run] again for "Tekrar dene": every
/// attempt carries the same key. After success the action is finished; a new
/// tap is a new action (new [IdempotentAction] or [restart]).
///
/// ```dart
/// final start = IdempotentAction<PickingTask>(
///   (key) => api.send(PickingEndpoints.start, idempotencyKey: key, ...),
/// );
/// await start.run();   // fails with a network error
/// await start.run();   // retry: same Idempotency-Key
/// ```
class IdempotentAction<T> {
  IdempotentAction(this._send, {IdempotencyKey Function()? keyFactory})
    : _keyFactory = keyFactory ?? IdempotencyKey.generate {
    _key = _keyFactory();
  }

  final Future<T> Function(IdempotencyKey key) _send;
  final IdempotencyKey Function() _keyFactory;
  late IdempotencyKey _key;
  bool _succeeded = false;

  /// The key every attempt of this action sends.
  IdempotencyKey get key => _key;

  bool get succeeded => _succeeded;

  /// Sends the action; a retry after a failure reuses [key].
  Future<T> run() async {
    final result = await _send(_key);
    _succeeded = true;
    return result;
  }

  /// Starts a *different* user action with this object (e.g. the next scan
  /// after a successful one). Not needed after a 4xx: the API stores only
  /// 2xx results, so a corrected request may keep the key. Reusing a key
  /// whose 2xx is stored with a different body gets 422
  /// `idempotency.key_reused`.
  void restart() {
    _key = _keyFactory();
    _succeeded = false;
  }
}
