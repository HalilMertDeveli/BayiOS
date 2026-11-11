import 'package:bayios_mobile/core/push/firebase_push.dart';
import 'package:flutter/foundation.dart';
import 'package:flutter_test/flutter_test.dart';

FirebaseConfig? _resolve(
  TargetPlatform platform, {
  String apiKey = 'android-key',
  String iosApiKey = '',
  String androidAppId = '1:1:android:a',
  String iosAppId = '1:1:ios:i',
}) => FirebaseConfig.resolve(
  platform: platform,
  apiKey: apiKey,
  iosApiKey: iosApiKey,
  projectId: 'bayios-test',
  messagingSenderId: '1',
  androidAppId: androidAppId,
  iosAppId: iosAppId,
  iosBundleId: '',
);

void main() {
  test('Android uses the default API key and the Android app id', () {
    final config = _resolve(TargetPlatform.android, iosApiKey: 'ios-key')!;
    expect(config.apiKey, 'android-key');
    expect(config.appId, '1:1:android:a');
  });

  test('iOS uses BAYIOS_FIREBASE_IOS_API_KEY when it is set', () {
    final config = _resolve(TargetPlatform.iOS, iosApiKey: ' ios-key ')!;
    expect(config.apiKey, 'ios-key');
    expect(config.appId, '1:1:ios:i');
  });

  test('iOS falls back to the default API key', () {
    expect(_resolve(TargetPlatform.iOS)!.apiKey, 'android-key');
  });

  test('a missing value for the platform means no push', () {
    expect(_resolve(TargetPlatform.android, androidAppId: ''), isNull);
    expect(_resolve(TargetPlatform.iOS, apiKey: ''), isNull);
    expect(
      _resolve(TargetPlatform.android, apiKey: '', iosApiKey: 'k'),
      isNull,
    );
    expect(_resolve(TargetPlatform.windows), isNull);
  });
}
