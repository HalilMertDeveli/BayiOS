import 'dart:async';

import 'package:bayios_mobile/core/api/api_exception.dart';
import 'package:bayios_mobile/core/api/problem_details.dart';
import 'package:bayios_mobile/core/auth/auth_controller.dart';
import 'package:bayios_mobile/core/network/connectivity.dart';
import 'package:bayios_mobile/core/providers.dart';
import 'package:bayios_mobile/core/ui/widgets.dart';
import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_riverpod/misc.dart';
import 'package:flutter_test/flutter_test.dart';

import '../../helpers/fakes.dart';
import '../../helpers/pump.dart';
import '../../helpers/push_fakes.dart';

class FakeConnectivity implements ConnectivitySource {
  final _controller = StreamController<bool>.broadcast();

  void emit({required bool online}) => _controller.add(online);

  @override
  Stream<bool> get changes => _controller.stream;
}

/// Connection errors (#44, third criterion): the offline state is visible on
/// every screen, the API is probed when the network comes back (and
/// periodically while it is away), and failed loads re-fetch by themselves
/// once it answers. Writes are not resent automatically.
void main() {
  late FakeAdapter adapter;
  late FakeConnectivity connectivity;
  late int retries;

  setUp(() {
    adapter = FakeAdapter();
    connectivity = FakeConnectivity();
    retries = 0;
  });

  List<Override> overrides({Duration? probeEvery}) => [
    appEnvProvider.overrideWithValue(testEnv),
    authRepositoryProvider.overrideWithValue(FakeAuthRepository()),
    dioProvider.overrideWithValue(fakeDio(adapter)),
    connectivitySourceProvider.overrideWithValue(connectivity),
    offlineProbeIntervalProvider.overrideWithValue(probeEvery),
  ];

  Widget screen(Object error) => Scaffold(
    body: Column(
      children: [
        const OfflineBanner(),
        Expanded(
          child: ErrorView(error: error, onRetry: () => retries++),
        ),
      ],
    ),
  );

  FakeReply meta(RequestOptions request) {
    expect(request.path, '/api/v1/meta');
    return FakeReply(200, json: {'name': 'BayiOS'});
  }

  testWidgets('no network: banner; network back → probe → banner gone and '
      'the failed load re-fetches once', (tester) async {
    await pumpScreen(
      tester,
      screen(ApiException.network()),
      overrides: overrides(),
    );
    expect(find.text(l10n.offlineBanner), findsNothing);

    connectivity.emit(online: false);
    await tester.pumpAndSettle();
    expect(find.text(l10n.offlineBanner), findsOneWidget);
    expect(find.byKey(const Key('offline-banner-retry')), findsOneWidget);
    await expectAccessibleTargets(tester);
    expect(retries, 0);

    adapter.enqueue(meta);
    connectivity.emit(online: true);
    await tester.pumpAndSettle();
    expect(adapter.requests.single.path, '/api/v1/meta');
    expect(find.text(l10n.offlineBanner), findsNothing);
    expect(retries, 1);
  });

  testWidgets('network back but the API still unreachable: banner stays, '
      'nothing re-fetched', (tester) async {
    await pumpScreen(
      tester,
      screen(ApiException.network(timeout: true)),
      overrides: overrides(),
    );
    connectivity.emit(online: false);
    await tester.pumpAndSettle();

    adapter.enqueue((_) => FakeReply.fail(DioExceptionType.connectionError));
    connectivity.emit(online: true);
    await tester.pumpAndSettle();
    expect(find.text(l10n.offlineBanner), findsOneWidget);
    expect(retries, 0);
  });

  testWidgets('while offline the API is probed periodically', (tester) async {
    await pumpScreen(
      tester,
      screen(ApiException.network()),
      overrides: overrides(probeEvery: const Duration(seconds: 15)),
    );
    adapter.enqueue((_) => FakeReply.fail(DioExceptionType.connectionError));
    connectivity.emit(online: false);
    await tester.pumpAndSettle();

    await tester.pump(const Duration(seconds: 15));
    await tester.pumpAndSettle();
    expect(adapter.requests, hasLength(1), reason: 'first probe failed');
    expect(find.text(l10n.offlineBanner), findsOneWidget);

    adapter.enqueue(meta);
    await tester.pump(const Duration(seconds: 15));
    await tester.pumpAndSettle();
    expect(adapter.requests, hasLength(2));
    expect(find.text(l10n.offlineBanner), findsNothing);
    expect(retries, 1);

    // Reachable again: no further probes.
    await tester.pump(const Duration(seconds: 60));
    expect(adapter.requests, hasLength(2));
  });

  testWidgets('"Tekrar dene" on the banner probes at once', (tester) async {
    await pumpScreen(
      tester,
      screen(ApiException.network()),
      overrides: overrides(),
    );
    connectivity.emit(online: false);
    await tester.pumpAndSettle();
    adapter.enqueue(meta);
    await tester.tap(find.byKey(const Key('offline-banner-retry')));
    await tester.pumpAndSettle();
    expect(find.text(l10n.offlineBanner), findsNothing);
    expect(retries, 1);
  });

  testWidgets('a server error is not re-fetched on reconnect (user retries)', (
    tester,
  ) async {
    await pumpScreen(
      tester,
      screen(
        ApiException(
          status: 500,
          problem: const ProblemDetails(code: 'server.error'),
        ),
      ),
      overrides: overrides(),
    );
    final container = ProviderScope.containerOf(
      tester.element(find.byType(ErrorView)),
    );
    container.read(networkStatusProvider.notifier)
      ..markUnreachable()
      ..markReachable();
    await tester.pumpAndSettle();
    expect(retries, 0);
    expect(find.text(l10n.retry), findsOneWidget);
  });
}
