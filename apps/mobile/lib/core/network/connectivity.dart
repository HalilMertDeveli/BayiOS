import 'package:connectivity_plus/connectivity_plus.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

/// OS network state changes: `false` = no network interface at all (flight
/// mode, Wi-Fi off), `true` = some network (it may still not reach the API —
/// that is what the probe finds out).
abstract interface class ConnectivitySource {
  Stream<bool> get changes;
}

/// Default (tests, unsupported platforms): never reports a change.
class NoConnectivitySource implements ConnectivitySource {
  const NoConnectivitySource();

  @override
  Stream<bool> get changes => const Stream.empty();
}

/// `connectivity_plus` (Android ConnectivityManager / iOS NWPathMonitor).
class PlatformConnectivitySource implements ConnectivitySource {
  PlatformConnectivitySource([Connectivity? connectivity])
    : _connectivity = connectivity ?? Connectivity();

  final Connectivity _connectivity;

  @override
  Stream<bool> get changes => _connectivity.onConnectivityChanged
      .map((results) => results.any((r) => r != ConnectivityResult.none))
      .distinct();
}

/// Overridden in bootstrap with [PlatformConnectivitySource].
final connectivitySourceProvider = Provider<ConnectivitySource>(
  (ref) => const NoConnectivitySource(),
);

/// While the API is unreachable, probe it this often (null = only on
/// connectivity changes and "Tekrar dene"). Bootstrap sets 15 s; widget tests
/// keep null so no periodic timer outlives a test.
final offlineProbeIntervalProvider = Provider<Duration?>((ref) => null);
