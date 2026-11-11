import 'dart:async';

import 'package:bayios_mobile/core/push/push_service.dart';
import 'package:firebase_core/firebase_core.dart';
import 'package:firebase_messaging/firebase_messaging.dart';
import 'package:flutter/foundation.dart';

/// Firebase client configuration from `--dart-define`s (`env/<env>.json`,
/// git-ignored; names in env/dev.example.json). These are Firebase *client*
/// identifiers — not secrets, but environment-specific, so nothing is
/// committed and no `google-services.json` / `GoogleService-Info.plist` is
/// needed: Firebase is initialised in Dart. Missing values → no push.
class FirebaseConfig {
  const FirebaseConfig({
    required this.apiKey,
    required this.appId,
    required this.messagingSenderId,
    required this.projectId,
    this.iosBundleId,
  });

  /// Null unless every value for the current platform is present.
  static FirebaseConfig? fromDefines({TargetPlatform? platform}) => resolve(
    platform: platform ?? defaultTargetPlatform,
    apiKey: const String.fromEnvironment('BAYIOS_FIREBASE_API_KEY'),
    iosApiKey: const String.fromEnvironment('BAYIOS_FIREBASE_IOS_API_KEY'),
    projectId: const String.fromEnvironment('BAYIOS_FIREBASE_PROJECT_ID'),
    messagingSenderId: const String.fromEnvironment(
      'BAYIOS_FIREBASE_MESSAGING_SENDER_ID',
    ),
    androidAppId: const String.fromEnvironment(
      'BAYIOS_FIREBASE_ANDROID_APP_ID',
    ),
    iosAppId: const String.fromEnvironment('BAYIOS_FIREBASE_IOS_APP_ID'),
    iosBundleId: const String.fromEnvironment('BAYIOS_FIREBASE_IOS_BUNDLE_ID'),
  );

  /// Firebase creates one client API key per registered app: [apiKey] is the
  /// Android app's key and the default; [iosApiKey], when set, is used on iOS.
  static FirebaseConfig? resolve({
    required TargetPlatform platform,
    required String apiKey,
    required String iosApiKey,
    required String projectId,
    required String messagingSenderId,
    required String androidAppId,
    required String iosAppId,
    required String iosBundleId,
  }) {
    final appId = switch (platform) {
      TargetPlatform.android => androidAppId,
      TargetPlatform.iOS => iosAppId,
      _ => '',
    };
    final key = platform == TargetPlatform.iOS && iosApiKey.trim().isNotEmpty
        ? iosApiKey
        : apiKey;
    final values = [key, projectId, messagingSenderId, appId];
    if (values.any((v) => v.trim().isEmpty)) return null;
    return FirebaseConfig(
      apiKey: key.trim(),
      appId: appId.trim(),
      messagingSenderId: messagingSenderId.trim(),
      projectId: projectId.trim(),
      iosBundleId: iosBundleId.trim().isEmpty ? null : iosBundleId.trim(),
    );
  }

  final String apiKey;
  final String appId;
  final String messagingSenderId;
  final String projectId;
  final String? iosBundleId;

  FirebaseOptions toOptions() => FirebaseOptions(
    apiKey: apiKey,
    appId: appId,
    messagingSenderId: messagingSenderId,
    projectId: projectId,
    iosBundleId: iosBundleId,
  );
}

/// [PushService] over `firebase_messaging`. Created only when
/// [FirebaseConfig.fromDefines] found a configuration.
class FirebasePushService implements PushService {
  FirebasePushService._(this._messaging);

  /// Initialises Firebase (notifications only — no other Firebase product).
  static Future<FirebasePushService> start(FirebaseConfig config) async {
    await Firebase.initializeApp(options: config.toOptions());
    final messaging = FirebaseMessaging.instance;
    // iOS: in the foreground the app shows its own banner instead.
    await messaging.setForegroundNotificationPresentationOptions(
      sound: true,
      badge: true,
    );
    return FirebasePushService._(messaging);
  }

  final FirebaseMessaging _messaging;

  @override
  bool get available => true;

  @override
  Future<PushPermission> requestPermission() async {
    final settings = await _messaging.requestPermission();
    return switch (settings.authorizationStatus) {
      AuthorizationStatus.authorized ||
      AuthorizationStatus.provisional => PushPermission.granted,
      AuthorizationStatus.denied ||
      AuthorizationStatus.deniedPermanently => PushPermission.denied,
      AuthorizationStatus.notDetermined => PushPermission.notDetermined,
    };
  }

  @override
  // The API sends FCM HTTP v1 messages to registration tokens
  // (`message.token`); the Installation-ID direct send (register() /
  // onRegistered) is not adopted yet, so the token API stays on purpose.
  // ignore: deprecated_member_use
  Future<String?> token() => _messaging.getToken();

  @override
  // Registration-token rotation (see token()).
  // ignore: deprecated_member_use
  Stream<String> get tokenRefreshes => _messaging.onTokenRefresh;

  @override
  Stream<PushTap> get taps => FirebaseMessaging.onMessageOpenedApp
      .map((m) => PushTap.fromData(m.data))
      .where((t) => t != null)
      .cast<PushTap>();

  @override
  Future<PushTap?> initialTap() async {
    final message = await _messaging.getInitialMessage();
    return message == null ? null : PushTap.fromData(message.data);
  }

  @override
  Stream<ForegroundPush> get foregroundMessages =>
      FirebaseMessaging.onMessage.map(
        (m) => ForegroundPush(
          title: m.notification?.title ?? '',
          body: m.notification?.body ?? '',
          tap: PushTap.fromData(m.data),
        ),
      );

  @override
  // ignore: deprecated_member_use, the registration-token API (see token()).
  Future<void> deleteToken() => _messaging.deleteToken();
}
